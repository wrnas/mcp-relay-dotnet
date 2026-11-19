"""Thin MCP adapter for Mcp-Relay.Net.

ApprovalMode=client uses MCP elicitation and a separate relay-to-MCP secret.
ApprovalMode=web sends a URL for a browser-only independent approval.
ApprovalMode=chat is explicitly lower-assurance: the model can call ConfirmChatApproval.
"""
import os
from urllib.parse import quote

import httpx
from mcp.server.fastmcp import Context, FastMCP
from pydantic import BaseModel, Field

RELAY_BASE = os.getenv("RELAY_BASE", "http://127.0.0.1:54321").rstrip("/")
RELAY_TOKEN = os.getenv("RELAY_API_KEY", "")
CLIENT_CONFIRM_KEY = os.getenv("RELAY_CLIENT_CONFIRM_KEY", "")
TIMEOUT = float(os.getenv("RELAY_TIMEOUT", "35"))

mcp = FastMCP("relay-mcp")


class _ApprovalAnswer(BaseModel):
    confirm: bool = Field(description="用户已审阅以上操作并明确批准时选择 true，否则为 false")


async def _call(method: str, path: str, *, client_confirm: bool = False, **kwargs) -> dict:
    if not RELAY_TOKEN:
        return {"ok": False, "error_type": "CONFIG_ERROR", "message": "未设置 RELAY_API_KEY 环境变量"}
    if client_confirm and not CLIENT_CONFIRM_KEY:
        return {"ok": False, "error_type": "CONFIG_ERROR", "message": "客户端审批需要 RELAY_CLIENT_CONFIRM_KEY"}

    headers = {"X-Api-Key": RELAY_TOKEN}
    if client_confirm:
        headers["X-Client-Confirm-Key"] = CLIENT_CONFIRM_KEY
    try:
        async with httpx.AsyncClient(timeout=TIMEOUT) as client:
            resp = await client.request(method, RELAY_BASE + path, headers=headers, **kwargs)
            body = resp.json()
    except httpx.TimeoutException:
        return {"ok": False, "error_type": "TIMEOUT", "message": "中转服务超时，修改操作不要直接重试，请先查询审批状态"}
    except httpx.RequestError:
        return {"ok": False, "error_type": "NETWORK", "message": "无法连接中转服务，请检查服务状态"}
    except ValueError:
        return {"ok": False, "error_type": "BACKEND_ERROR", "message": f"非 JSON 响应，HTTP {resp.status_code}"}

    if not isinstance(body, dict):
        return {"ok": False, "error_type": "BACKEND_ERROR", "message": "后端返回格式错误"}
    if resp.is_success and body.get("code") == 200:
        return {"ok": True, "data": body.get("data")}
    return {
        "ok": False,
        "error_type": body.get("errorType") or body.get("error_type") or "BACKEND_ERROR",
        "message": body.get("message", "未知错误"),
        "data": body.get("data"),
    }


@mcp.tool()
async def GetSkillList(query: str = "", top_k: int = 8) -> dict:
    """搜索可用 Skill 摘要。已有 tool_name 可直接 GetSkillDetail。"""
    return await _call("GET", "/skills", params={"query": query, "top_k": top_k})


@mcp.tool()
async def GetSkillDetail(tool_name: str) -> dict:
    """查询 Skill 参数定义和风险级别(read/write)。"""
    return await _call("GET", "/skills/" + quote(tool_name, safe=""))


@mcp.tool()
async def ExecuteSkill(tool_name: str, ctx: Context, args: dict | None = None) -> dict:
    """执行已配置 Skill。写操作按 .NET 的 Relay:ApprovalMode 进入确认流程。

    client: MCP 客户端弹确认窗口，确认后此工具自动调用受限的客户端审批入口；
    web: 返回审批网页 URL，人工登录网页确认；
    chat: 返回操作摘要和完整确认短语，必须等用户在聊天中明确回复后再调用 ConfirmChatApproval。
    不要重复提交同一个写操作，请改用 GetApprovalStatus 检查状态。
    """
    response = await _call("POST", "/skills/execute", json={"tool_name": tool_name, "args": args or {}})
    if response.get("error_type") != "REQUIRE_APPROVAL":
        return response
    data = response.get("data") or {}
    if data.get("mode") != "client":
        return response

    approval_id = data.get("approval_id", "")
    if not CLIENT_CONFIRM_KEY:
        return {
            "ok": False, "error_type": "CONFIG_ERROR",
            "message": "客户端模式要求配置 RELAY_CLIENT_CONFIRM_KEY（与 .NET Relay:ClientConfirmKey 一致），当前操作未执行。",
            "data": data,
        }

    # The approval secret is never part of the MCP tool schema or tool result.
    # Form elicitation only works when the MCP client explicitly supports it.
    try:
        decision = await ctx.elicit(
            "运维写操作审批（请核对操作目标和参数）：\n" + str(data.get("summary", "")) +
            "\n审批编号：" + approval_id + "。不同意请取消。",
            schema=_ApprovalAnswer,
        )
    except Exception:
        return {
            "ok": False, "error_type": "CLIENT_CONFIRM_UNAVAILABLE",
            "message": "当前 MCP 客户端无法完成原生交互确认，操作未执行。请改用 web 或 chat 模式，或换支持 Elicitation 的客户端。",
            "data": data,
        }

    if decision.action != "accept" or decision.data is None or decision.data.confirm is not True:
        return {
            "ok": False, "error_type": "APPROVAL_DECLINED",
            "message": "客户端确认被取消或拒绝，写操作未执行，待审批任务会自动过期。",
            "data": {"approval_id": approval_id},
        }

    # Only this private code path can send the confirmation key (not an MCP tool).
    return await _call("POST", "/approvals/" + quote(approval_id, safe="") + "/confirm-client",
                       client_confirm=True)


@mcp.tool()
async def ConfirmChatApproval(approval_id: str, confirmation: str) -> dict:
    """仅聊天审批模式使用。务必先把待审批操作的摘要与完整确认短语展示给真人，
    等其明确回复该短语后才能调用。不得由模型自行填写或猜测确认短语。

    注意：本接口无法验证短语确实来自真人，聊天审批只能用于显式允许的低风险 Skill。
    """
    return await _call("POST", "/approvals/" + quote(approval_id, safe="") + "/confirm-chat",
                       json={"confirmation": confirmation})


@mcp.tool()
async def ExecuteSql(db_alias: str, sql: str) -> dict:
    """执行一条临时只读 SELECT / WITH ... SELECT 查询（最多返回配置行数）。

    动态 SQL 只能使用最小权限的只读账号，不能执行修改。
    修改须配置固定写 Skill，再完成所选审批流程。
    """
    return await _call("POST", "/sql/execute", json={"dbAlias": db_alias, "sql": sql})


@mcp.tool()
async def GetApprovalStatus(approval_id: str) -> dict:
    """只读查看写操作的审批状态与结果，不能批准。"""
    return await _call("GET", "/approvals/" + quote(approval_id, safe="") + "/status")


if __name__ == "__main__":
    mcp.run(transport="stdio")
