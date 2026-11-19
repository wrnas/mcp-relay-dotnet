using Microsoft.AspNetCore.Mvc;
using System.Net;

namespace RelayMcp.Controllers;

[ApiController]
[Route("approvals")]
public sealed class ApprovalsController : ControllerBase
{
    private readonly ApprovalQueue _queue;
    private readonly IConfiguration _configuration;

    public ApprovalsController(ApprovalQueue queue, IConfiguration configuration)
    {
        _queue = queue;
        _configuration = configuration;
    }

    [HttpGet("{id}/status")]
    public IActionResult Status([FromRoute] string id)
    {
        var value = _queue.Status(id);
        return value is null
            ? NotFound(ResponseHelper.Fail("审批任务不存在或已过期", 404, "NOT_FOUND"))
            : Ok(ResponseHelper.Success(value));
    }

    [HttpPost("{id}/confirm-client")]
    public async Task<IActionResult> ConfirmClient([FromRoute] string id)
    {
        if (!IsMode("client") || !AllowedTransport() ||
            !Program.FixedTimeEquals(Request.Headers["X-Client-Confirm-Key"].ToString(),
                _configuration["Relay:ClientConfirmKey"]))
            return Denied("客户端确认未授权或审批模式不匹配");
        var result = await _queue.ApproveClientAsync(id);
        if (result is ApprovalQueue.Denied denied) return Denied(denied.Message);
        return result is null ? NotFound(ResponseHelper.Fail("审批任务不存在或模式不匹配", 404, "NOT_FOUND"))
            : Ok(ResponseHelper.Success(result));
    }

    public sealed class ChatConfirmation
    {
        public string Confirmation { get; set; } = "";
    }

    // Deliberately lower assurance: the model can see the challenge and call this tool.
    // Enable only for explicitly permitted, low-impact Skill actions.
    [HttpPost("{id}/confirm-chat")]
    public async Task<IActionResult> ConfirmChat([FromRoute] string id, [FromBody] ChatConfirmation input)
    {
        if (!IsMode("chat") || !_configuration.GetValue("Relay:ChatApprovalEnabled", false))
            return Denied("聊天审批未启用");
        var result = await _queue.ApproveChatAsync(id, input.Confirmation);
        if (result is ApprovalQueue.Denied denied) return Denied(denied.Message);
        return result is null ? NotFound(ResponseHelper.Fail("审批任务不存在或模式不匹配", 404, "NOT_FOUND"))
            : Ok(ResponseHelper.Success(result));
    }

    [HttpGet("pending")]
    public IActionResult Pending()
    {
        if (!IsAdministrator()) return Denied("需要本机独立审批密钥");
        return Ok(ResponseHelper.Success(_queue.Pending()));
    }

    // Optional legacy CLI approval for recovery only; disabled by default.
    [HttpPost("{id}/execute")]
    public async Task<IActionResult> Execute([FromRoute] string id)
    {
        if (!_configuration.GetValue("Relay:EnableLegacyCliApproval", false) || !IsAdministrator())
            return Denied("旧版终端审批未启用");
        var result = await _queue.ExecuteApprovedAsync(id);
        if (result is ApprovalQueue.Denied denied) return Denied(denied.Message);
        return result is null ? NotFound(ResponseHelper.Fail("审批任务不存在", 404, "NOT_FOUND"))
            : Ok(ResponseHelper.Success(result));
    }

    [HttpPost("{id}/cancel")]
    public IActionResult Cancel([FromRoute] string id)
    {
        if (!IsAdministrator()) return Denied("需要本机独立审批密钥");
        var result = _queue.Cancel(id);
        return result is null ? NotFound(ResponseHelper.Fail("审批任务不存在", 404, "NOT_FOUND"))
            : Ok(ResponseHelper.Success(result));
    }

    private bool IsMode(string mode) =>
        string.Equals(_configuration["Relay:ApprovalMode"], mode, StringComparison.OrdinalIgnoreCase);

    private bool AllowedTransport() => Request.IsHttps ||
        (HttpContext.Connection.RemoteIpAddress is IPAddress ip && IPAddress.IsLoopback(ip));

    private bool IsAdministrator()
    {
        var remote = HttpContext.Connection.RemoteIpAddress;
        return remote is not null && IPAddress.IsLoopback(remote)
            && Program.FixedTimeEquals(Request.Headers["X-Approval-Key"].ToString(),
                _configuration["Relay:ApprovalKey"]);
    }

    private IActionResult Denied(string message) =>
        StatusCode(403, ResponseHelper.Fail(message, 403, "PERMISSION_DENIED"));
}
