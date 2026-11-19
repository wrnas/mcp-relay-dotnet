using Microsoft.AspNetCore.Mvc;

namespace RelayMcp.Controllers;

[ApiController]
[Route("sql")]
public class SqlController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<SqlController> _logger;

    public SqlController(IConfiguration configuration, ILogger<SqlController> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public sealed class SqlRequest
    {
        public string DbAlias { get; set; } = "";
        public string Sql { get; set; } = "";
    }

    // Dynamic SQL is always read-only. Changes must be preconfigured as a Skill,
    // and independently approved via the local /approvals endpoint.
    [HttpPost("execute")]
    public async Task<IActionResult> ExecuteSql([FromBody] SqlRequest req)
    {
        if (!SqlSafety.IsReadOnly(req.Sql, out var reason))
            return StatusCode(403, ResponseHelper.Fail(reason, 403, "SECURITY_DENIED"));
        var result = await DbQueryRunner.ExecuteAsync(_configuration, req.DbAlias, req.Sql, logger: _logger);
        return result.Ok
            ? Ok(ResponseHelper.Success(result.Data))
            : StatusCode(result.Code, ResponseHelper.Fail(result.Message ?? "查询失败", result.Code, result.ErrorType ?? "BACKEND_ERROR"));
    }
}
