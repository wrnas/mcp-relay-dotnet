using System.Security;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RelayMcp
{
    public class SkillRegistry
    {
        private readonly Dictionary<string, SkillDefinition> _skills = new(StringComparer.OrdinalIgnoreCase);
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly IConfiguration _config;


        public SkillRegistry(IWebHostEnvironment env, IConfiguration config)
        {
            _config = config;
            // 优先找项目根目录，找不到就找程序运行目录
            var dir = Path.Combine(env.ContentRootPath, "skills");
            if (!Directory.Exists(dir))
            {
                dir = Path.Combine(AppContext.BaseDirectory, "skills");
            }

            if (!Directory.Exists(dir))
            {
                Console.WriteLine($"警告: 找不到 skills 目录 {dir}");
                return;
            }

            Console.WriteLine($"正在加载 skills 目录: {dir}");

            foreach (var file in Directory.GetFiles(dir, "*.json"))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    // 直接反序列化为字典
                    var dict = JsonSerializer.Deserialize<Dictionary<string, SimpleSkill>>(json, JsonOpts);
                    if (dict is null) continue;

                    var fileDomain = Path.GetFileNameWithoutExtension(file);
                    foreach (var kvp in dict)
                    {
                        _skills[kvp.Key] = ToDefinition(kvp.Key, kvp.Value, fileDomain);
                    }
                    Console.WriteLine($"成功加载文件: {file}, 包含 {dict.Count} 个技能");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"读取文件 {file} 失败: {ex.Message}");
                }
            }

           
        }

        private SkillDefinition ToDefinition(string name, SimpleSkill s, string fileDomain)
        {
            var backend = BuildBackend(s);
            if (backend != null && !string.IsNullOrEmpty(s.Auth) && !string.IsNullOrEmpty(s.TokenKey))
            {
                backend.AuthType = s.Auth;
                backend.TokenKey = s.TokenKey;
                // 去 appsettings.json 里找真实的 token
                backend.Token = _config[$"Tokens:{s.TokenKey}"];
            }

            return new()
            {
                ToolName = name,
                Description = s.Desc,
                Domain = s.Domain ?? fileDomain,
                InputSchema = BuildSchema(s.Params),

                // 👇 这里一定要用上面处理过 Token 的 backend，不能重新调 BuildBackend(s)！！！
                Backend = backend,

                Enabled = true
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

        public SkillDefinition? Get(string name)
            => _skills.TryGetValue(name, out var s) ? s : null;

        public List<SkillSummary> Search(string query, int topK)
        {
            var q = query?.Trim() ?? "";
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

        public async Task<ExecResult> ExecuteAsync(string toolName, JsonElement args, IHttpClientFactory httpFactory)
        {
            if (!_skills.TryGetValue(toolName, out var skill))
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
            // 如果没有配置 Token 或认证类型，直接返回
            if (string.IsNullOrEmpty(backend.Token) || string.IsNullOrEmpty(backend.AuthType))
                return;

            if (backend.AuthType.Equals("bearer", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", backend.Token);
            }
            else if (backend.AuthType.Equals("basic", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(backend.Token);
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(bytes));
            }
            else if (backend.AuthType.Equals("header", StringComparison.OrdinalIgnoreCase))
            {
                // 如果是自定义 Header，TokenKey 就是 Header 的名字（比如 "X-Api-Key"）
                var headerName = string.IsNullOrEmpty(backend.TokenKey) ? "X-Access-Token" : backend.TokenKey;
                request.Headers.Add(headerName, backend.Token);
            }
        }

        private static async Task<ExecResult> ExecuteRestAsync(SkillDefinition skill, JsonElement args, IHttpClientFactory httpFactory)
        {
            var url = skill.Backend!.Url!;
            var remaining = new Dictionary<string, object?>();

            // 1. 解析 URL 路径参数
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

            // 2. 提前构建 HttpRequestMessage，方便统一注入 Header
            var request = new HttpRequestMessage(method, url);

            // 3. 注入业务系统要求的鉴权 Token
            InjectAuthHeaders(request, skill.Backend);

            HttpResponseMessage resp;

            // 4. 分发 GET 或 POST 等请求
            if (method == HttpMethod.Get)
            {
                // 拼接剩余参数为 QueryString
                var qs = string.Join("&", remaining.Select(kv =>
                    $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value?.ToString() ?? "")}"));

                if (!string.IsNullOrEmpty(qs))
                {
                    url += (url.Contains('?') ? "&" : "?") + qs;
                    request.RequestUri = new Uri(url); // 更新请求地址
                }
                resp = await client.SendAsync(request);
            }
            else
            {
                // 序列化剩余参数为 JSON Body
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

            // 👇 核心魔法：如果没有写死 body，就自动拼装 XML
            if (string.IsNullOrEmpty(body) && !string.IsNullOrEmpty(skill.Backend.SoapMethod))
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("<soapenv:Envelope xmlns:soapenv=\"http://schemas.xmlsoap.org/soap/envelope/\" ");
                sb.Append($"xmlns:tem=\"{skill.Backend.SoapNamespace}\">");
                sb.Append("<soapenv:Header/><soapenv:Body>");
                sb.Append($"<tem:{skill.Backend.SoapMethod}>");

                // 自动遍历参数并填入
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

            // 如果既没有 body 也没有 method，报错
            if (string.IsNullOrEmpty(body))
                return ExecResult.Fail("BACKEND_ERROR", "未配置 SOAP Body 或 Method", 500);

            // 1. 如果配了模板，替换 SOAP XML 模板里的变量
            if (!string.IsNullOrEmpty(skill.Backend.SoapBody) && args.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in args.EnumerateObject())
                    body = body.Replace("{" + prop.Name + "}", SecurityElement.Escape(prop.Value.ToString()));
            }

            // 2. 构建请求
            var req = new HttpRequestMessage(HttpMethod.Post, skill.Backend.Url)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "text/xml")
            };

            if (!string.IsNullOrEmpty(skill.Backend.SoapAction))
                req.Headers.Add("SOAPAction", skill.Backend.SoapAction);

            // 3. 注入鉴权 Token
            InjectAuthHeaders(req, skill.Backend);

            var resp = await httpFactory.CreateClient().SendAsync(req);
            var text = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                return ExecResult.Fail("BACKEND_ERROR", $"SOAP 返回 {(int)resp.StatusCode}: {text}", (int)resp.StatusCode);

            return ExecResult.Success(text);
        }


        #region 兼容老式简单的返回结构的webservice       


        #endregion
    }
}