"""Offline checks. These do NOT replace a .NET compile or live API smoke test."""
import asyncio
import importlib.util
import json
import os
from pathlib import Path
import sys
import types

ROOT = Path(__file__).resolve().parents[1]


def test_json_files():
    config = json.loads((ROOT / "appsettings.json").read_text(encoding="utf8"))
    assert config["Relay"]["EnableWrites"] is False
    assert config["Relay"]["ApiKey"] == ""
    assert config["Relay"]["ApprovalKey"] == ""
    for file in (ROOT / "skills").glob("*.json"):
        data = json.loads(file.read_text(encoding="utf8"))
        assert all(isinstance(s.get("params"), dict) for s in data.values())
        for skill in data.values():
            assert sum(k in skill for k in ("get", "post", "put", "delete", "soap", "sql", "script", "ldap_filter")) == 1
            assert skill.get("risk") in ("read", "write")


def test_powershell_templates_migrated():
    data = json.loads((ROOT / "skills/AD.json").read_text(encoding="utf8"))
    for skill in data.values():
        assert skill["powershell_target"] == "local"
        assert "{username}" not in skill["script"]
        assert "{keyword}" not in skill["script"]
        assert "{groupname}" not in skill["script"]
        for name in skill["params"]:
            assert "$" + name in skill["script"]


def load_mcp_module(monkeypatch):
    class FakeFastMCP:
        def __init__(self, name):
            self.name = name

        def tool(self):
            return lambda function: function

    mcp = types.ModuleType("mcp")
    mcp_server = types.ModuleType("mcp.server")
    fastmcp = types.ModuleType("mcp.server.fastmcp")
    fastmcp.FastMCP = FakeFastMCP
    fastmcp.Context = type("Context", (), {})
    monkeypatch.setitem(sys.modules, "mcp", mcp)
    monkeypatch.setitem(sys.modules, "mcp.server", mcp_server)
    monkeypatch.setitem(sys.modules, "mcp.server.fastmcp", fastmcp)
    monkeypatch.setenv("RELAY_API_KEY", "unit-test-api-key")
    spec = importlib.util.spec_from_file_location("relay_mcp_test", ROOT / "Mcp_Server.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def test_python_mcp_roundtrip(monkeypatch):
    module = load_mcp_module(monkeypatch)

    class Response:
        def __init__(self, status, body):
            self.status_code = status
            self.body = body
            self.is_success = 200 <= status < 300

        def json(self):
            return self.body

    class Client:
        def __init__(self, timeout):
            self.timeout = timeout

        async def __aenter__(self):
            return self

        async def __aexit__(self, *args):
            pass

        async def request(self, method, url, headers, **kwargs):
            assert headers["X-Api-Key"] == "unit-test-api-key"
            if url.endswith("/sql/execute"):
                assert kwargs["json"] == {"dbAlias": "LOCAL_SQLITE", "sql": "SELECT 1"}
                return Response(200, {"code": 200, "data": {"type": "scalar", "data": 1}})
            return Response(403, {"code": 403, "errorType": "REQUIRE_APPROVAL", "message": "pending", "data": {"approval_id": "abc"}})

    module.httpx = types.SimpleNamespace(
        AsyncClient=Client,
        TimeoutException=Exception,
        RequestError=Exception,
    )
    ok = asyncio.run(module.ExecuteSql("LOCAL_SQLITE", "SELECT 1"))
    assert ok == {"ok": True, "data": {"type": "scalar", "data": 1}}
    pending = asyncio.run(module.ExecuteSkill("do_write", object(), {"name": "abc"}))
    assert pending["ok"] is False
    assert pending["error_type"] == "REQUIRE_APPROVAL"
    assert pending["data"] == {"approval_id": "abc"}


def test_mcp_requires_key(monkeypatch):
    module = load_mcp_module(monkeypatch)
    module.RELAY_TOKEN = ""
    reply = asyncio.run(module.ExecuteSql("LOCAL_SQLITE", "SELECT 1"))
    assert reply["error_type"] == "CONFIG_ERROR"
