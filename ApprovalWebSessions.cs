using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace RelayMcp;

/// <summary>Short-lived, server-side approval sessions. Browser never stores the approval key.</summary>
public sealed class ApprovalWebSessions
{
    public const string CookieName = "relay_approval_session";
    public sealed record Session(string CsrfToken, DateTimeOffset ExpiresAt);
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private readonly ConcurrentDictionary<string, (int Failures, DateTimeOffset Start)> _failedLogins = new();

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));

    private void Cleanup()
    {
        foreach (var (id, session) in _sessions)
            if (session.ExpiresAt <= DateTimeOffset.UtcNow) _sessions.TryRemove(id, out _);
        foreach (var (ip, attempt) in _failedLogins)
            if (attempt.Start.AddMinutes(5) <= DateTimeOffset.UtcNow) _failedLogins.TryRemove(ip, out _);
    }

    public bool IsLoginThrottled(string ip)
    {
        Cleanup();
        return _failedLogins.TryGetValue(ip, out var state) && state.Failures >= 5;
    }

    public void RecordFailedLogin(string ip)
    {
        _failedLogins.AddOrUpdate(ip, _ => (1, DateTimeOffset.UtcNow), (_, previous) =>
            previous.Start.AddMinutes(5) < DateTimeOffset.UtcNow
                ? (1, DateTimeOffset.UtcNow)
                : (previous.Failures + 1, previous.Start));
    }

    public void Issue(HttpContext context)
    {
        Cleanup();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var session = new Session(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), DateTimeOffset.UtcNow.AddMinutes(30));
        if (_sessions.Count >= 50) _sessions.Clear();
        _sessions[Hash(token)] = session;
        context.Response.Cookies.Append(CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = context.Request.IsHttps,
            Path = "/approve",
            Expires = session.ExpiresAt,
            IsEssential = true
        });
    }

    public bool TryGet(HttpContext context, out Session? session)
    {
        Cleanup();
        session = null;
        return context.Request.Cookies.TryGetValue(CookieName, out var raw)
            && raw.Length == 64
            && _sessions.TryGetValue(Hash(raw), out session)
            && session.ExpiresAt > DateTimeOffset.UtcNow;
    }

    public bool ValidateCsrf(HttpContext context, string? csrf)
        => TryGet(context, out var session) && session is not null
            && Program.FixedTimeEquals(csrf, session.CsrfToken);
}
