using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace RelayMcp;

/// <summary>
/// One-shot, in-memory approvals. An approval binds the exact Skill definition and arguments
/// captured when the request was created. No confirmation can change either of them.
/// </summary>
public sealed class ApprovalQueue
{
    public sealed record Ticket(string Id, string Mode, string? ChatPhrase);
    public sealed record Denied(string Message);
    public sealed record Preview(string Id, string Mode, string Summary, string State, DateTimeOffset ExpiresAt);

    private sealed class Item
    {
        public required string Id { get; init; }
        public required string Mode { get; init; }
        public required string Summary { get; init; }
        public required Func<Task<ExecResult>> Run { get; init; }
        public string? ChatPhrase { get; init; }
        public bool ChatAllowed { get; init; }
        public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
        public string State { get; set; } = "pending";
        public int FailedAttempts { get; set; }
        public ExecResult? Result { get; set; }
        public object Gate { get; } = new();
    }

    private readonly ConcurrentDictionary<string, Item> _items = new();
    private readonly ILogger<ApprovalQueue> _logger;
    private readonly IConfiguration _config;

    public ApprovalQueue(ILogger<ApprovalQueue> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    private TimeSpan Ttl => TimeSpan.FromMinutes(Math.Clamp(_config.GetValue("Relay:ApprovalTtlMinutes", 5), 1, 60));

    private void Cleanup()
    {
        foreach (var (id, item) in _items)
        {
            lock (item.Gate)
            {
                var now = DateTimeOffset.UtcNow;
                if (item.State == "pending" && item.CreatedAt + Ttl <= now)
                {
                    item.State = "expired";
                    item.UpdatedAt = now;
                }
                if (item.State != "running" && item.State != "pending" && item.UpdatedAt.AddMinutes(30) < now)
                    _items.TryRemove(id, out _);
            }
        }
    }

    public Ticket? Enqueue(string summary, string mode, bool chatAllowed, Func<Task<ExecResult>> run)
    {
        Cleanup();
        if (mode is not ("client" or "web" or "chat") || _items.Count >= 100) return null;
        var id = Guid.NewGuid().ToString("N");
        var phrase = mode == "chat" ? $"确认执行 {RandomNumberGenerator.GetInt32(100000, 1000000)}" : null;
        var item = new Item
        {
            Id = id, Summary = summary, Mode = mode, ChatPhrase = phrase,
            ChatAllowed = chatAllowed, Run = run
        };
        if (!_items.TryAdd(id, item)) return null;
        _logger.LogInformation("Write awaiting approval: {ApprovalId}, mode={Mode}", id, mode);
        return new Ticket(id, mode, phrase);
    }

    public Preview? GetPreview(string id)
    {
        Cleanup();
        if (!_items.TryGetValue(id, out var item)) return null;
        lock (item.Gate)
            return new Preview(item.Id, item.Mode, item.Summary, item.State, item.CreatedAt + Ttl);
    }

    private object ToDto(Item item, bool includeSummary)
    {
        lock (item.Gate)
        {
            return new
            {
                approval_id = item.Id,
                mode = item.Mode,
                status = item.State,
                expires_at = item.CreatedAt + Ttl,
                summary = includeSummary ? item.Summary : null,
                result = item.Result is null ? null : new
                {
                    ok = item.Result.Ok,
                    data = item.Result.Data,
                    error_type = item.Result.ErrorType,
                    message = item.Result.Message
                }
            };
        }
    }

    public object? Status(string id, bool includeSummary = false)
    {
        Cleanup();
        return _items.TryGetValue(id, out var item) ? ToDto(item, includeSummary) : null;
    }

    public IEnumerable<object> Pending()
    {
        Cleanup();
        return _items.Values.Where(x => x.State == "pending")
            .OrderBy(x => x.CreatedAt).Select(x => ToDto(x, true)).ToArray();
    }

    public Task<object?> ApproveClientAsync(string id) => ExecuteForModeAsync(id, "client");

    public Task<object?> ApproveWebAsync(string id) => ExecuteForModeAsync(id, "web");

    public Task<object?> ApproveChatAsync(string id, string? confirmation)
        => ExecuteForModeAsync(id, "chat", confirmation);

    // Only for the legacy local administrator endpoint; never a model-facing tool.
    public Task<object?> ExecuteApprovedAsync(string id) => ExecuteForModeAsync(id, null);

    private async Task<object?> ExecuteForModeAsync(string id, string? requiredMode, string? confirmation = null)
    {
        Cleanup();
        if (!_items.TryGetValue(id, out var item)) return null;
        lock (item.Gate)
        {
            if (!_config.GetValue("Relay:EnableWrites", false))
                return new Denied("写操作已被关闭");
            if (!string.Equals(_config["Relay:ApprovalMode"] ?? "web", item.Mode, StringComparison.OrdinalIgnoreCase))
                return new Denied("审批模式已变化，请重新发起操作");
            if (item.Mode == "chat" && !_config.GetValue("Relay:ChatApprovalEnabled", false))
                return new Denied("聊天审批已被关闭");
            if (requiredMode is not null && item.Mode != requiredMode) return null;
            if (item.State != "pending") return ToDto(item, true);
            if (item.CreatedAt + Ttl <= DateTimeOffset.UtcNow)
            {
                item.State = "expired";
                item.UpdatedAt = DateTimeOffset.UtcNow;
                return ToDto(item, true);
            }
            if (requiredMode == "chat")
            {
                if (!item.ChatAllowed || !Program.FixedTimeEquals(item.ChatPhrase, confirmation))
                {
                    item.FailedAttempts++;
                    if (item.FailedAttempts >= 3)
                    {
                        item.State = "cancelled";
                        item.UpdatedAt = DateTimeOffset.UtcNow;
                    }
                    return new Denied("确认文字错误，或此 Skill 未允许聊天审批");
                }
            }
            item.State = "running";
            item.UpdatedAt = DateTimeOffset.UtcNow;
        }

        ExecResult result;
        try { result = await item.Run(); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Approved write failed: {ApprovalId}", id);
            result = ExecResult.Fail("EXECUTION_ERROR", "写操作执行失败，请检查服务端日志", 500);
        }
        lock (item.Gate)
        {
            item.Result = result;
            item.State = result.Ok ? "completed" : "failed";
            item.UpdatedAt = DateTimeOffset.UtcNow;
        }
        _logger.LogInformation("Approved write finished: {ApprovalId}, state={State}", id, item.State);
        return ToDto(item, true);
    }

    public object? Cancel(string id)
    {
        Cleanup();
        if (!_items.TryGetValue(id, out var item)) return null;
        lock (item.Gate)
        {
            if (item.State == "pending")
            {
                item.State = "cancelled";
                item.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }
        return ToDto(item, true);
    }
}
