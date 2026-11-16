import httpx
from mcp.server.fastmcp import FastMCP

RELAY_BASE = "http://localhost:54321"
RELAY_TOKEN = "myMcpKey"
ENDPOINT_LIST = "/skills"
ENDPOINT_DESC = "/skills/{name}"
ENDPOINT_EXEC = "/skills/execute"
ENDPOINT_SQL = "/sql/execute"  # SQL 执行端点
TIMEOUT = 30

mcp = FastMCP("relay-mcp")


async def _call(method, path, **kwargs):
    headers = {"X-Api-Key": RELAY_TOKEN} if RELAY_TOKEN else {}
    try:
        async with httpx.AsyncClient(timeout=TIMEOUT) as client:
            resp = await client.request(method, RELAY_BASE + path, headers=headers, **kwargs)
        body = resp.json()
    except httpx.TimeoutException:
        return {"ok": False, "error_type": "TIMEOUT", "message": "中转服务超时"}
    except httpx.RequestError as exc:
        return {"ok": False, "error_type": "NETWORK", "message": f"无法连接中转服务: {exc}"}
    except ValueError:
        return {"ok": False, "error_type": "BACKEND_ERROR", "message": f"非 JSON 响应，HTTP {resp.status_code}"}

    if body.get("code") == 200:
        return {"ok": True, "data": body.get("data")}
        
    return {
        "ok": False,
        "error_type": body.get("error_type", "BACKEND_ERROR"),
        "message": body.get("message", "未知错误"),
        "data": body.get("data")  # 必须带上 data，大模型才能拿到 confirm_token
    }


@mcp.tool()
async def GetSkillList(query: str, top_k: int = 8) -> dict:
    """不确定技能名时，用自然语言查询最相关的技能摘要（top-K 条，只含 name/description/domain）。
    已知 tool_name 时不要调用本方法，直接 GetSkillDetail 或 ExecuteSkill。
    """
    return await _call("GET", ENDPOINT_LIST, params={"query": query, "top_k": top_k})


@mcp.tool()
async def GetSkillDetail(tool_name: str) -> dict:
    """执行前获取该技能的参数 schema 和调用说明。返回 NOT_FOUND 说明 tool_name 有误，先 GetSkillList 确认。"""
    return await _call("GET", ENDPOINT_DESC.format(name=tool_name))


@mcp.tool()
async def ExecuteSkill(tool_name: str, args: dict = None) -> dict:
    """执行技能，args 需符合 GetSkillDetail 的 schema。

    失败返回 {ok: false, error_type, message}，按 error_type 处理：
    INVALID_PARAMS → 按 message 修正 args 后重试一次；NOT_FOUND → 重新 GetSkillList；
    PERMISSION_DENIED → 告知用户，不要重试；BACKEND_ERROR → 可重试一次；
    TIMEOUT/NETWORK → 告知用户，不要连续重试。
    成功返回 {ok: true, data: ...}，从 data 中取字段继续任务。
    """
    return await _call("POST", ENDPOINT_EXEC, json={"tool_name": tool_name, "args": args or {}})


@mcp.tool()
async def ExecuteSql(db_alias: str, sql: str, confirm_token: str = None) -> dict:
    """执行数据库 SQL 查询或修改。
    
    规则：
    1. 默认只能执行 SELECT 查询，返回最多 100 条结果。
    2. 如果要执行 UPDATE 或 DELETE，第一次调用时不传 confirm_token，系统会拦截并返回 error_type="REQUIRE_CONFIRMATION" 以及一个 confirm_token（在 data 中）。
    3. 你【必须】向用户展示你要执行的 SQL 语句。在用户明确回复“确认/执行”后，带上该 confirm_token 再次调用本工具。
    4. 注意：不同数据库别名（db_alias）对应的 SQL 语法不同（如 MySQL 用 LIMIT，SQLServer 用 TOP）。
    """
    payload = {
        "dbAlias": db_alias,
        "sql": sql,
        "confirmToken": confirm_token
    }
    return await _call("POST", ENDPOINT_SQL, json=payload)


if __name__ == "__main__":
    mcp.run(transport="stdio")