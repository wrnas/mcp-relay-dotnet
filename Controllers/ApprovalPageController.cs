using System.Net;
using Microsoft.AspNetCore.Mvc;

namespace RelayMcp.Controllers;

/// <summary>
/// 极简网页审批：使用独立的 ApprovalKey、浏览器会话和 CSRF 令牌，不使用 AI 接口的 ApiKey。
/// 默认远程访问要求 HTTPS；经管理员显式启用配置后可在受控私有网段使用 HTTP。
/// </summary>
[Route("approve")]
public sealed class ApprovalPageController : Controller
{
    private readonly ApprovalQueue _queue;
    private readonly ApprovalWebSessions _sessions;
    private readonly IConfiguration _configuration;

    public ApprovalPageController(ApprovalQueue queue, ApprovalWebSessions sessions, IConfiguration configuration)
    {
        _queue = queue;
        _sessions = sessions;
        _configuration = configuration;
    }

    // 和 Program.cs 的审批中间件使用同一套传输校验规则，避免出现中间件放行但页面返回 404。
    // 注意：AllowInsecureLanApproval 默认 false，只有显式配置为 true 才允许内网 HTTP 审批。
    private bool Available => string.Equals(_configuration["Relay:ApprovalMode"], "web", StringComparison.OrdinalIgnoreCase)
        && Program.IsApprovalTransportAllowed(
            HttpContext, _configuration.GetValue<bool>("Relay:AllowInsecureLanApproval"));

    private static string H(string? value) => WebUtility.HtmlEncode(value ?? "");
    private static bool ValidId(string id) => id.Length == 32 && id.All(Uri.IsHexDigit);

    private IActionResult Page(string content, int status = 200)
    {
        Response.Headers["Cache-Control"] = "no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'";
        Response.StatusCode = status;
        return Content("<!doctype html><html lang='zh-CN'><head><meta charset='utf-8'><meta name='viewport' content='width=device-width, initial-scale=1'>"
            + "<title>运维操作审批</title><style>body{font-family:system-ui,Arial;max-width:720px;margin:50px auto;padding:0 20px;line-height:1.7}"
            + "main{border:1px solid #aaa;border-radius:10px;padding:24px}pre{white-space:pre-wrap;overflow-wrap:anywhere;background:#f5f5f5;padding:16px}"
            + "input{font-size:1rem;padding:8px}button{font-size:1rem;padding:8px 16px;margin-right:12px;cursor:pointer}</style></head><body><main>"
            + content + "</main></body></html>", "text/html; charset=utf-8");
    }

    [HttpGet("{id}")]
    public IActionResult Review([FromRoute] string id)
    {
        if (!Available) return NotFound();
        if (!ValidId(id)) return Page("<h2>审批编号不合法</h2>", 400);
        var preview = _queue.GetPreview(id);
        if (preview is null || preview.Mode != "web") return Page("<h2>审批任务不存在或已过期</h2>", 404);

        if (!_sessions.TryGet(HttpContext, out var session) || session is null)
        {
            return Page("<h2>运维审批登录</h2><p>请输入独立的 ApprovalKey。该密码不会发给 AI 或 MCP 客户端。</p>"
                + $"<form method='post' action='/approve/{id}/login'><label>审批密码 <input type='password' name='key' autocomplete='current-password' required></label>"
                + "<button type='submit'>登录并查看操作</button></form>");
        }

        var content = "<h2>确认运维操作</h2>"
            + $"<p><strong>编号：</strong>{H(preview.Id)}</p>"
            + $"<p><strong>状态：</strong>{H(preview.State)}（有效期截止 {H(preview.ExpiresAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz"))}）</p>"
            + $"<pre>{H(preview.Summary)}</pre>";
        if (preview.State == "pending")
        {
            content += "<p>请核对目标、参数和影响。确认后只执行一次，不会自动重试。</p>"
                + $"<form method='post' action='/approve/{id}/confirm'><input type='hidden' name='csrf' value='{H(session.CsrfToken)}'>"
                + "<button type='submit'>确认执行</button></form>"
                + $"<form method='post' action='/approve/{id}/cancel'><input type='hidden' name='csrf' value='{H(session.CsrfToken)}'>"
                + "<button type='submit'>取消操作</button></form>";
        }
        else content += "<p>该任务已不再等待确认。可回到 AI 客户端查看执行状态。</p>";
        return Page(content);
    }

    [HttpPost("{id}/login")]
    [RequestSizeLimit(4096)]
    public async Task<IActionResult> Login([FromRoute] string id)
    {
        if (!Available) return NotFound();
        if (!ValidId(id) || _queue.GetPreview(id)?.Mode != "web") return NotFound();
        var remote = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (_sessions.IsLoginThrottled(remote)) return Page("<h2>登录失败次数过多，请稍后再试</h2>", 429);
        var form = await Request.ReadFormAsync();
        if (!Program.FixedTimeEquals(form["key"].ToString(), _configuration["Relay:ApprovalKey"]))
        {
            _sessions.RecordFailedLogin(remote);
            return Page("<h2>审批密码错误</h2><p>请返回重新输入。</p>", 403);
        }
        _sessions.Issue(HttpContext);
        return SeeOther(id);
    }

    [HttpPost("{id}/confirm")]
    [RequestSizeLimit(4096)]
    public async Task<IActionResult> Confirm([FromRoute] string id)
    {
        if (!Available) return NotFound();
        if (!ValidId(id)) return BadRequest();
        var form = await Request.ReadFormAsync();
        if (!_sessions.ValidateCsrf(HttpContext, form["csrf"].ToString()))
            return Page("<h2>审批会话失效或表单校验失败，请重新登录</h2>", 403);
        var result = await _queue.ApproveWebAsync(id);
        if (result is ApprovalQueue.Denied denied) return Page("<h2>审批被拒绝</h2><p>" + H(denied.Message) + "</p>", 403);
        return result is null ? NotFound() : SeeOther(id);
    }

    [HttpPost("{id}/cancel")]
    [RequestSizeLimit(4096)]
    public async Task<IActionResult> Cancel([FromRoute] string id)
    {
        if (!Available) return NotFound();
        if (!ValidId(id)) return BadRequest();
        var form = await Request.ReadFormAsync();
        if (!_sessions.ValidateCsrf(HttpContext, form["csrf"].ToString()))
            return Page("<h2>审批会话失效或表单校验失败，请重新登录</h2>", 403);
        _queue.Cancel(id);
        return SeeOther(id);
    }

    private IActionResult SeeOther(string id)
    {
        Response.StatusCode = 303;
        Response.Headers["Location"] = "/approve/" + id;
        return new EmptyResult();
    }
}
