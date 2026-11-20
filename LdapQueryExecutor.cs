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

        // Skill 只提供服务器别名，禁止 AI 随意指定域控地址。
        var alias = skill.LdapServer;
        var section = _settings.GetSection($"ActiveDirectory:Servers:{alias}");
        if (string.IsNullOrWhiteSpace(alias) || !section.Exists())
            return ExecResult.Fail("LDAP_CONFIG", $"未找到 AD 服务器配置 ActiveDirectory:Servers:{alias}", 500);

        var host = section["Host"];
        var baseDn = section["BaseDn"];
        var user = section["Username"];
        var passwordEnv = section["PasswordEnv"];
        var configuredPassword = section["Password"];

        // 个人助手提供两种密码配置方式，减少首次接入的配置门槛：
        // 1. Password：直接从本机配置文件读取（适合单机测试、私有部署）。
        // 2. PasswordEnv：从运行进程的环境变量读取（适合长期部署）。
        // 如果两项都配置，优先使用 PasswordEnv；变量不存在时明确报错，不悄悄回退到旧密码。
        // 注意：不要将真实密码写入日志、API 返回值或公开 Git 仓库。
        if (string.IsNullOrWhiteSpace(host))
            return ExecResult.Fail("LDAP_CONFIG", $"ActiveDirectory:Servers:{alias}:Host 未配置", 500);
        if (string.IsNullOrWhiteSpace(baseDn))
            return ExecResult.Fail("LDAP_CONFIG", $"ActiveDirectory:Servers:{alias}:BaseDn 未配置", 500);
        if (string.IsNullOrWhiteSpace(user))
            return ExecResult.Fail("LDAP_CONFIG", $"ActiveDirectory:Servers:{alias}:Username 未配置", 500);
        string? password;
        if (!string.IsNullOrWhiteSpace(passwordEnv))
        {
            // 环境变量必须存在于 RelayMcp.exe 进程中；其他终端设置不会自动生效。
            password = Environment.GetEnvironmentVariable(passwordEnv);
            if (string.IsNullOrEmpty(password))
                return ExecResult.Fail("LDAP_CONFIG",
                    $"没有读取到密码环境变量 {passwordEnv}；请检查进程环境，或删除 PasswordEnv 并改用 Password 直接配置", 500);
        }
        else
        {
            password = configuredPassword;
            if (string.IsNullOrEmpty(password))
                return ExecResult.Fail("LDAP_CONFIG",
                    $"ActiveDirectory:Servers:{alias} 未配置 LDAP 密码：请填写 Password 或 PasswordEnv", 500);
        }

        // 非域环境当前只允许 LDAPS + Basic（Simple Bind），禁止密码通过明文 LDAP 传输。
        if (!section.GetValue("UseSsl", true) || section.GetValue("Port", 636) <= 0 ||
            !string.Equals(section["AuthType"], "Basic", StringComparison.OrdinalIgnoreCase))
            return ExecResult.Fail("LDAP_CONFIG", "当前非域 LDAP 连接仅支持 LDAPS + Basic Bind（UPN/DN 账号）", 500);

        var queryDn = skill.LdapBaseDn ?? baseDn;
        if (!DnWithinBase(queryDn, baseDn))
            return ExecResult.Fail("LDAP_CONFIG", "Skill 的 ldap_base_dn 超出服务器允许的 BaseDn", 403);
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
        // 仅供隔离的开发测试环境使用；未配置时默认严格验证 LDAPS 证书。
        var allowUntrustedCertificate = section.GetValue("AllowUntrustedCertificate", false);
        if (allowUntrustedCertificate)
            _logger.LogWarning("AD LDAP 服务器别名 {Server} 已跳过 LDAPS 证书验证（仅供测试，不适用于生产）", alias);

        try
        {
            // System.DirectoryServices.Protocols executes synchronously. Keep it off the request thread.
            var result = await Task.Run(() => Search(host, section.GetValue("Port", 636), user,
                password, queryDn, filter, scope, skill.LdapAttributes!, limit, seconds,
                allowUntrustedCertificate));
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
            // 先记录实际失败阶段和原始异常，方便从服务器日志定位 TLS/认证问题。
            // 错误信息只展示目标别名、阶段、LDAP 错误码，不回显用户名或密码。
            var phase = ex.Data["LDAP_PHASE"]?.ToString() ?? "LDAP查询";
            _logger.LogWarning(ex, "AD LDAP 请求失败；服务器别名 {Server}，阶段 {Phase}，错误码 {Code}",
                skill.LdapServer, phase, ex.ErrorCode);
            if (ex.ErrorCode == 81)
                return ExecResult.Fail("LDAP_CONNECT",
                    $"AD {phase}失败（LDAP 81 / LDAP_SERVER_DOWN）。端口通不代表 TLS 握手成功；请在网关机器上用 LDP 勾选 SSL 测试域控 FQDN:636，并检查证书名称、证书信任链及 TLS。", 502);
            if (ex.ErrorCode == 49)
                return ExecResult.Fail("LDAP_AUTH", "AD 身份认证失败（LDAP 49）；请核对账号格式、密码及账号状态", 502);
            return ExecResult.Fail("LDAP_ERROR", $"AD {phase}失败（LDAP {ex.ErrorCode}）；请查看网关日志以获取具体错误", 502);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("LDAP search failed on {Server}: {Kind}", skill.LdapServer, ex.GetType().Name);
            return ExecResult.Fail("LDAP_ERROR", $"AD 查询失败（{ex.GetType().Name}）", 502);
        }
    }

    private static List<Dictionary<string, object?>> Search(string host, int port, string username, string password,
        string dn, string filter, SearchScope scope, List<string> attrs, int limit, int seconds,
        bool allowUntrustedCertificate)
    {
        using var connection = new LdapConnection(new LdapDirectoryIdentifier(host, port));
        connection.AuthType = AuthType.Basic;
        connection.Credential = new NetworkCredential(username, password);
        connection.SessionOptions.ProtocolVersion = 3;
        connection.SessionOptions.SecureSocketLayer = true;
        connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;
        // 仅在显式启用测试开关时跳过证书验证；默认保留正常的证书链及主机名验证。
        if (allowUntrustedCertificate)
            connection.SessionOptions.VerifyServerCertificate = (_, _) => true;
        connection.Timeout = TimeSpan.FromSeconds(seconds);
        try
        {
            // Bind 既包含建立 LDAPS/TLS 会话，也包含账号认证。
            connection.Bind();
        }
        catch (LdapException ex)
        {
            ex.Data["LDAP_PHASE"] = "连接/认证";
            throw;
        }

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
            SearchResponse response;
            try
            {
                response = (SearchResponse)connection.SendRequest(req);
            }
            catch (LdapException ex)
            {
                ex.Data["LDAP_PHASE"] = "搜索";
                throw;
            }
            foreach (SearchResultEntry entry in response.Entries)
            {
                var item = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                item["distinguishedName"] = entry.DistinguishedName;
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
