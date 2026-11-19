using System.DirectoryServices.Protocols;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RelayMcp;

/// <summary>
/// Read-only, configuration-scoped LDAP searches. This executor deliberately does not
/// expose a free-form LDAP query or any LDAP write operation to MCP clients.
/// </summary>
public sealed class LdapQueryExecutor
{
    private static readonly Regex ParamRef = new(@"\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled);
    private static readonly Regex AttributeName = new(@"^[a-zA-Z][a-zA-Z0-9-]*$", RegexOptions.Compiled);
    private readonly IConfiguration _settings;
    private readonly ILogger<LdapQueryExecutor> _logger;

    public LdapQueryExecutor(IConfiguration settings, ILogger<LdapQueryExecutor> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public static void ValidateSkillTemplate(string? server, string? filter, List<string>? attributes,
        string? scope, string? baseDn, Dictionary<string, string>? parameters)
    {
        if (string.IsNullOrWhiteSpace(server) || !Regex.IsMatch(server, @"^[A-Za-z0-9_-]+$"))
            throw new FormatException("LDAP Skill 必须提供 ldap_server 别名");
        if (string.IsNullOrWhiteSpace(filter) || filter.Length > 2048 || !filter.StartsWith('(') || !filter.EndsWith(')'))
            throw new FormatException("LDAP Skill 必须提供合理的 ldap_filter");
        if (attributes is null || attributes.Count == 0 || attributes.Count > 30 ||
            attributes.Any(a => !AttributeName.IsMatch(a)))
            throw new FormatException("LDAP Skill 必须明确列出 1-30 个合法的 ldap_attributes");
        if (scope is not null && scope is not ("base" or "one" or "sub"))
            throw new FormatException("ldap_scope 仅支持 base/one/sub");
        if (baseDn is not null && (baseDn.Length > 512 || baseDn.Contains('\0')))
            throw new FormatException("ldap_base_dn 不合法");
        var declared = parameters?.Keys.ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>();
        foreach (Match m in ParamRef.Matches(filter))
            if (!declared.Contains(m.Groups[1].Value))
                throw new FormatException($"LDAP 过滤器参数 {m.Groups[1].Value} 未在 params 中声明");
    }

    public async Task<ExecResult> ExecuteAsync(BackendConfig skill, JsonElement args)
    {
        if (!_settings.GetValue("ActiveDirectory:Enabled", false))
            return ExecResult.Fail("LDAP_DISABLED", "ActiveDirectory:Enabled 尚未启用", 403);

        var section = _settings.GetSection($"ActiveDirectory:Servers:{skill.LdapServer}");
        var host = section["Host"];
        var baseDn = section["BaseDn"];
        var user = section["Username"];
        var passwordEnv = section["PasswordEnv"];
        var password = string.IsNullOrWhiteSpace(passwordEnv) ? null : Environment.GetEnvironmentVariable(passwordEnv);
        var allowed = section.GetSection("AllowedAttributes").Get<string[]>() ?? Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(baseDn) ||
            string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(password) || allowed.Length == 0)
            return ExecResult.Fail("LDAP_CONFIG", "AD 服务器地址、搜索范围、查询账号、密码环境变量或属性白名单未配置", 500);
        if (!section.GetValue("UseSsl", true) || section.GetValue("Port", 636) <= 0 ||
            !string.Equals(section["AuthType"], "Basic", StringComparison.OrdinalIgnoreCase))
            return ExecResult.Fail("LDAP_CONFIG", "当前非域 LDAP 连接仅支持 LDAPS + Basic Bind（UPN/DN 账号）", 500);

        var queryDn = skill.LdapBaseDn ?? baseDn;
        if (!DnWithinBase(queryDn, baseDn))
            return ExecResult.Fail("LDAP_CONFIG", "Skill 的 ldap_base_dn 超出服务器允许的 BaseDn", 403);
        var allow = allowed.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (skill.LdapAttributes is null || skill.LdapAttributes.Any(a => !allow.Contains(a)))
            return ExecResult.Fail("LDAP_CONFIG", "Skill 请求了未在 AllowedAttributes 中授权的属性", 403);
        var filter = ParamRef.Replace(skill.LdapFilter!, m =>
        {
            var p = m.Groups[1].Value;
            return args.ValueKind == JsonValueKind.Object && args.TryGetProperty(p, out var value)
                ? EscapeFilter(value.ToString()) : "";
        });
        var scope = (skill.LdapScope ?? "sub") switch
        {
            "base" => SearchScope.Base,
            "one" => SearchScope.OneLevel,
            _ => SearchScope.Subtree
        };
        var limit = Math.Clamp(section.GetValue("MaxResults", 100), 1, 500);
        var seconds = Math.Clamp(section.GetValue("TimeoutSeconds", 15), 2, 60);

        try
        {
            // System.DirectoryServices.Protocols executes synchronously. Keep it off the request thread.
            var result = await Task.Run(() => Search(host, section.GetValue("Port", 636), user,
                password, queryDn, filter, scope, skill.LdapAttributes!, limit, seconds));
            return ExecResult.Success(new
            {
                server = skill.LdapServer,
                base_dn = queryDn,
                returned = result.Count,
                result_limit = limit,
                limit_reached = result.Count >= limit,
                group_membership = "memberOf/member 仅表示直接关系；不包含完整嵌套组和主组计算",
                entries = result
            });
        }
        catch (LdapException ex)
        {
            _logger.LogWarning("LDAP search failed on {Server}: {Code}", skill.LdapServer, ex.ErrorCode);
            return ExecResult.Fail("LDAP_ERROR", $"AD 查询失败（LDAP code {ex.ErrorCode}），请检查认证、TLS、权限和筛选条件", 502);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("LDAP search failed on {Server}: {Kind}", skill.LdapServer, ex.GetType().Name);
            return ExecResult.Fail("LDAP_ERROR", $"AD 查询失败（{ex.GetType().Name}）", 502);
        }
    }

    private static List<Dictionary<string, object?>> Search(string host, int port, string username, string password,
        string dn, string filter, SearchScope scope, List<string> attrs, int limit, int seconds)
    {
        using var connection = new LdapConnection(new LdapDirectoryIdentifier(host, port));
        connection.AuthType = AuthType.Basic;
        connection.Credential = new NetworkCredential(username, password);
        connection.SessionOptions.ProtocolVersion = 3;
        connection.SessionOptions.SecureSocketLayer = true;
        connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;
        // Do NOT use VerifyServerCertificate override: the server certificate must chain
        // to a trusted CA and match the configured Host.
        connection.Timeout = TimeSpan.FromSeconds(seconds);
        connection.Bind();

        var req = new SearchRequest(dn, filter, scope, attrs.ToArray())
        {
            TimeLimit = TimeSpan.FromSeconds(seconds)
        };
        var result = new List<Dictionary<string, object?>>();
        var cookie = Array.Empty<byte>();
        do
        {
            req.Controls.Clear();
            req.Controls.Add(new PageResultRequestControl(Math.Min(100, limit - result.Count)) { Cookie = cookie });
            var response = (SearchResponse)connection.SendRequest(req);
            foreach (SearchResultEntry entry in response.Entries)
            {
                var item = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["distinguishedName"] = entry.DistinguishedName
                };
                foreach (DirectoryAttribute attribute in entry.Attributes.Values)
                {
                    var values = attribute.GetValues(typeof(string)).Cast<string>().ToArray();
                    item[attribute.Name] = values.Length == 1 ? values[0] : values;
                }
                AddAccountStatus(item);
                result.Add(item);
                if (result.Count >= limit) break;
            }
            if (result.Count >= limit) break;
            cookie = response.Controls.OfType<PageResultResponseControl>().FirstOrDefault()?.Cookie ?? Array.Empty<byte>();
        } while (cookie.Length > 0);
        return result;
    }

    private static void AddAccountStatus(Dictionary<string, object?> item)
    {
        if (item.TryGetValue("userAccountControl", out var value) && int.TryParse(value?.ToString(), out var flags))
            item["enabled"] = (flags & 0x2) == 0;
        if (item.TryGetValue("msDS-User-Account-Control-Computed", out value) &&
            int.TryParse(value?.ToString(), out flags))
            item["locked"] = (flags & 0x10) != 0;
        foreach (var attr in new[] { "pwdLastSet", "accountExpires", "lockoutTime" })
        {
            if (!item.TryGetValue(attr, out value) || !long.TryParse(value?.ToString(), out var ticks)) continue;
            if (ticks <= 0 || (attr == "accountExpires" && ticks == long.MaxValue)) continue;
            try { item[attr + "Utc"] = DateTime.FromFileTimeUtc(ticks).ToString("O"); }
            catch (ArgumentOutOfRangeException) { /* preserve raw value */ }
        }
    }

    public static string EscapeFilter(string text)
    {
        var b = new StringBuilder(text.Length);
        foreach (var ch in text)
            b.Append(ch switch
            {
                '*' => @"\2a", '(' => @"\28", ')' => @"\29", '\\' => @"\5c", '\0' => @"\00",
                _ => ch.ToString()
            });
        return b.ToString();
    }

    private static bool DnWithinBase(string child, string baseDn) =>
        child.Trim().Equals(baseDn.Trim(), StringComparison.OrdinalIgnoreCase) ||
        child.Trim().EndsWith("," + baseDn.Trim(), StringComparison.OrdinalIgnoreCase);
}
