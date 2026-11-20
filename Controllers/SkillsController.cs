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

    // 第一步：业务系统目录，来自 skills/<system>.json 的文件名。
    [HttpGet("domains")]
    public IActionResult GetDomains()
        => Ok(ResponseHelper.Success(_registry.ListSystems()));

    // 第二步：按 system（domain 参数）获取该系统所有 Skill，或关键词搜索。
    // 兼容旧用法：query=ad 精确命中系统时返回 ad.json 的全部 Skill。
    // query 为空时返回全部启用 Skill，不默认截断为 8 个。
    [HttpGet]
    public IActionResult GetList([FromQuery] string? domain, [FromQuery] string? query, [FromQuery] int? top_k)
    {
        var skills = !string.IsNullOrWhiteSpace(domain)
            ? (string.IsNullOrWhiteSpace(query)
                ? _registry.ListBySystem(domain)
                : _registry.SearchInSystem(domain, query, top_k ?? 8))
            : _registry.Search(query ?? "", top_k ?? 8);
        return Ok(ResponseHelper.Success(skills));
    }

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
