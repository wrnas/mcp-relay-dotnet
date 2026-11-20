using System.DirectoryServices.Protocols;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RelayMcp;

/// <summary>
/// 固定的 AD 写入动作。Skill 只选择动作及参数，不接受任意 LDAP 写请求。
/// 写入启用与审批仍然完全由 SkillRegistry 的现有机制负责。
/// </summary>
public sealed class LdapMutationExecutor
{
    private static readonly HashSet<string> Actions = new(StringComparer.Ordinal)
    {
        "ensure_user_in_group", "remove_user_from_group", "ensure_user_in_ou",
        "create_user", "delete_user", "set_user_attribute", "enable_user", "disable_user",
        "unlock_user", "reset_user_password", "create_group", "delete_group",
        "set_group_description", "create_ou", "delete_ou",
        "enable_computer", "disable_computer", "delete_computer"
    };

    private static readonly HashSet<string> EditableUserAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "displayName", "givenName", "sn", "mail", "department", "title",
        "telephoneNumber", "mobile", "physicalDeliveryOfficeName", "company",
        "description", "employeeID", "streetAddress", "l", "st", "postalCode"
    };

    private readonly IConfiguration _settings;
    private readonly ILogger<LdapMutationExecutor> _logger;

    public LdapMutationExecutor(IConfiguration settings, ILogger<LdapMutationExecutor> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public static void ValidateSkillTemplate(string? server, string? action, Dictionary<string, string>? parameters)
    {
        if (string.IsNullOrWhiteSpace(server) || !Regex.IsMatch(server, @"^[A-Za-z0-9_-]+$"))
            throw new FormatException("AD 写入 Skill 必须指定 ldap_server 别名");
        if (string.IsNullOrWhiteSpace(action) || !Actions.Contains(action))
            throw new FormatException($"AD 写入动作不受支持: {action}");
        if (parameters is null || parameters.Count == 0)
            throw new FormatException("AD 写入 Skill 必须声明 params");
    }

    public async Task<ExecResult> ExecuteAsync(BackendConfig skill, JsonElement args)
    {
        if (!_settings.GetValue("ActiveDirectory:Enabled", false))
            return ExecResult.Fail("LDAP_DISABLED", "ActiveDirectory 尚未启用", 403);
        var alias = skill.LdapServer;
        var section = _settings.GetSection($"ActiveDirectory:Servers:{alias}");
        if (string.IsNullOrWhiteSpace(alias) || !section.Exists())
            return ExecResult.Fail("LDAP_CONFIG", $"未找到 AD 配置：{alias}", 500);
        var host = section["Host"];
        var baseDn = section["BaseDn"];
        var user = section["Username"];
        var passEnv = section["PasswordEnv"];
        var password = string.IsNullOrWhiteSpace(passEnv)
            ? section["Password"] : Environment.GetEnvironmentVariable(passEnv);
        var port = section.GetValue("Port", 636);
        var seconds = Math.Clamp(section.GetValue("TimeoutSeconds", 15), 2, 60);
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(baseDn) ||
            string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(password))
            return ExecResult.Fail("LDAP_CONFIG", "缺少 AD Host、BaseDn 或连接凭据", 500);
        if (!section.GetValue("UseSsl", true) || port <= 0 ||
            !string.Equals(section["AuthType"], "Basic", StringComparison.OrdinalIgnoreCase))
            return ExecResult.Fail("LDAP_CONFIG", "AD 写入当前仅支持 LDAPS + Basic", 500);
        var allowUntrusted = section.GetValue("AllowUntrustedCertificate", false);
        if (allowUntrusted)
            _logger.LogWarning("AD 写入 {Action} 使用跳过服务器证书验证的 LDAPS 测试配置，服务器别名 {Alias}", skill.LdapAction, alias);

        try
        {
            var result = await Task.Run(() => Perform(skill.LdapAction!, args, host, port,
                user, password!, baseDn, seconds, allowUntrusted));
            _logger.LogInformation("AD 写入完成：{Action}, {Status}, {Alias}", skill.LdapAction, result.Status, alias);
            return ExecResult.Success(new { server = alias, action = skill.LdapAction,
                status = result.Status, changed = result.Changed, distinguishedName = result.Dn });
        }
        catch (ActionError ex)
        {
            return ExecResult.Fail(ex.ErrorType, ex.Message, ex.HttpCode);
        }
        catch (LdapException ex)
        {
            _logger.LogWarning(ex, "AD 写入 LDAP 失败：{Action}, code={Code}", skill.LdapAction, ex.ErrorCode);
            return ExecResult.Fail("LDAP_WRITE_ERROR", $"AD 写入失败（LDAP {ex.ErrorCode}）", 502);
        }
        catch (DirectoryOperationException ex)
        {
            _logger.LogWarning(ex, "AD 写入目录操作失败：{Action}", skill.LdapAction);
            return ExecResult.Fail("LDAP_WRITE_ERROR", $"AD 拒绝操作（{ex.Response?.ResultCode}）；详情请看服务端日志", 502);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AD 写入失败：{Action}", skill.LdapAction);
            return ExecResult.Fail("LDAP_WRITE_ERROR", $"AD 写入失败（{ex.GetType().Name}）", 502);
        }
    }

    private static ActionResult Perform(string action, JsonElement args, string host, int port,
        string username, string password, string baseDn, int timeout, bool allowUntrusted)
    {
        using var ldap = new LdapConnection(new LdapDirectoryIdentifier(host, port))
        {
            AuthType = AuthType.Basic,
            Credential = new NetworkCredential(username, password),
            Timeout = TimeSpan.FromSeconds(timeout)
        };
        ldap.SessionOptions.ProtocolVersion = 3;
        ldap.SessionOptions.SecureSocketLayer = true;
        ldap.SessionOptions.ReferralChasing = ReferralChasingOptions.None;
        if (allowUntrusted)
            ldap.SessionOptions.VerifyServerCertificate = (_, _) => true;
        ldap.Bind();

        switch (action)
        {
            case "ensure_user_in_group":
            case "remove_user_from_group":
            {
                var user = FindUser(ldap, baseDn, Arg(args, "username"));
                var groupDn = FindGroup(ldap, baseDn, Arg(args, "group"));
                var isMember = EntryAt(ldap, groupDn,
                    "(&(objectClass=group)(member=" + LdapQueryExecutor.EscapeFilter(user.DistinguishedName) + "))") is not null;
                if (action == "ensure_user_in_group")
                {
                    if (isMember) return new("already_member", false, groupDn);
                    Modify(ldap, groupDn, "member", DirectoryAttributeOperation.Add, user.DistinguishedName);
                    return new("added", true, groupDn);
                }
                if (!isMember) return new("already_absent", false, groupDn);
                Modify(ldap, groupDn, "member", DirectoryAttributeOperation.Delete, user.DistinguishedName);
                return new("removed", true, groupDn);
            }
            case "ensure_user_in_ou":
            {
                var user = FindUser(ldap, baseDn, Arg(args, "username"));
                var target = Within(Arg(args, "ouDn"), baseDn);
                NeedEntry(ldap, target, "(objectClass=organizationalUnit)", "OU");
                if (ParentDn(user.DistinguishedName).Equals(target, StringComparison.OrdinalIgnoreCase))
                    return new("already_in_ou", false, user.DistinguishedName);
                var move = new ModifyDNRequest(user.DistinguishedName, target, Rdn(user.DistinguishedName))
                {
                    DeleteOldRdn = true
                };
                ldap.SendRequest(move);
                return new("moved", true, Rdn(user.DistinguishedName) + "," + target);
            }
            case "create_user":
            {
                var sam = Arg(args, "username");
                var cn = Arg(args, "cn");
                var parent = Within(Arg(args, "ouDn"), baseDn);
                NeedEntry(ldap, parent, "(objectClass=organizationalUnit)", "OU");
                if (FindBySam(ldap, baseDn, sam, "user") is not null)
                    return new("already_exists", false, sam);
                var dn = "CN=" + EscapeRdn(cn) + "," + parent;
                var attrs = new List<DirectoryAttribute>
                {
                    new("objectClass", "top", "person", "organizationalPerson", "user"),
                    new("cn", cn), new("sAMAccountName", sam),
                    new("userAccountControl", "514") // 账户初始禁用，单独设置密码和启用
                };
                AddOptional(attrs, args, "displayName", "displayName");
                AddOptional(attrs, args, "mail", "mail");
                AddOptional(attrs, args, "department", "department");
                ldap.SendRequest(new AddRequest(dn, attrs.ToArray()));
                return new("created_disabled", true, dn);
            }
            case "delete_user":
            {
                var user = FindBySam(ldap, baseDn, Arg(args, "username"), "user");
                if (user is null) return new("already_absent", false, null);
                ldap.SendRequest(new DeleteRequest(user.DistinguishedName));
                return new("deleted", true, user.DistinguishedName);
            }
            case "set_user_attribute":
            {
                var user = FindUser(ldap, baseDn, Arg(args, "username"));
                var attr = Arg(args, "attribute");
                if (!EditableUserAttributes.Contains(attr))
                    throw new ActionError("INVALID_PARAMS", $"不支持修改用户属性 {attr}", 400);
                var value = Arg(args, "value", allowEmpty: true);
                Modify(ldap, user.DistinguishedName, attr,
                    string.IsNullOrEmpty(value) ? DirectoryAttributeOperation.Delete : DirectoryAttributeOperation.Replace,
                    string.IsNullOrEmpty(value) ? null : value);
                return new("updated", true, user.DistinguishedName);
            }
            case "enable_user":
            case "disable_user":
            {
                var user = FindUser(ldap, baseDn, Arg(args, "username"));
                return SetEnabled(ldap, user, action == "enable_user");
            }
            case "unlock_user":
            {
                var user = FindUser(ldap, baseDn, Arg(args, "username"));
                Modify(ldap, user.DistinguishedName, "lockoutTime", DirectoryAttributeOperation.Replace, "0");
                return new("unlocked", true, user.DistinguishedName);
            }
            case "reset_user_password":
            {
                var user = FindUser(ldap, baseDn, Arg(args, "username"));
                var newPassword = Arg(args, "password");
                var change = new DirectoryAttributeModification
                {
                    Name = "unicodePwd", Operation = DirectoryAttributeOperation.Replace
                };
                change.Add(Encoding.Unicode.GetBytes("\"" + newPassword + "\""));
                ldap.SendRequest(new ModifyRequest(user.DistinguishedName, change));
                return new("password_reset", true, user.DistinguishedName);
            }
            case "create_group":
            {
                var cn = Arg(args, "cn");
                var sam = Optional(args, "samAccountName") ?? cn;
                var parent = Within(Arg(args, "ouDn"), baseDn);
                NeedEntry(ldap, parent, "(objectClass=organizationalUnit)", "OU");
                var dn = "CN=" + EscapeRdn(cn) + "," + parent;
                if (EntryAt(ldap, dn, "(objectClass=group)") is not null)
                    return new("already_exists", false, dn);
                var attrs = new List<DirectoryAttribute>
                {
                    new("objectClass", "top", "group"), new("cn", cn),
                    new("sAMAccountName", sam), new("groupType", "-2147483646") // Global Security
                };
                AddOptional(attrs, args, "description", "description");
                ldap.SendRequest(new AddRequest(dn, attrs.ToArray()));
                return new("created", true, dn);
            }
            case "delete_group":
            {
                var dn = FindGroup(ldap, baseDn, Arg(args, "group"));
                ldap.SendRequest(new DeleteRequest(dn));
                return new("deleted", true, dn);
            }
            case "set_group_description":
            {
                var dn = FindGroup(ldap, baseDn, Arg(args, "group"));
                var value = Arg(args, "description", allowEmpty: true);
                Modify(ldap, dn, "description",
                    string.IsNullOrEmpty(value) ? DirectoryAttributeOperation.Delete : DirectoryAttributeOperation.Replace,
                    string.IsNullOrEmpty(value) ? null : value);
                return new("updated", true, dn);
            }
            case "create_ou":
            {
                var ou = Arg(args, "ou");
                var parent = Within(Arg(args, "parentDn"), baseDn);
                NeedEntry(ldap, parent, "(|(objectClass=organizationalUnit)(objectClass=domainDNS))", "父容器");
                var dn = "OU=" + EscapeRdn(ou) + "," + parent;
                if (EntryAt(ldap, dn, "(objectClass=organizationalUnit)") is not null)
                    return new("already_exists", false, dn);
                ldap.SendRequest(new AddRequest(dn,
                    new DirectoryAttribute("objectClass", "top", "organizationalUnit"),
                    new DirectoryAttribute("ou", ou)));
                return new("created", true, dn);
            }
            case "delete_ou":
            {
                var dn = Within(Arg(args, "ouDn"), baseDn);
                if (EntryAt(ldap, dn, "(objectClass=organizationalUnit)") is null)
                    return new("already_absent", false, dn);
                ldap.SendRequest(new DeleteRequest(dn)); // 非空OU由AD拒绝，不做递归删除
                return new("deleted", true, dn);
            }
            case "enable_computer":
            case "disable_computer":
            case "delete_computer":
            {
                var name = Arg(args, "computerName");
                if (!name.EndsWith('$')) name += "$";
                var computer = FindBySam(ldap, baseDn, name, "computer");
                if (computer is null)
                {
                    if (action == "delete_computer") return new("already_absent", false, null);
                    throw new ActionError("NOT_FOUND", "找不到指定计算机账号", 404);
                }
                if (action == "delete_computer")
                {
                    ldap.SendRequest(new DeleteRequest(computer.DistinguishedName));
                    return new("deleted", true, computer.DistinguishedName);
                }
                return SetEnabled(ldap, computer, action == "enable_computer");
            }
            default:
                throw new ActionError("INVALID_ACTION", "未知 AD 写入动作", 400);
        }
    }

    private static ActionResult SetEnabled(LdapConnection ldap, SearchResultEntry entry, bool enable)
    {
        var raw = Attribute(entry, "userAccountControl");
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var flags))
            throw new ActionError("LDAP_DATA", "读取账号状态失败", 502);
        const int disabledFlag = 0x2;
        var changedFlags = enable ? flags & ~disabledFlag : flags | disabledFlag;
        if (flags == changedFlags)
            return new(enable ? "already_enabled" : "already_disabled", false, entry.DistinguishedName);
        Modify(ldap, entry.DistinguishedName, "userAccountControl", DirectoryAttributeOperation.Replace,
            changedFlags.ToString(CultureInfo.InvariantCulture));
        return new(enable ? "enabled" : "disabled", true, entry.DistinguishedName);
    }

    private static SearchResultEntry FindUser(LdapConnection ldap, string baseDn, string username) =>
        FindBySam(ldap, baseDn, username, "user") ??
        throw new ActionError("NOT_FOUND", "找不到指定 AD 用户", 404);

    private static SearchResultEntry? FindBySam(LdapConnection ldap, string baseDn, string sam, string objectClass)
    {
        var escaped = LdapQueryExecutor.EscapeFilter(sam);
        var request = new SearchRequest(baseDn,
            "(&(objectCategory=" + objectClass + ")(sAMAccountName=" + escaped + "))",
            SearchScope.Subtree, "userAccountControl");
        request.SizeLimit = 2;
        var response = (SearchResponse)ldap.SendRequest(request);
        if (response.Entries.Count > 1)
            throw new ActionError("AMBIGUOUS", "搜索命中多个对象，已停止修改", 409);
        return response.Entries.Count == 0 ? null : response.Entries[0];
    }

    // 组可以传 sAMAccountName、CN 或完整 DN；重名时明确拒绝，不猜目标。
    private static string FindGroup(LdapConnection ldap, string baseDn, string group)
    {
        if (group.StartsWith("CN=", StringComparison.OrdinalIgnoreCase) &&
            group.EndsWith("," + baseDn.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            var dn = Within(group, baseDn);
            NeedEntry(ldap, dn, "(objectClass=group)", "AD组");
            return dn;
        }
        var escaped = LdapQueryExecutor.EscapeFilter(group);
        var filter = "(&(objectClass=group)(|(sAMAccountName=" + escaped + ")(cn=" + escaped + ")))";
        var request = new SearchRequest(baseDn, filter, SearchScope.Subtree, "distinguishedName")
        {
            SizeLimit = 2
        };
        var response = (SearchResponse)ldap.SendRequest(request);
        if (response.Entries.Count == 0)
            throw new ActionError("NOT_FOUND", "找不到指定 AD 组", 404);
        if (response.Entries.Count > 1)
            throw new ActionError("AMBIGUOUS", "组名对应多个 AD 组，请改用完整 DN", 409);
        return response.Entries[0].DistinguishedName;
    }

    private static SearchResultEntry? EntryAt(LdapConnection ldap, string dn, string filter)
    {
        try
        {
            var response = (SearchResponse)ldap.SendRequest(new SearchRequest(dn, filter, SearchScope.Base, "distinguishedName"));
            return response.Entries.Count == 0 ? null : response.Entries[0];
        }
        catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.NoSuchObject)
        {
            return null;
        }
    }

    private static void NeedEntry(LdapConnection ldap, string dn, string filter, string kind)
    {
        if (EntryAt(ldap, dn, filter) is null)
            throw new ActionError("NOT_FOUND", $"找不到目标{kind}，请核对 DN", 404);
    }

    private static void Modify(LdapConnection ldap, string dn, string attribute,
        DirectoryAttributeOperation operation, string? value)
    {
        var change = new DirectoryAttributeModification { Name = attribute, Operation = operation };
        if (value is not null) change.Add(value);
        ldap.SendRequest(new ModifyRequest(dn, change));
    }

    private static void AddOptional(List<DirectoryAttribute> attrs, JsonElement args, string param, string attribute)
    {
        var value = Optional(args, param);
        if (!string.IsNullOrWhiteSpace(value)) attrs.Add(new DirectoryAttribute(attribute, value));
    }

    private static string Arg(JsonElement args, string name, bool allowEmpty = false)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String)
            throw new ActionError("INVALID_PARAMS", $"缺少必填参数：{name}", 400);
        var value = property.GetString() ?? "";
        if (!allowEmpty && string.IsNullOrWhiteSpace(value))
            throw new ActionError("INVALID_PARAMS", $"参数 {name} 不能为空", 400);
        return value;
    }

    private static string? Optional(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Within(string dn, string baseDn)
    {
        dn = dn.Trim();
        baseDn = baseDn.Trim();
        if (dn.Equals(baseDn, StringComparison.OrdinalIgnoreCase) ||
            dn.EndsWith("," + baseDn, StringComparison.OrdinalIgnoreCase))
            return dn;
        throw new ActionError("INVALID_PARAMS", "目标 DN 不在配置的 BaseDn 范围内", 400);
    }

    private static string? Attribute(SearchResultEntry entry, string key)
    {
        if (!entry.Attributes.Contains(key)) return null;
        return entry.Attributes[key]?.GetValues(typeof(string)).Cast<string>().FirstOrDefault();
    }

    // 识别未转义的第一个逗号；保留用户原有 RDN，不错误截断包含 \, 的 CN。
    private static int DnComma(string dn)
    {
        var escaped = false;
        for (var i = 0; i < dn.Length; i++)
        {
            if (escaped) { escaped = false; continue; }
            if (dn[i] == '\\') { escaped = true; continue; }
            if (dn[i] == ',') return i;
        }
        throw new ActionError("LDAP_DATA", "对象 DN 格式不正确", 502);
    }

    private static string Rdn(string dn) => dn[..DnComma(dn)];
    private static string ParentDn(string dn) => dn[(DnComma(dn) + 1)..];

    private static string EscapeRdn(string value)
    {
        if (value.Length == 0) throw new ActionError("INVALID_PARAMS", "名称不能为空", 400);
        var sb = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '\0') throw new ActionError("INVALID_PARAMS", "名称不能含 NUL", 400);
            if (",+\"\\<>;=".Contains(c) || (i == 0 && (c == ' ' || c == '#')) ||
                (i == value.Length - 1 && c == ' ')) sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    private sealed record ActionResult(string Status, bool Changed, string? Dn);

    private sealed class ActionError : Exception
    {
        public string ErrorType { get; }
        public int HttpCode { get; }
        public ActionError(string type, string message, int code) : base(message)
        {
            ErrorType = type;
            HttpCode = code;
        }
    }
}
