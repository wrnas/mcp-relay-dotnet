using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using System.Data.Common; // 👈 核心命名空间
using System.Text.RegularExpressions;

namespace RelayMcp.Controllers
{
    [ApiController]
    [Route("sql")]
    public class SqlController : ControllerBase
    {
        private readonly IConfiguration _config;
        private readonly IMemoryCache _cache;

        public SqlController(IConfiguration config, IMemoryCache cache)
        {
            _config = config;
            _cache = cache;
        }

        public class SqlRequest
        {
            public string DbAlias { get; set; }
            public string Sql { get; set; }
            public string? ConfirmToken { get; set; }
        }

        [HttpPost("execute")]
        public async Task<IActionResult> ExecuteSql([FromBody] SqlRequest req)
        {
            var dbConfig = _config.GetSection($"DatabaseSettings:Connections:{req.DbAlias}");
            var providerName = dbConfig["Provider"];
            var connStr = dbConfig["ConnectionString"];

            if (string.IsNullOrEmpty(connStr) || string.IsNullOrEmpty(providerName))
                return BadRequest(ResponseHelper.Fail("数据库别名或驱动配置不存在", 400, "INVALID_PARAMS"));

            // 1. 提取 SQL 的核心操作词（比如 SELECT, UPDATE, EXEC, CALL, 或者存储过程名）
            string opType = GetSqlOperationType(req.Sql);

            // 2. 安全放行：SELECT 和 WITH (CTE 查询)
            if (opType == "SELECT" || opType == "WITH")
            {
                return await ExecuteSqlRaw(providerName, connStr, req.Sql);
            }

            // 3. 其他一切操作（UPDATE/DELETE/EXEC/CALL/存储过程等）全视为“危险操作”

            // 3.1 白名单校验
            var allowedOps = _config.GetSection("DatabaseSettings:AllowedOperations").Get<string[]>();

            // 如果白名单为空，或者里面不包含当前操作词，直接拦截
            if (allowedOps == null || !allowedOps.Contains(opType, StringComparer.OrdinalIgnoreCase))
            {
                return BadRequest(ResponseHelper.Fail($"安全拦截：当前系统未授权执行 '{opType}' 操作。如果你调用的存储过程，请确保在 AllowedOperations 中配置了 'EXEC' 或该存储过程名称。", 403, "SECURITY_DENIED"));
            }

            // 3.2 二次确认（Token 校验）
            if (string.IsNullOrEmpty(req.ConfirmToken))
            {
                var token = Guid.NewGuid().ToString();
                _cache.Set(token, req.DbAlias + "|" + req.Sql, TimeSpan.FromMinutes(5));

                var failResponse = ResponseHelper.Fail($"⚠️ 危险操作！请向用户确认是否要执行该操作：{req.Sql}", 403, "REQUIRE_CONFIRMATION");
                return StatusCode(403, new
                {
                    code = 403,
                    message = failResponse.Message,
                    error_type = failResponse.ErrorType,
                    data = new { confirm_token = token }
                });
            }

            // 3.3 校验 Token
            var cachedSql = _cache.Get<string>(req.ConfirmToken);
            if (cachedSql != req.DbAlias + "|" + req.Sql)
                return BadRequest(ResponseHelper.Fail("无效或过期的确认凭证，请重新发起", 400, "INVALID_TOKEN"));

            // 3.4 执行并销毁 Token
            _cache.Remove(req.ConfirmToken);

            // 👇 这里的调用参数个数（providerName, connStr, req.Sql）必须和下面定义的 ExecuteSqlRaw 完全一致
            return await ExecuteSqlRaw(providerName, connStr, req.Sql);
        }

        /// <summary>
        /// 提取 SQL 第一个关键字，同时防止通过注释注入执行额外语句
        /// </summary>
        private string GetSqlOperationType(string sql)
        {
            // 移除单行注释和多行注释
            var cleanSql = Regex.Replace(sql, @"(--.*?$)|(/\*.*?\*/)", "", RegexOptions.Multiline).Trim();
            // 匹配第一个单词
            var match = Regex.Match(cleanSql, @"^\s*([a-zA-Z_]+)");
            return match.Success ? match.Groups[1].Value.ToUpper() : "";
        }

        /// <summary>
        /// 执行 SQL，支持多种数据库
        /// </summary>
        private async Task<IActionResult> ExecuteSqlRaw(string providerName, string connStr, string sql)
        {
            try
            {
                // 动态获取对应数据库的工厂类
                var factory = DbProviderFactories.GetFactory(providerName);
                using var conn = factory.CreateConnection();
                if (conn == null)
                    return BadRequest(ResponseHelper.Fail("无法创建数据库连接", 500, "BACKEND_ERROR"));

                conn.ConnectionString = connStr;
                await conn.OpenAsync();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;

                using var reader = await cmd.ExecuteReaderAsync();

                // 情况 1：非查询语句（如 UPDATE/DELETE/EXEC）
                if (reader.FieldCount == 0)
                {
                    return Ok(ResponseHelper.Success(new { type = "affectedRows", message = "执行成功", affectedRows = reader.RecordsAffected }));
                }

                // 读取第一行
                if (!await reader.ReadAsync())
                {
                    return Ok(ResponseHelper.Success(new { type = "empty", message = "查询结果为空", data = new List<object>() }));
                }

                // 情况 2：单行单列（标量值）
                if (reader.FieldCount == 1)
                {
                    var hasNextRow = await reader.ReadAsync();
                    if (!hasNextRow)
                    {
                        return Ok(ResponseHelper.Success(new { type = "scalar", column = reader.GetName(0), data = reader.GetValue(0) }));
                    }
                }

                // 情况 3：数据集 (多行/单行多列)
                var dataList = new List<Dictionary<string, object>>();
                int rowCount = 0;
                do
                {
                    var row = new Dictionary<string, object>();
                    for (int i = 0; i < reader.FieldCount; i++)
                        row[reader.GetName(i)] = reader.GetValue(i);

                    dataList.Add(row);
                    rowCount++;
                } while (await reader.ReadAsync() && rowCount < 100);

                if (dataList.Count == 1)
                {
                    return Ok(ResponseHelper.Success(new { type = "singleRow", data = dataList[0] }));
                }

                return Ok(ResponseHelper.Success(new { type = "dataset", rowCount = dataList.Count, data = dataList }));
            }
            catch (Exception ex)
            {
                return BadRequest(ResponseHelper.Fail($"SQL执行失败: {ex.Message}", 500, "BACKEND_ERROR"));
            }
        }
    }
}