using Microsoft.AspNetCore.Mvc;

namespace RelayMcp.Controllers
{
    [ApiController]
    [Route("skills")]
    public class SkillsController : ControllerBase
    {
        private readonly SkillRegistry _registry;
        private readonly IHttpClientFactory _httpFactory;

        public SkillsController(SkillRegistry registry, IHttpClientFactory httpFactory)
        {
            _registry = registry;
            _httpFactory = httpFactory;
        }

        // 对应 MCP: ENDPOINT_LIST = "/skills"
        // 请求方式: GET /skills?query=xxx&top_k=8
        [HttpGet]
        public IActionResult GetList([FromQuery] string? query, [FromQuery] int? top_k)
        {
            var list = _registry.Search(query ?? "", top_k ?? 8);
            return Ok(ResponseHelper.Success(list));
        }

        // 对应 MCP: ENDPOINT_DESC = "/skills/{name}"
        // 请求方式: GET /skills/erp_get_order
        [HttpGet("{name}")]
        public IActionResult GetDetail([FromRoute] string name)
        {
            var skill = _registry.Get(name);
            if (skill is null)
                return StatusCode(404, ResponseHelper.Fail($"技能 {name} 不存在", 404, "NOT_FOUND"));

            return Ok(ResponseHelper.Success(skill.ToDetailDto()));
        }

        // 对应 MCP: ENDPOINT_EXEC = "/skills/execute"
        // 请求方式: POST /skills/execute
        [HttpPost("execute")]
        public async Task<IActionResult> Execute([FromBody] ExecuteRequest req)
        {
            var result = await _registry.ExecuteAsync(req.ToolName, req.Args, _httpFactory);

            if (result.Ok)
                return Ok(ResponseHelper.Success(result.Data));

            return StatusCode(result.Code,
                ResponseHelper.Fail(result.Message!, result.Code, result.ErrorType!));
        }
    }
}