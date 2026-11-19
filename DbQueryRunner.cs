using System.Data;
using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RelayMcp;

/// <summary>One result-reading path for dynamic and configured SQL.</summary>
public static class DbQueryRunner
{
    private static readonly Regex Placeholder = new(@"\{([a-zA-Z_][a-zA-Z0-9_]*)\}", RegexOptions.Compiled);
    private static readonly Regex AliasPattern = new(@"^[a-zA-Z0-9_-]+$", RegexOptions.Compiled);

    public static async Task<ExecResult> ExecuteAsync(
        IConfiguration config, string? alias, string sql, JsonElement args = default,
        ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(alias) || !AliasPattern.IsMatch(alias))
            return ExecResult.Fail("INVALID_PARAMS", "数据库别名格式不正确", 400);

        var database = config.GetSection($"DatabaseSettings:Connections:{alias}");
        var provider = database["Provider"];
        var connectionString = database["ConnectionString"];
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(connectionString))
            return ExecResult.Fail("INVALID_PARAMS", $"未找到数据库配置: {alias}", 400);

        if (string.IsNullOrWhiteSpace(sql))
            return ExecResult.Fail("INVALID_PARAMS", "SQL 不能为空", 400);

        try
        {
            var factory = DbProviderFactories.GetFactory(provider);
            using var conn = factory.CreateConnection()
                ?? throw new InvalidOperationException("无法创建数据库连接");
            conn.ConnectionString = connectionString;
            await conn.OpenAsync();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = Math.Clamp(config.GetValue("DatabaseSettings:CommandTimeoutSeconds", 30), 1, 300);

            if (args.ValueKind == JsonValueKind.Object)
            {
                var parameterNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (Match match in Placeholder.Matches(sql))
                    parameterNames.Add(match.Groups[1].Value);

                foreach (var name in parameterNames)
                {
                    if (!args.TryGetProperty(name, out var value))
                        return ExecResult.Fail("INVALID_PARAMS", $"SQL 模板缺少参数: {name}", 400);
                    var parameter = cmd.CreateParameter();
                    parameter.ParameterName = "@" + name;
                    parameter.Value = ConvertValue(value);
                    cmd.Parameters.Add(parameter);
                }
                cmd.CommandText = Placeholder.Replace(sql, m => "@" + m.Groups[1].Value);
            }
            else if (Placeholder.IsMatch(sql))
            {
                return ExecResult.Fail("INVALID_PARAMS", "SQL 模板缺少参数对象", 400);
            }

            using var reader = await cmd.ExecuteReaderAsync();
            var limit = Math.Clamp(config.GetValue("DatabaseSettings:MaxRows", 100), 1, 1000);
            var result = await ReadResultAsync(reader, limit);
            return ExecResult.Success(result);
        }
        catch (DbException ex)
        {
            logger?.LogWarning("SQL execution failed for {Alias}: {ErrorType}", alias, ex.GetType().Name);
            return ExecResult.Fail("BACKEND_ERROR", $"数据库执行失败 ({ex.GetType().Name})，请检查服务端日志", 502);
        }
        catch (Exception ex)
        {
            logger?.LogError("SQL execution error for {Alias}: {ErrorType}", alias, ex.GetType().Name);
            return ExecResult.Fail("BACKEND_ERROR", $"SQL 执行失败 ({ex.GetType().Name})", 500);
        }
    }

    private static object ConvertValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? (object)DBNull.Value,
        JsonValueKind.Number when value.TryGetInt64(out var n) => n,
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => DBNull.Value,
        _ => value.GetRawText()
    };

    internal static async Task<object> ReadResultAsync(DbDataReader reader, int limit)
    {
        if (reader.FieldCount == 0)
            return new { type = "affectedRows", affectedRows = reader.RecordsAffected };

        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var baseName = reader.GetName(i);
            if (string.IsNullOrWhiteSpace(baseName)) baseName = $"column_{i + 1}";
            var name = baseName;
            for (var suffix = 2; !seen.Add(name); suffix++) name = $"{baseName}_{suffix}";
            names.Add(name);
        }

        var rows = new List<Dictionary<string, object?>>();
        while (rows.Count < limit && await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < reader.FieldCount; i++)
                row[names[i]] = await reader.IsDBNullAsync(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        var truncated = rows.Count == limit && await reader.ReadAsync();

        if (rows.Count == 0)
            return new { type = "empty", rowCount = 0, truncated = false, data = rows };
        if (rows.Count == 1 && !truncated && reader.FieldCount == 1)
            return new { type = "scalar", column = names[0], data = rows[0][names[0]] };
        if (rows.Count == 1 && !truncated)
            return new { type = "singleRow", data = rows[0] };
        return new { type = "dataset", rowCount = rows.Count, truncated, data = rows };
    }
}
