using Microsoft.AspNetCore.Mvc;

namespace RelayMcp.Controllers;

[ApiController]
[Route("skills")]
public sealed class SkillsController : ControllerBase
{
    private readonly SkillRegistry _registry;
    private readonly IHttpClientFactory _httpFactory;

    public SkillsController(SkillRegistry registry, IHttpClientFactory httpFactory)
    {
        _registry = registry;
        _httpFactory = httpFactory;
    }

    [HttpGet]
    public IActionResult GetList([FromQuery] string? query, [FromQuery] int? top_k)
        => Ok(ResponseHelper.Success(_registry.Search(query ?? "", Math.Clamp(top_k ?? 8, 1, 50))));

    [HttpGet("{name}")]
    public IActionResult GetDetail([FromRoute] string name)
    {
        var skill = _registry.Get(name);
        if (skill is null || !skill.Enabled)
            return NotFound(ResponseHelper.Fail($"技能 {name} 不存在或已禁用", 404, "NOT_FOUND"));
        return Ok(ResponseHelper.Success(skill.ToDetailDto()));
    }

    [HttpPost("execute")]
    public async Task<IActionResult> Execute([FromBody] ExecuteRequest req)
    {
        var result = await _registry.ExecuteAsync(req.ToolName, req.Args, _httpFactory);
        return result.Ok
            ? Ok(ResponseHelper.Success(result.Data))
            : StatusCode(result.Code, ResponseHelper.Fail(result.Message ?? "执行失败", result.Code,
                result.ErrorType ?? "BACKEND_ERROR", result.Data));
    }
}
