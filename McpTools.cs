using System.ComponentModel;
using System.Net;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace RelayMcp;

/// <summary>
/// Native MCP adapter for the existing SkillRegistry and approval queue.
/// Business logic stays in the existing executors; this file only exposes MCP tools.
/// </summary>
[McpServerToolType]
public sealed class McpTools
{
    private readonly SkillRegistry _registry;
    private readonly ApprovalQueue _approvals;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _configuration;

    public McpTools(SkillRegistry registry, ApprovalQueue approvals,
        IHttpClientFactory httpFactory, IConfiguration configuration)
    {
        _registry = registry;
        _approvals = approvals;
        _httpFactory = httpFactory;
        _configuration = configuration;
    }

    [McpServerTool, Description("List available business systems. Call this first when you do not know which system has the required skills (AD, HR, Exchange, etc.).")]
    public string GetSkillDomains()
        => Ok(_registry.ListSystems());

    [McpServerTool, Description("Discover configured skills. Set domain to a system name from GetSkillDomains (e.g. ad) to list ALL skills in that system, without top_k truncation. When searching by words, top_k limits matches. Get details before calling a skill.")]
    public string GetSkillList(
        [Description("Optional search text; empty means list all skills in the selected system")] string query = "",
        [Description("Maximum keyword-search results (1-50); ignored when listing a whole system")] int top_k = 8,
        [Description("Optional system file name, such as ad or hr. Use GetSkillDomains to discover names")] string domain = "")
    {
        object skills = !string.IsNullOrWhiteSpace(domain)
            ? (string.IsNullOrWhiteSpace(query)
                ? _registry.ListBySystem(domain)
                : _registry.SearchInSystem(domain, query, top_k))
            : _registry.Search(query, top_k);
        return Ok(skills);
    }

    [McpServerTool, Description("Get one skill's full parameter JSON schema, description, system, and read/write risk. Always inspect before invoking an unfamiliar skill.")]
    public string GetSkillDetail([Description("The exact skill tool_name")] string tool_name)
    {
        var skill = _registry.Get(tool_name);
        return skill is null || !skill.Enabled
            ? Fail("NOT_FOUND", $"Skill {tool_name} is missing or disabled", 404)
            : Ok(skill.ToDetailDto());
    }

    [McpServerTool, Description("Execute a configured skill. Supply the exact tool_name and an args JSON object matching its schema. Write skills use RelayMcp's existing approval flow. Do not repeat a pending write; use GetApprovalStatus.")]
    public async Task<string> ExecuteSkill(
        [Description("Exact skill name from GetSkillList")] string tool_name,
        [Description("Skill arguments, as a JSON object (not a JSON-encoded string)")] Dictionary<string, JsonElement>? args = null)
    {
        var element = JsonSerializer.SerializeToElement(args ?? new Dictionary<string, JsonElement>());
        var result = await _registry.ExecuteAsync(tool_name, element, _httpFactory);
        return FromExecResult(result);
    }

    [McpServerTool, Description("Read the status and result of a previously requested write operation. This tool does not approve it.")]
    public string GetApprovalStatus([Description("Approval ID returned by ExecuteSkill")] string approval_id)
    {
        var result = _approvals.Status(approval_id);
        return result is null ? Fail("NOT_FOUND", "Approval not found or expired", 404) : Ok(result);
    }

    [McpServerTool, Description("Complete a chat-mode approval only after the user explicitly sends the full confirmation phrase. Chat mode is weaker than web approval. Never invent or supply a confirmation on your own.")]
    public async Task<string> ConfirmChatApproval(
        [Description("Approval ID returned by ExecuteSkill")] string approval_id,
        [Description("Exact confirmation phrase explicitly provided by the user")] string confirmation)
    {
        if (!string.Equals(_configuration["Relay:ApprovalMode"], "chat", StringComparison.OrdinalIgnoreCase) ||
            !_configuration.GetValue("Relay:ChatApprovalEnabled", false))
            return Fail("PERMISSION_DENIED", "Chat approval is not enabled", 403);

        var result = await _approvals.ApproveChatAsync(approval_id, confirmation);
        if (result is ApprovalQueue.Denied denied)
            return Fail("PERMISSION_DENIED", denied.Message, 403);
        return result is null ? Fail("NOT_FOUND", "Approval not found or expired", 404) : Ok(result);
    }

    // The SQL controller source wasn't supplied. For V1 we deliberately reuse its existing
    // read-only endpoint rather than duplicating SQL validation. This is a loopback-only call.
    [McpServerTool, Description("Execute one temporary read-only SELECT / WITH ... SELECT SQL query using an existing database alias. Reuses the application's read-only /sql/execute API and its safety checks.")]
    public async Task<string> ExecuteSql(
        [Description("Configured database alias")] string db_alias,
        [Description("A single read-only SQL query")] string sql)
    {
        var endpoint = _configuration["Mcp:InternalBaseUrl"] ?? "http://127.0.0.1:54321";
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps) ||
            !IsLoopbackHost(baseUri.Host) ||
            baseUri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(baseUri.UserInfo) ||
            !string.IsNullOrEmpty(baseUri.Query) || !string.IsNullOrEmpty(baseUri.Fragment))
        {
            return Fail("CONFIG_ERROR", "Mcp:InternalBaseUrl must be a loopback HTTP(S) root URL", 500);
        }

        var apiKey = _configuration["Relay:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            return Fail("CONFIG_ERROR", "Relay:ApiKey is not configured", 500);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "/sql/execute"));
            request.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
            request.Content = new StringContent(
                JsonSerializer.Serialize(new { dbAlias = db_alias, sql }), Encoding.UTF8, "application/json");
            var client = _httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(Math.Clamp(
                _configuration.GetValue("Relay:HttpTimeoutSeconds", 30), 1, 300));
            using var response = await client.SendAsync(request);
            var payload = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(payload);
            var body = doc.RootElement;
            if (body.ValueKind != JsonValueKind.Object)
                return Fail("BACKEND_ERROR", "SQL API returned an invalid response", 502);

            if (response.IsSuccessStatusCode && GetInt(body, "code") == 200)
                return Ok(GetElement(body, "data"));
            return Fail(GetString(body, "errorType") ?? "BACKEND_ERROR",
                GetString(body, "message") ?? "SQL request failed", (int)response.StatusCode,
                GetElement(body, "data"));
        }
        catch (TaskCanceledException)
        {
            return Fail("TIMEOUT", "SQL request timed out", 504);
        }
        catch (HttpRequestException)
        {
            return Fail("NETWORK", "Cannot connect to the local SQL REST endpoint", 502);
        }
        catch (JsonException)
        {
            return Fail("BACKEND_ERROR", "SQL API did not return JSON", 502);
        }
    }

    private static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));

    private static JsonElement? GetElement(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var value) ? value.Clone() : null;

    private static string? GetString(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static int? GetInt(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var n) ? n : null;

    private static string Ok(object? data) => JsonSerializer.Serialize(new
    {
        ok = true,
        data,
        error_type = (string?)null,
        message = "ok"
    });

    private static string Fail(string kind, string message, int code, object? data = null) =>
        JsonSerializer.Serialize(new { ok = false, data, error_type = kind, message, code });

    private static string FromExecResult(ExecResult r) => JsonSerializer.Serialize(new
    {
        ok = r.Ok,
        data = r.Data,
        error_type = r.ErrorType,
        message = r.Ok ? "ok" : r.Message,
        code = r.Code
    });
}
