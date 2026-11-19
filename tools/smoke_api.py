"""Small API smoke test against a running relay using a disposable SQLite in-memory DB.

Set:
  RELAY_API_KEY=<same as Relay__ApiKey>
  DatabaseSettings__Connections__LOCAL_SQLITE__ConnectionString='Data Source=:memory:'
Then launch dotnet and run this script in another terminal.
"""
import json
import os
import sys
from urllib.error import HTTPError
from urllib.request import Request, urlopen

BASE = os.getenv("RELAY_BASE", "http://127.0.0.1:54321").rstrip("/")
API_KEY = os.getenv("RELAY_API_KEY")
if not API_KEY:
    sys.exit("Set RELAY_API_KEY first")


def call(method, path, data=None):
    raw = json.dumps(data).encode("utf-8") if data is not None else None
    request = Request(BASE + path, data=raw, method=method, headers={
        "X-Api-Key": API_KEY, "Content-Type": "application/json"
    })
    try:
        with urlopen(request, timeout=10) as response:
            return response.status, json.load(response)
    except HTTPError as exc:
        return exc.code, json.load(exc)


def sql(text):
    return call("POST", "/sql/execute", {"dbAlias": "LOCAL_SQLITE", "sql": text})


status, response = call("GET", "/skills")
assert status == 200, response
status, response = sql("SELECT 42 AS answer")
assert status == 200 and response["data"]["type"] == "scalar" and response["data"]["data"] == 42, response
status, response = sql("SELECT 1 AS n UNION ALL SELECT 2 AS n")
assert status == 200 and response["data"]["type"] == "dataset", response
assert [r["n"] for r in response["data"]["data"]] == [1, 2], response
status, response = sql("SELECT NULL AS result")
assert status == 200 and response["data"]["data"] is None, response
status, response = sql("SELECT 1; DELETE FROM t_user")
assert status == 403 and response.get("errorType") == "SECURITY_DENIED", response
status, response = sql("WITH cte AS (DELETE FROM t_user) SELECT * FROM cte")
assert status == 403, response
status, response = sql("UPDATE t_user SET enabled=1")
assert status == 403, response
print("PASS: list, scalar, multi-row single-column, NULL and SQL write denial")
