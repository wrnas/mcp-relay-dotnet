using System.Collections;
using System.Data.Common;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RelayMcp;

public sealed class SkillRegistry : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private static readonly Regex ParameterName = new(@"^[a-zA-Z_][a-zA-Z0-9_]*$", RegexOptions.Compiled);
    private static readonly Regex LegacyPsPlaceholder = new(@"\{[a-zA-Z_][a-zA-Z0-9_]*\}", RegexOptions.Compiled);

    private readonly IConfiguration _config;
    private readonly ApprovalQueue _approvals;
    private readonly LdapQueryExecutor _ldap;
    private readonly LdapMutationExecutor _ldapWrite;
    private readonly ILogger<SkillRegistry> _logger;
    private readonly string _skillsDir;
    private readonly object _reloadGate = new();
    private Dictionary<string, Dictionary<string, SkillDefinition>> _byFile = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, SkillDefinition> _skills = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;

    public SkillRegistry(IWebHostEnvironment env, IConfiguration config, ApprovalQueue approvals, LdapQueryExecutor ldap, LdapMutationExecutor ldapWrite, ILogger<SkillRegistry> logger)
    {
        _config = config;
        _approvals = approvals;
        _ldap = ldap;
        _ldapWrite = ldapWrite;
        _logger = logger;
        _skillsDir = Path.Combine(env.ContentRootPath, "skills");
        if (!Directory.Exists(_skillsDir))
            _skillsDir = Path.Combine(AppContext.BaseDirectory, "skills");
        Directory.CreateDirectory(_skillsDir);

        LoadAllSkills();
        try
        {
            _watcher = new FileSystemWatcher(_skillsDir, "*.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = false
            };
            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileChanged;
            _watcher.Deleted += OnFileChanged;
            _watcher.Renamed += OnFileChanged;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Skill hot reload watcher unavailable");
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        // Events can occur while an editor has written only half the JSON file.
        Thread.Sleep(250);
        LoadAllSkills();
    }

    private void LoadAllSkills()
    {
        lock (_reloadGate)
        {
            try
            {
                var updatedFiles = new Dictionary<string, Dictionary<string, SkillDefinition>>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in Directory.GetFiles(_skillsDir, "*.json").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var sr = new StreamReader(fs);
                        var document = JsonSerializer.Deserialize<Dictionary<string, SimpleSkill>>(sr.ReadToEnd(), JsonOpts)
                            ?? throw new FormatException("JSON 根节点必须是 Skill 对象");
                        var definitions = new Dictionary<string, SkillDefinition>(StringComparer.OrdinalIgnoreCase);
                        foreach (var (name, skill) in document)
                        {
                            if (string.IsNullOrWhiteSpace(name) || skill is null)
                                throw new FormatException("Skill 名称或内容不能为空");
                            definitions.Add(name, ToDefinition(name, skill, Path.GetFileNameWithoutExtension(file)));
                        }
                        updatedFiles[file] = definitions;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Skill file invalid, retaining last good configuration: {File} ({Error})",
                            Path.GetFileName(file), ex.Message);
                        if (_byFile.TryGetValue(file, out var previous)) updatedFiles[file] = previous;
                    }
                }

                var merged = new Dictionary<string, SkillDefinition>(StringComparer.OrdinalIgnoreCase);
                foreach (var (file, definitions) in updatedFiles)
                {
                    foreach (var (name, definition) in definitions)
                    {
                        if (!merged.TryAdd(name, definition))
                            _logger.LogWarning("Duplicate Skill {Skill} in {File}; first definition retained", name, Path.GetFileName(file));
                    }
                }
                _byFile = updatedFiles;
                _skills = merged;
                _logger.LogInformation("Loaded {Count} Skills", _skills.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Skill directory reload failed; retained last known configuration");
            }
        }
    }

    private SkillDefinition ToDefinition(string name, SimpleSkill simple, string system)
    {
        if (simple.Params != null && simple.Params.Keys.Any(k => !ParameterName.IsMatch(k)))
            throw new FormatException($"{name}: 参数名只能使用英文字母、数字、下划线，且不能以数字开头");
        var backend = BuildBackend(simple);
        if (backend is null) throw new FormatException($"{name}: 缺少执行方式");

        if (!string.IsNullOrWhiteSpace(simple.Auth))
        {
            if (!new[] { "bearer", "basic", "header" }.Contains(simple.Auth.ToLowerInvariant()))
                throw new FormatException($"{name}: 不支持的 auth 类型");
            if (string.IsNullOrWhiteSpace(simple.TokenKey))
                throw new FormatException($"{name}: auth 必须配置 token_key");
            backend.AuthType = simple.Auth;
            backend.TokenKey = simple.TokenKey;
            backend.Token = _config[$"Tokens:{simple.TokenKey}"];
            backend.HeaderName = simple.HeaderName;
            if (string.IsNullOrWhiteSpace(backend.Token))
                throw new FormatException($"{name}: 未设置 Tokens:{simple.TokenKey}");
        }

        if (simple.Risk is not null && simple.Risk != "read" && simple.Risk != "write")
            throw new FormatException($"{name}: risk 仅支持 read/write");

        var defaultWrite = backend.Protocol switch
        {
            "rest" => backend.Method != "GET",
            "soap" or "powershell" => true,
            "ldap" => false,
            "ldap_write" => true,
            "sql" => !SqlSafety.IsReadOnly(backend.SqlTemplate, out _),
            _ => true
        };
        // Dynamic SQL cannot be changed into a read operation just by claiming risk=read.
        var sqlIsWrite = backend.Protocol == "sql" && !SqlSafety.IsReadOnly(backend.SqlTemplate, out _);
        var isWrite = sqlIsWrite || simple.Risk == "write" || (simple.Risk is null && defaultWrite);

        if (backend.Protocol == "ldap" && simple.Risk == "write")
            throw new FormatException($"{name}: ldap_filter 是只读查询，不能配置 risk=write");
        if (backend.Protocol == "ldap_write" && simple.Risk != "write")
            throw new FormatException($"{name}: ldap_action 必须显式配置 risk=write");

        if (backend.Protocol == "powershell" && LegacyPsPlaceholder.IsMatch(backend.ScriptTemplate ?? ""))
            throw new FormatException($"{name}: PowerShell 不再支持 {{参数}} 字符串替换，请使用 $参数 变量");

        return new SkillDefinition
        {
            ToolName = name,
            Description = simple.Desc,
            Domain = simple.Domain ?? system,
            System = system,
            InputSchema = BuildSchema(simple.Params),
            Backend = backend,
            Enabled = simple.Enabled,
            IsWrite = isWrite,
            ChatApprovalAllowed = simple.ChatApproval
        };
    }

    private static BackendConfig? BuildBackend(SimpleSkill s)
    {
        var count = new[] { s.Get, s.Post, s.Put, s.Delete, s.Soap, s.SqlTemplate, s.Script, s.LdapFilter, s.LdapAction }
            .Count(x => x != null);
        if (count != 1) throw new FormatException("必须且只能配置一个执行方式 get/post/put/delete/soap/sql/script/ldap_filter/ldap_action");
        if (s.LdapAction is not null)
        {
            LdapMutationExecutor.ValidateSkillTemplate(s.LdapServer, s.LdapAction, s.Params);
            return new() { Protocol = "ldap_write", LdapServer = s.LdapServer, LdapAction = s.LdapAction };
        }
        if (s.LdapFilter is not null)
        {
            LdapQueryExecutor.ValidateSkillTemplate(s.LdapServer, s.LdapFilter, s.LdapAttributes, s.LdapScope, s.LdapBaseDn, s.Params);
            return new() { Protocol = "ldap", LdapServer = s.LdapServer,
                LdapFilter = s.LdapFilter, LdapAttributes = s.LdapAttributes,
                LdapScope = s.LdapScope, LdapBaseDn = s.LdapBaseDn };
        }
        if (s.Get is not null) return new() { Protocol = "rest", Method = "GET", Url = s.Get };
        if (s.Post is not null) return new() { Protocol = "rest", Method = "POST", Url = s.Post };
        if (s.Put is not null) return new() { Protocol = "rest", Method = "PUT", Url = s.Put };
        if (s.Delete is not null) return new() { Protocol = "rest", Method = "DELETE", Url = s.Delete };
        if (s.SqlTemplate is not null)
        {
            if (string.IsNullOrWhiteSpace(s.DbAlias)) throw new FormatException("SQL Skill 缺少 db_alias");
            return new() { Protocol = "sql", DbAlias = s.DbAlias, SqlTemplate = s.SqlTemplate };
        }
        if (s.Script is not null)
        {
            var target = s.PowerShellTarget ?? "exchange";
            if (target != "local" && target != "exchange")
                throw new FormatException("powershell_target 只能为 local 或 exchange");
            return new() { Protocol = "powershell", ScriptTemplate = s.Script, PowerShellTarget = target };
        }
        return new()
        {
            Protocol = "soap", Method = "POST", Url = s.Soap,
            SoapAction = s.Action, SoapBody = s.Body,
            SoapMethod = s.SoapMethod, SoapNamespace = s.SoapNamespace
        };
    }

    private static JsonElement BuildSchema(Dictionary<string, string>? parameters)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        if (parameters != null)
        {
            foreach (var (name, definition) in parameters)
            {
                var raw = definition?.Trim() ?? "";
                var optional = raw.StartsWith('?');
                if (optional) raw = raw[1..].Trim();
                var space = raw.IndexOf(' ');
                var type = NormalizeType(space < 0 ? raw : raw[..space]);
                var description = space < 0 ? "" : raw[(space + 1)..];
                properties[name] = new JsonObject { ["type"] = type, ["description"] = description };
                if (!optional) required.Add(name);
            }
        }
        return JsonSerializer.SerializeToElement(new JsonObject
        {
            ["type"] = "object", ["properties"] = properties, ["required"] = required,
            ["additionalProperties"] = false
        });
    }

    private static string NormalizeType(string type) => type.ToLowerInvariant() switch
    {
        "int" or "integer" => "integer",
        "num" or "number" or "float" or "double" => "number",
        "bool" or "boolean" => "boolean",
        "arr" or "array" => "array",
        "obj" or "object" => "object",
        _ => "string"
    };

    public SkillDefinition? Get(string name)
    {
        lock (_reloadGate) return _skills.TryGetValue(name, out var skill) ? skill : null;
    }

    // 文件名就是业务系统标识：skills/ad.json => system=ad。
    // 不依赖单个 Skill 的 domain 字段；domain 仍保留原来的业务分类含义。
    public List<SkillSystemSummary> ListSystems()
    {
        lock (_reloadGate)
        {
            return _skills.Values.Where(s => s.Enabled)
                .GroupBy(s => s.System, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => new SkillSystemSummary(g.Key, g.Count()))
                .ToList();
        }
    }

    // 不提供 query 时返回全部已启用 Skill（只含摘要，不加载参数 Schema）。
    public List<SkillSummary> ListAll()
    {
        lock (_reloadGate)
        {
            return _skills.Values.Where(s => s.Enabled)
                .OrderBy(s => s.System, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.ToolName, StringComparer.OrdinalIgnoreCase)
                .Select(ToSummary)
                .ToList();
        }
    }

    // 系统精确匹配：不受 top_k 影响，返回该系统全部启用 Skill。
    public List<SkillSummary> ListBySystem(string system)
    {
        var name = system?.Trim() ?? "";
        lock (_reloadGate)
        {
            return _skills.Values
                .Where(s => s.Enabled && s.System.Equals(name, StringComparison.OrdinalIgnoreCase))
                .OrderBy(s => s.ToolName, StringComparer.OrdinalIgnoreCase)
                .Select(ToSummary)
                .ToList();
        }
    }

    // 已定位业务系统后再按关键词筛选：GET /skills?domain=ad&query=用户。
    // 仅关键词搜索使用 top_k；若 query 为空，请调用 ListBySystem 返回系统全量目录。
    public List<SkillSummary> SearchInSystem(string system, string query, int topK)
    {
        var name = system?.Trim() ?? "";
        var q = query?.Trim() ?? "";
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        lock (_reloadGate)
        {
            var candidates = _skills.Values
                .Where(s => s.Enabled && s.System.Equals(name, StringComparison.OrdinalIgnoreCase));
            return candidates.Select(s => new { Skill = s, Score = Score(s, q, words) })
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Skill.ToolName, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Clamp(topK, 1, 50))
                .Select(x => ToSummary(x.Skill))
                .ToList();
        }
    }

    public List<SkillSummary> Search(string query, int topK)
    {
        var q = query?.Trim() ?? "";
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        lock (_reloadGate)
        {
            var candidates = _skills.Values.Where(s => s.Enabled).ToList();

            // 兼容原有 Python MCP 的 GetSkillList(query, top_k)：
            // GetSkillList(query="ad", top_k=8) 将返回 ad.json 的所有 Skill，
            // 不需要先修改现有 MCP 客户端的默认 top_k。
            if (q.Length == 0)
                return candidates.OrderBy(s => s.System, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(s => s.ToolName, StringComparer.OrdinalIgnoreCase)
                    .Select(ToSummary).ToList();
            if (candidates.Any(s => s.System.Equals(q, StringComparison.OrdinalIgnoreCase)))
                return candidates.Where(s => s.System.Equals(q, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(s => s.ToolName, StringComparer.OrdinalIgnoreCase)
                    .Select(ToSummary).ToList();

            // 普通关键词搜索仍保留原来的 top_k 限制，避免无关结果过多。
            return candidates
                .Select(s => new { Skill = s, Score = Score(s, q, words) })
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Skill.ToolName, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Clamp(topK, 1, 50))
                .Select(x => ToSummary(x.Skill))
                .ToList();
        }
    }

    private static SkillSummary ToSummary(SkillDefinition skill) =>
        new(skill.ToolName, skill.Description, skill.Domain)
        {
            system = skill.System,
            risk = skill.IsWrite ? "write" : "read"
        };

    private static int Score(SkillDefinition s, string query, string[] words)
    {
        if (string.IsNullOrWhiteSpace(query)) return 1;
        var score = 0;
        if (s.ToolName.Equals(query, StringComparison.OrdinalIgnoreCase)) score += 100;
        if (s.ToolName.Contains(query, StringComparison.OrdinalIgnoreCase)) score += 30;
        if (s.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) == true) score += 20;
        if (s.Domain?.Contains(query, StringComparison.OrdinalIgnoreCase) == true) score += 10;
        if (s.System.Contains(query, StringComparison.OrdinalIgnoreCase)) score += 10;
        foreach (var word in words)
        {
            if (s.ToolName.Contains(word, StringComparison.OrdinalIgnoreCase)) score += 8;
            if (s.Description?.Contains(word, StringComparison.OrdinalIgnoreCase) == true) score += 5;
            if (s.Domain?.Contains(word, StringComparison.OrdinalIgnoreCase) == true) score += 3;
        }
        return score;
    }

    public Task<ExecResult> ExecuteAsync(string toolName, JsonElement args, IHttpClientFactory httpFactory)
    {
        var skill = Get(toolName);
        if (skill is null || !skill.Enabled)
            return Task.FromResult(ExecResult.Fail("NOT_FOUND", $"技能 {toolName} 不存在或已禁用", 404));

        // 统一按服务器配置禁用 AD 操作；匹配 ldap_action 或完整 Skill 名。
        // 在创建审批任务前检查，避免禁用操作进入待审批队列。
        var disabled = CheckAdOperationDisabled(skill);
        if (disabled is not null)
            return Task.FromResult(disabled);

        var validation = ValidateArguments(skill.InputSchema, args);
        if (validation is not null)
            return Task.FromResult(ExecResult.Fail("INVALID_PARAMS", validation, 400));

        if (skill.IsWrite)
        {
            if (!_config.GetValue("Relay:EnableWrites", false))
                return Task.FromResult(ExecResult.Fail("WRITE_DISABLED", "写操作未开启：请设置 Relay:EnableWrites=true", 403));

            var mode = (_config["Relay:ApprovalMode"] ?? "web").ToLowerInvariant();
            if (mode is not ("client" or "web" or "chat"))
                return Task.FromResult(ExecResult.Fail("CONFIG_ERROR", "Relay:ApprovalMode 只能是 client、web、chat", 500));
            if (mode == "web" && string.IsNullOrWhiteSpace(_config["Relay:ApprovalKey"]))
                return Task.FromResult(ExecResult.Fail("WRITE_DISABLED", "网页审批必须配置独立的 Relay:ApprovalKey", 403));
            if (mode == "client" && string.IsNullOrWhiteSpace(_config["Relay:ClientConfirmKey"]))
                return Task.FromResult(ExecResult.Fail("WRITE_DISABLED", "客户端审批必须配置 Relay:ClientConfirmKey", 403));
            var apiKey = _config["Relay:ApiKey"];
            var approvalSecret = mode == "client" ? _config["Relay:ClientConfirmKey"] : _config["Relay:ApprovalKey"];
            if (mode != "chat" && Program.FixedTimeEquals(apiKey, approvalSecret))
                return Task.FromResult(ExecResult.Fail("CONFIG_ERROR", "独立审批密钥不能与 ApiKey 相同", 403));
            if (mode == "chat" && (!_config.GetValue("Relay:ChatApprovalEnabled", false) || !skill.ChatApprovalAllowed))
                return Task.FromResult(ExecResult.Fail("WRITE_DISABLED",
                    "聊天审批需要设置 ChatApprovalEnabled=true 且在该 Skill 配置 chat_approval=true", 403));

            // Bind the approval to the exact Skill and input values. The submitted approval ID
            // cannot change the captured operation; changes require a new request.
            var capturedArgs = args.ValueKind == JsonValueKind.Object
                ? args.Clone() : JsonSerializer.SerializeToElement(new Dictionary<string, object>());
            var summary = BuildApprovalSummary(skill, capturedArgs);
            var ticket = _approvals.Enqueue(summary, mode, skill.ChatApprovalAllowed,
                () => ExecuteBackendAsync(skill, capturedArgs, httpFactory));
            if (ticket is null)
                return Task.FromResult(ExecResult.Fail("BUSY", "待审批队列已满", 429));

            var publicBase = (_config["Relay:ApprovalPublicBaseUrl"] ?? "http://127.0.0.1:54321").TrimEnd('/');
            var approvalUrl = mode == "web" ? $"{publicBase}/approve/{ticket.Id}" : null;
            var message = mode switch
            {
                "client" => "请使用 MCP 客户端原生确认窗口批准；不支持交互确认的客户端不会执行此操作。",
                "web" => "请打开独立审批网页审核并确认，不要重新提交写操作。",
                _ => "低安全级别聊天确认：请等待运维人员明确回复完整确认文字，随后才能调用 ConfirmChatApproval。"
            };
            return Task.FromResult(ExecResult.Fail("REQUIRE_APPROVAL", message, 403,
                new
                {
                    approval_id = ticket.Id,
                    mode = mode,
                    summary = summary,
                    approval_url = approvalUrl,
                    confirmation_phrase = mode == "chat" ? ticket.ChatPhrase : null,
                    expires_in_minutes = Math.Clamp(_config.GetValue("Relay:ApprovalTtlMinutes", 5), 1, 60)
                }));
        }
        return ExecuteBackendAsync(skill, args, httpFactory);
    }

    // 支持 DisabledOperations 中写动作名（例如 delete_user）或 Skill 名（例如 ad_delete_user）。
    // 只读查询可按 Skill 名禁用；其他非 AD Skill 不受影响。
    private ExecResult? CheckAdOperationDisabled(SkillDefinition skill)
    {
        var backend = skill.Backend;
        if (backend?.Protocol is not ("ldap" or "ldap_write"))
            return null;
        var section = _config.GetSection($"ActiveDirectory:Servers:{backend.LdapServer}:DisabledOperations");
        var operations = section.Get<string[]>() ?? Array.Empty<string>();
        var action = backend.LdapAction;
        if (operations.Any(value =>
                !string.IsNullOrWhiteSpace(value) &&
                (string.Equals(value.Trim(), skill.ToolName, StringComparison.OrdinalIgnoreCase) ||
                 (!string.IsNullOrWhiteSpace(action) &&
                  string.Equals(value.Trim(), action, StringComparison.OrdinalIgnoreCase)))))
            return ExecResult.Fail("AD_OPERATION_DISABLED",
                $"AD 操作 {skill.ToolName} 已被 DisabledOperations 禁用", 403);
        return null;
    }

    private static string BuildApprovalSummary(SkillDefinition skill, JsonElement args)
    {
        var values = new Dictionary<string, string>();
        if (args.ValueKind == JsonValueKind.Object)
            foreach (var property in args.EnumerateObject())
                values[property.Name] = new[] { "password", "passwd", "pwd", "token", "secret", "key" }
                    .Any(x => property.Name.Contains(x, StringComparison.OrdinalIgnoreCase))
                    ? "[REDACTED]" : property.Value.ToString();
        var backend = skill.Backend!;
        var action = backend.Protocol switch
        {
            "sql" => $"SQL database={backend.DbAlias}",
            "rest" => $"REST method={backend.Method}",
            "soap" => $"SOAP method={backend.SoapMethod ?? backend.SoapAction}",
            "powershell" => $"PowerShell target={backend.PowerShellTarget}",
            "ldap" => $"LDAP server={backend.LdapServer}",
            "ldap_write" => $"AD action={backend.LdapAction}, server={backend.LdapServer}",
            _ => backend.Protocol ?? "unknown"
        };
        return $"Skill: {skill.ToolName}\nDescription: {skill.Description}\nAction: {action}\nArgs: {JsonSerializer.Serialize(values)}";
    }

    private static string? ValidateArguments(JsonElement schema, JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object && args.ValueKind != JsonValueKind.Undefined)
            return "args 必须是 JSON 对象";
        var props = schema.GetProperty("properties");
        var supplied = args.ValueKind == JsonValueKind.Object
            ? args.EnumerateObject().ToDictionary(x => x.Name, x => x.Value, StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>();
        foreach (var req in schema.GetProperty("required").EnumerateArray())
        {
            var name = req.GetString()!;
            if (!supplied.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null ||
                (value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString())))
                return $"缺少必填字段: {name}";
        }
        foreach (var (name, value) in supplied)
        {
            if (!props.TryGetProperty(name, out var definition)) return $"未知参数: {name}";
            if (value.ValueKind == JsonValueKind.Null) continue;
            var type = definition.GetProperty("type").GetString();
            var matches = type switch
            {
                "string" => value.ValueKind == JsonValueKind.String,
                "number" => value.ValueKind == JsonValueKind.Number,
                "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "array" => value.ValueKind == JsonValueKind.Array,
                "object" => value.ValueKind == JsonValueKind.Object,
                _ => false
            };
            if (!matches) return $"参数 {name} 类型应为 {type}";
        }
        return null;
    }

    private async Task<ExecResult> ExecuteBackendAsync(SkillDefinition skill, JsonElement args, IHttpClientFactory httpFactory)
    {
        // 审批完成时重新读取禁用列表：防止等待审批期间操作被禁用后仍执行。
        var disabled = CheckAdOperationDisabled(skill);
        if (disabled is not null)
            return disabled;
        try
        {
            return skill.Backend?.Protocol switch
            {
                "rest" => await ExecuteRestAsync(skill, args, httpFactory),
                "soap" => await ExecuteSoapAsync(skill, args, httpFactory),
                "sql" => await DbQueryRunner.ExecuteAsync(_config, skill.Backend.DbAlias,
                    skill.Backend.SqlTemplate!, args, _logger),
                "powershell" => await ExecutePowerShellAsync(skill, args),
                "ldap" => await _ldap.ExecuteAsync(skill.Backend!, args),
                "ldap_write" => await _ldapWrite.ExecuteAsync(skill.Backend!, args),
                _ => ExecResult.Fail("BACKEND_ERROR", "不支持的执行协议", 500)
            };
        }
        catch (TaskCanceledException)
        {
            return ExecResult.Fail("TIMEOUT", "后端执行超时；写操作请先核实结果，不能直接重试", 504);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Skill {Skill} failed: {Error}", skill.ToolName, ex.GetType().Name);
            return ExecResult.Fail("BACKEND_ERROR", $"执行失败 ({ex.GetType().Name})", 502);
        }
    }

    private static void InjectAuthHeaders(HttpRequestMessage request, BackendConfig backend)
    {
        if (string.IsNullOrEmpty(backend.Token) || string.IsNullOrWhiteSpace(backend.AuthType)) return;
        if (backend.AuthType.Equals("bearer", StringComparison.OrdinalIgnoreCase))
            request.Headers.Authorization = new("Bearer", backend.Token);
        else if (backend.AuthType.Equals("basic", StringComparison.OrdinalIgnoreCase))
            request.Headers.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(backend.Token)));
        else if (backend.AuthType.Equals("header", StringComparison.OrdinalIgnoreCase))
            request.Headers.Add(backend.HeaderName ?? "X-Access-Token", backend.Token);
    }

    private async Task<ExecResult> ExecuteRestAsync(SkillDefinition skill, JsonElement args, IHttpClientFactory factory)
    {
        var backend = skill.Backend!;
        var url = backend.Url!;
        var remaining = new Dictionary<string, JsonElement>();
        if (args.ValueKind == JsonValueKind.Object)
            foreach (var parameter in args.EnumerateObject())
            {
                var placeholder = "{" + parameter.Name + "}";
                if (url.Contains(placeholder, StringComparison.Ordinal))
                    url = url.Replace(placeholder, Uri.EscapeDataString(parameter.Value.ToString()));
                else remaining[parameter.Name] = parameter.Value;
            }
        if (Regex.IsMatch(url, @"\{[a-zA-Z_][a-zA-Z0-9_]*\}"))
            return ExecResult.Fail("INVALID_PARAMS", "REST URL 存在未填写参数", 400);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            return ExecResult.Fail("INVALID_PARAMS", "REST URL 必须是 http/https 地址", 400);

        var method = new HttpMethod(backend.Method ?? "GET");
        if (method == HttpMethod.Get && remaining.Count > 0)
        {
            var query = string.Join("&", remaining.Select(p =>
                $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value.ToString())}"));
            url += (url.Contains('?') ? "&" : "?") + query;
        }
        using var request = new HttpRequestMessage(method, url);
        InjectAuthHeaders(request, backend);
        if (method != HttpMethod.Get)
            request.Content = new StringContent(JsonSerializer.Serialize(remaining), Encoding.UTF8, "application/json");

        var client = factory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(Math.Clamp(_config.GetValue("Relay:HttpTimeoutSeconds", 30), 1, 300));
        using var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            return ExecResult.Fail("BACKEND_ERROR", $"REST HTTP {(int)response.StatusCode}", (int)response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        try { return ExecResult.Success(JsonSerializer.Deserialize<JsonElement>(body)); }
        catch (JsonException) { return ExecResult.Success(body); }
    }

    private async Task<ExecResult> ExecuteSoapAsync(SkillDefinition skill, JsonElement args, IHttpClientFactory factory)
    {
        var backend = skill.Backend!;
        var body = backend.SoapBody;
        if (string.IsNullOrWhiteSpace(body) && !string.IsNullOrWhiteSpace(backend.SoapMethod))
        {
            if (!ParameterName.IsMatch(backend.SoapMethod))
                return ExecResult.Fail("INVALID_PARAMS", "SOAP 方法名格式不合法", 400);
            var sb = new StringBuilder();
            sb.Append("<soapenv:Envelope xmlns:soapenv=\"http://schemas.xmlsoap.org/soap/envelope/\" ");
            sb.Append("xmlns:tem=\"").Append(SecurityElement.Escape(backend.SoapNamespace ?? "")).Append("\">");
            sb.Append("<soapenv:Header/><soapenv:Body><tem:").Append(backend.SoapMethod).Append('>');
            if (args.ValueKind == JsonValueKind.Object)
                foreach (var property in args.EnumerateObject())
                    sb.Append("<tem:").Append(property.Name).Append('>')
                        .Append(SecurityElement.Escape(property.Value.ToString()))
                        .Append("</tem:").Append(property.Name).Append('>');
            sb.Append("</tem:").Append(backend.SoapMethod).Append("></soapenv:Body></soapenv:Envelope>");
            body = sb.ToString();
        }
        if (string.IsNullOrWhiteSpace(body))
            return ExecResult.Fail("INVALID_PARAMS", "未配置 SOAP body 或 soap_method", 400);
        if (!string.IsNullOrWhiteSpace(backend.SoapBody) && args.ValueKind == JsonValueKind.Object)
            foreach (var property in args.EnumerateObject())
                body = body.Replace("{" + property.Name + "}", SecurityElement.Escape(property.Value.ToString()));
        if (Regex.IsMatch(body, @"\{[a-zA-Z_][a-zA-Z0-9_]*\}"))
            return ExecResult.Fail("INVALID_PARAMS", "SOAP 模板存在未填写参数", 400);

        using var request = new HttpRequestMessage(HttpMethod.Post, backend.Url)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/xml")
        };
        if (!string.IsNullOrWhiteSpace(backend.SoapAction)) request.Headers.Add("SOAPAction", backend.SoapAction);
        InjectAuthHeaders(request, backend);
        var client = factory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(Math.Clamp(_config.GetValue("Relay:HttpTimeoutSeconds", 30), 1, 300));
        using var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            return ExecResult.Fail("BACKEND_ERROR", $"SOAP HTTP {(int)response.StatusCode}", (int)response.StatusCode);
        return ExecResult.Success(await response.Content.ReadAsStringAsync());
    }

    private Task<ExecResult> ExecutePowerShellAsync(SkillDefinition skill, JsonElement args)
        => Task.Run(() => InvokePowerShell(skill, args));

    private ExecResult InvokePowerShell(SkillDefinition skill, JsonElement args)
    {
        try
        {
            var backend = skill.Backend!;
            using var runspace = CreatePowerShellRunspace(backend.PowerShellTarget);
            runspace.Open();
            using var ps = PowerShell.Create();
            ps.Runspace = runspace;

            // Never interpolate argument values into source code. Bind positional
            // parameters to a scriptblock and refer to them as $variable in JSON.
            var names = skill.InputSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList();
            var preamble = "param(" + string.Join(", ", names.Select(n => "$" + n)) + ")\n";
            ps.AddScript(preamble + backend.ScriptTemplate, useLocalScope: true);
            foreach (var name in names)
            {
                object? value = null;
                if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var input))
                    value = input.ValueKind switch
                    {
                        JsonValueKind.String => input.GetString(),
                        JsonValueKind.Number when input.TryGetInt64(out var number) => number,
                        JsonValueKind.Number => input.GetDouble(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.Null => null,
                        _ => throw new ArgumentException("PowerShell 参数只接受简单类型")
                    };
                ps.AddArgument(value);
            }
            var output = ps.Invoke();
            if (ps.HadErrors)
            {
                _logger.LogWarning("PowerShell Skill {Skill} returned errors", skill.ToolName);
                return ExecResult.Fail("POWERSHELL_ERROR", "PowerShell 执行失败，请检查服务端日志", 502);
            }
            return ExecResult.Success(output.Select(ToSerializablePowerShellValue).ToArray());
        }
        catch (Exception ex)
        {
            _logger.LogWarning("PowerShell Skill {Skill} failed: {Type}", skill.ToolName, ex.GetType().Name);
            return ExecResult.Fail("EXECUTION_ERROR", $"PowerShell 执行失败 ({ex.GetType().Name})", 502);
        }
    }

    private static object? ToSerializablePowerShellValue(object? input)
    {
        if (input is null) return null;
        if (input is PSObject ps)
        {
            if (ps.BaseObject is PSCustomObject)
                return ps.Properties.ToDictionary(p => p.Name,
                    p => ToSerializablePowerShellValue(p.Value), StringComparer.OrdinalIgnoreCase);
            return ToSerializablePowerShellValue(ps.BaseObject);
        }
        if (input is string or bool or byte or short or int or long or float or double or decimal or DateTime or DateTimeOffset or Guid)
            return input;
        if (input is IDictionary dictionary)
        {
            var result = new Dictionary<string, object?>();
            foreach (DictionaryEntry entry in dictionary)
                result[entry.Key?.ToString() ?? "null"] = ToSerializablePowerShellValue(entry.Value);
            return result;
        }
        if (input is IEnumerable list && input is not string)
            return list.Cast<object?>().Take(1000).Select(ToSerializablePowerShellValue).ToArray();
        // Avoid circular references and giant embedded PowerShell objects.
        return input.ToString();
    }

    private Runspace CreatePowerShellRunspace(string? target)
    {
        if (target == "local") return RunspaceFactory.CreateRunspace();
        var url = _config["PowerShellSettings:ExchangeServerUrl"];
        var username = _config["PowerShellSettings:Username"];
        var password = _config["PowerShellSettings:Password"];
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("缺少远程 Exchange 凭证");
        var secure = new SecureString();
        foreach (var character in password) secure.AppendChar(character);
        secure.MakeReadOnly();
        var credentials = new PSCredential(username, secure);
        var connection = new WSManConnectionInfo(new Uri(url),
            "http://schemas.microsoft.com/powershell/Microsoft.Exchange", credentials);
        return RunspaceFactory.CreateRunspace(connection);
    }

    public void Dispose()
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnFileChanged;
            _watcher.Created -= OnFileChanged;
            _watcher.Deleted -= OnFileChanged;
            _watcher.Renamed -= OnFileChanged;
            _watcher.Dispose();
        }
    }
}
