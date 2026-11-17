using System.Security;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Data.Common;

namespace RelayMcp
{
    public class SkillRegistry
    {
        // 👈 去掉了 readonly，为了在热加载时能安全替换（写时复制机制）
        private Dictionary<string, SkillDefinition> _skills = new(StringComparer.OrdinalIgnoreCase);
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly IConfiguration _config;
        private readonly string _skillsDir;
        private readonly object _lockObj = new object(); // 用于防止并发读写冲突

        public SkillRegistry(IWebHostEnvironment env, IConfiguration config)
        {
            _config = config;

            // 确定 skills 目录
            _skillsDir = Path.Combine(env.ContentRootPath, "skills");
            if (!Directory.Exists(_skillsDir))
            {
                _skillsDir = Path.Combine(AppContext.BaseDirectory, "skills");
            }

            if (!Directory.Exists(_skillsDir))
            {
                Console.WriteLine($"警告: 找不到 skills 目录 {_skillsDir}");
                return;
            }

            // 1. 启动时首次加载所有 JSON
            LoadAllSkills();

            // 2. 👇 开启文件监听（热加载核心）
            try
            {
                var watcher = new FileSystemWatcher(_skillsDir, "*.json");
                // 监听最后修改时间、文件名和目录名
                watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName;

                // 绑定事件
                watcher.Changed += OnFileChanged;
                watcher.Created += OnFileChanged;
                watcher.Deleted += OnFileChanged;
                watcher.Renamed += OnFileChanged;

                watcher.EnableRaisingEvents = true;
                Console.WriteLine($"👀 已开启技能热加载监听: {_skillsDir}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"启动文件监听失败: {ex.Message}");
            }
        }

        // 👇 文件变化时的回调
        private void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            // 防抖处理：操作系统保存文件时经常会触发多次事件
            // 稍微等待 200 毫秒，等文件写入完成
            Thread.Sleep(200);

            Console.WriteLine($"\n🔄 检测到配置文件变化 [{e.ChangeType}]：{e.Name}，正在重新加载...");
            LoadAllSkills();
        }

        // 👇 抽离出的独立加载逻辑
        private void LoadAllSkills()
        {
            // 创建一个新的字典
            var newSkills = new Dictionary<string, SkillDefinition>(StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (var file in Directory.GetFiles(_skillsDir, "*.json"))
                {
                    try
                    {
                        // 读取时加上共享锁，防止文件被占用
                        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var sr = new StreamReader(fs);
                        var json = sr.ReadToEnd();

                        var dict = JsonSerializer.Deserialize<Dictionary<string, SimpleSkill>>(json, JsonOpts);
                        if (dict is null) continue;

                        var fileDomain = Path.GetFileNameWithoutExtension(file);
                        foreach (var kvp in dict)
                        {
                            newSkills[kvp.Key] = ToDefinition(kvp.Key, kvp.Value, fileDomain);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"读取文件 {file} 失败: {ex.Message}");
                    }
                }

                // 👈 核心：原子替换。在锁的保护下，把新字典替换给老字典
                lock (_lockObj)
                {
                    _skills = newSkills;
                }

                Console.WriteLine($"✅ 技能加载/重载完成，当前共有 {newSkills.Count} 个技能。");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"加载目录失败: {ex.Message}");
            }
        }

        private SkillDefinition ToDefinition(string name, SimpleSkill s, string fileDomain)
        {
            var backend = BuildBackend(s);
            if (backend != null && !string.IsNullOrEmpty(s.Auth) && !string.IsNullOrEmpty(s.TokenKey))
            {
                backend.AuthType = s.Auth;
                backend.TokenKey = s.TokenKey;
                backend.Token = _config[$"Tokens:{s.TokenKey}"];
            }

            return new()
            {
                ToolName = name,
                Description = s.Desc,
                Domain = s.Domain ?? fileDomain,
                InputSchema = BuildSchema(s.Params),
                Backend = backend,               
                Enabled = s.Enabled
            };
        }

        private static JsonElement BuildSchema(Dictionary<string, string>? paramsDict)
        {
            var props = new JsonObject();
            var required = new JsonArray();

            if (paramsDict is not null)
            {
                foreach (var (key, rawValue) in paramsDict)
                {
                    var raw = rawValue?.Trim() ?? "";
                    var optional = raw.StartsWith("?");
                    if (optional) raw = raw[1..].Trim();

                    var space = raw.IndexOf(' ');
                    var type = NormalizeType(space < 0 ? raw : raw[..space]);
                    var desc = space < 0 ? "" : raw[(space + 1)..];

                    props[key] = new JsonObject { ["type"] = type, ["description"] = desc };
                    if (!optional) required.Add(key);
                }
            }

            var root = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = props,
                ["required"] = required
            };
            return JsonSerializer.SerializeToElement(root);
        }

        private static string NormalizeType(string t) => t.ToLowerInvariant() switch
        {
            "int" or "integer" => "integer",
            "num" or "number" or "float" or "double" => "number",
            "bool" or "boolean" => "boolean",
            "arr" or "array" => "array",
            "obj" or "object" => "object",
            _ => "string"
        };

        private static BackendConfig? BuildBackend(SimpleSkill s)
        {
            if (s.Get is not null) return new() { Protocol = "rest", Method = "GET", Url = s.Get };
            if (s.Post is not null) return new() { Protocol = "rest", Method = "POST", Url = s.Post };
            if (s.Put is not null) return new() { Protocol = "rest", Method = "PUT", Url = s.Put };
            if (s.Delete is not null) return new() { Protocol = "rest", Method = "DELETE", Url = s.Delete };

            if (s.SqlTemplate is not null)
            {
                return new() { Protocol = "sql", DbAlias = s.DbAlias, SqlTemplate = s.SqlTemplate };
            }

            if (s.Soap is not null)
            {
                return new()
                {
                    Protocol = "soap",
                    Method = "POST",
                    Url = s.Soap,
                    SoapAction = s.Action,
                    SoapBody = s.Body,
                    SoapMethod = s.SoapMethod,
                    SoapNamespace = s.SoapNamespace
                };
            }
            return null;
        }

        // 👇 给读取字典的方法加上锁，确保在热加载替换字典的一瞬间，不会报空引用异常
        public SkillDefinition? Get(string name)
        {
            lock (_lockObj)
            {
                return _skills.TryGetValue(name, out var s) ? s : null;
            }
        }

        public List<SkillSummary> Search(string query, int topK)
        {
            var q = query?.Trim() ?? "";
            lock (_lockObj)
            {
                IEnumerable<SkillDefinition> all = _skills.Values.Where(s => s.Enabled);
                if (!string.IsNullOrEmpty(q))
                {
                    all = all.Where(s =>
                        s.ToolName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        (s.Description?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (s.Domain?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
                }
                return all.Take(topK).Select(s => new SkillSummary(s.ToolName, s.Description, s.Domain)).ToList();
            }
        }

        public async Task<ExecResult> ExecuteAsync(string toolName, JsonElement args, IHttpClientFactory httpFactory)
        {
            SkillDefinition? skill;
            lock (_lockObj)
            {
                _skills.TryGetValue(toolName, out skill);
            }

            if (skill == null)
                return ExecResult.Fail("NOT_FOUND", $"技能 {toolName} 不存在", 404);
            if (!skill.Enabled)
                return ExecResult.Fail("NOT_FOUND", $"技能 {toolName} 已禁用", 404);

            var missing = CheckRequired(skill.InputSchema, args);
            if (missing is not null)
                return ExecResult.Fail("INVALID_PARAMS", $"缺少必填字段: {missing}", 400);

            return skill.Backend?.Protocol switch
            {
                "rest" => await ExecuteRestAsync(skill, args, httpFactory),
                "soap" => await ExecuteSoapAsync(skill, args, httpFactory),
                "sql" => await ExecuteSqlTemplateAsync(skill, args),
                _ => ExecResult.Fail("BACKEND_ERROR", "不支持的协议", 500)
            };
        }

        private static string? CheckRequired(JsonElement schema, JsonElement args)
        {
            if (!schema.TryGetProperty("required", out var required) || required.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var item in required.EnumerateArray())
            {
                var name = item.GetString();
                if (name is null) continue;
                if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out _))
                    return name;
            }
            return null;
        }

        private static void InjectAuthHeaders(HttpRequestMessage request, BackendConfig backend)
        {
            if (string.IsNullOrEmpty(backend.Token) || string.IsNullOrEmpty(backend.AuthType))
                return;

            if (backend.AuthType.Equals("bearer", StringComparison.OrdinalIgnoreCase))
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", backend.Token);
            else if (backend.AuthType.Equals("basic", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(backend.Token);
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(bytes));
            }
            else if (backend.AuthType.Equals("header", StringComparison.OrdinalIgnoreCase))
            {
                var headerName = string.IsNullOrEmpty(backend.TokenKey) ? "X-Access-Token" : backend.TokenKey;
                request.Headers.Add(headerName, backend.Token);
            }
        }

        private static async Task<ExecResult> ExecuteRestAsync(SkillDefinition skill, JsonElement args, IHttpClientFactory httpFactory)
        {
            var url = skill.Backend!.Url!;
            var remaining = new Dictionary<string, object?>();

            if (args.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in args.EnumerateObject())
                {
                    var placeholder = "{" + prop.Name + "}";
                    if (url.Contains(placeholder))
                        url = url.Replace(placeholder, Uri.EscapeDataString(prop.Value.ToString()));
                    else
                        remaining[prop.Name] = prop.Value;
                }
            }

            var client = httpFactory.CreateClient();
            var method = new HttpMethod(skill.Backend.Method ?? "GET");
            var request = new HttpRequestMessage(method, url);

            InjectAuthHeaders(request, skill.Backend);

            HttpResponseMessage resp;

            if (method == HttpMethod.Get)
            {
                var qs = string.Join("&", remaining.Select(kv =>
                    $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value?.ToString() ?? "")}"));

                if (!string.IsNullOrEmpty(qs))
                {
                    url += (url.Contains('?') ? "&" : "?") + qs;
                    request.RequestUri = new Uri(url);
                }
                resp = await client.SendAsync(request);
            }
            else
            {
                var json = JsonSerializer.Serialize(remaining);
                request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                resp = await client.SendAsync(request);
            }

            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                return ExecResult.Fail("BACKEND_ERROR", $"后端返回 {(int)resp.StatusCode}: {body}", (int)resp.StatusCode);

            try { return ExecResult.Success(JsonSerializer.Deserialize<JsonElement>(body)); }
            catch { return ExecResult.Success(body); }
        }

        private static async Task<ExecResult> ExecuteSoapAsync(SkillDefinition skill, JsonElement args, IHttpClientFactory httpFactory)
        {
            var body = skill.Backend!.SoapBody;

            if (string.IsNullOrEmpty(body) && !string.IsNullOrEmpty(skill.Backend.SoapMethod))
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("<soapenv:Envelope xmlns:soapenv=\"http://schemas.xmlsoap.org/soap/envelope/\" ");
                sb.Append($"xmlns:tem=\"{skill.Backend.SoapNamespace}\">");
                sb.Append("<soapenv:Header/><soapenv:Body>");
                sb.Append($"<tem:{skill.Backend.SoapMethod}>");

                if (args.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in args.EnumerateObject())
                    {
                        var val = SecurityElement.Escape(prop.Value.ToString());
                        sb.Append($"<tem:{prop.Name}>{val}</tem:{prop.Name}>");
                    }
                }

                sb.Append($"</tem:{skill.Backend.SoapMethod}>");
                sb.Append("</soapenv:Body></soapenv:Envelope>");
                body = sb.ToString();
            }

            if (string.IsNullOrEmpty(body))
                return ExecResult.Fail("BACKEND_ERROR", "未配置 SOAP Body 或 Method", 500);

            if (!string.IsNullOrEmpty(skill.Backend.SoapBody) && args.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in args.EnumerateObject())
                    body = body.Replace("{" + prop.Name + "}", SecurityElement.Escape(prop.Value.ToString()));
            }

            var req = new HttpRequestMessage(HttpMethod.Post, skill.Backend.Url)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "text/xml")
            };

            if (!string.IsNullOrEmpty(skill.Backend.SoapAction))
                req.Headers.Add("SOAPAction", skill.Backend.SoapAction);

            InjectAuthHeaders(req, skill.Backend);

            var resp = await httpFactory.CreateClient().SendAsync(req);
            var text = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                return ExecResult.Fail("BACKEND_ERROR", $"SOAP 返回 {(int)resp.StatusCode}: {text}", (int)resp.StatusCode);

            return ExecResult.Success(text);
        }

        private async Task<ExecResult> ExecuteSqlTemplateAsync(SkillDefinition skill, JsonElement args)
        {
            var dbConfig = _config.GetSection($"DatabaseSettings:Connections:{skill.Backend!.DbAlias}");
            var providerName = dbConfig["Provider"];
            var connStr = dbConfig["ConnectionString"];

            if (string.IsNullOrEmpty(connStr) || string.IsNullOrEmpty(providerName))
                return ExecResult.Fail("BACKEND_ERROR", $"数据库配置 {skill.Backend.DbAlias} 不存在", 500);

            try
            {
                var factory = DbProviderFactories.GetFactory(providerName);
                using var conn = factory.CreateConnection();
                conn!.ConnectionString = connStr;
                await conn.OpenAsync();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = skill.Backend.SqlTemplate;

                if (args.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in args.EnumerateObject())
                    {
                        var placeholder = "{" + prop.Name + "}";
                        if (cmd.CommandText.Contains(placeholder))
                        {
                            cmd.CommandText = cmd.CommandText.Replace(placeholder, "@" + prop.Name);
                            var p = cmd.CreateParameter();
                            p.ParameterName = "@" + prop.Name;
                            p.Value = prop.Value.ValueKind == JsonValueKind.Number ? prop.Value.GetDecimal() : prop.Value.ToString();
                            cmd.Parameters.Add(p);
                        }
                    }
                }

                using var reader = await cmd.ExecuteReaderAsync();

                if (reader.FieldCount == 0)
                {
                    return ExecResult.Success(new { type = "affectedRows", message = "执行成功", affectedRows = reader.RecordsAffected });
                }

                var dataList = new List<Dictionary<string, object>>();
                int rowCount = 0;
                while (await reader.ReadAsync() && rowCount < 100)
                {
                    var row = new Dictionary<string, object>();
                    for (int i = 0; i < reader.FieldCount; i++)
                        row[reader.GetName(i)] = reader.GetValue(i);
                    dataList.Add(row);
                    rowCount++;
                }

                if (dataList.Count == 1) return ExecResult.Success(new { type = "singleRow", data = dataList[0] });
                return ExecResult.Success(new { type = "dataset", rowCount = dataList.Count, data = dataList });
            }
            catch (Exception ex)
            {
                return ExecResult.Fail("BACKEND_ERROR", $"SQL模板执行失败: {ex.Message}", 500);
            }
        }
    }
}