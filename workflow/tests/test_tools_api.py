from fastapi.testclient import TestClient

from app.main import app
from tests.conftest import auth_headers


def test_tool_catalog_is_authenticated():
    with TestClient(app) as client:
        response = client.get("/tools")

    assert response.status_code == 401


def test_tool_catalog_with_valid_token_but_no_identity_headers_returns_400():
    """認證通過但缺租戶／角色 → 400 missing_context（與 401 是兩條不同的失敗分支）。"""
    with TestClient(app) as client:
        response = client.get("/tools", headers=auth_headers(tenant_id=None, role=None))

    assert response.status_code == 400
    assert response.json()["detail"]["error"] == "missing_context"


def test_tool_catalog_exposes_safe_builder_metadata_only():
    with TestClient(app) as client:
        response = client.get("/tools", headers=auth_headers())

    assert response.status_code == 200
    tools = {item["name"]: item for item in response.json()}
    assert tools["backend.retrieval_search"] == {
        "name": "backend.retrieval_search",
        "kind": "http",
        "description": "在目前租戶已授權的知識庫中進行向量檢索",
        "risk": "read",
        "returns": "list[chunk]",
    }
    assert tools["local.calculator"]["risk"] == "low"
    serialized = response.text.lower()
    assert all(
        forbidden not in serialized
        for forbidden in (
            "endpoint",
            "token",
            "x-internal",
            "/api/",
            "http://",
            "https://",
            "callable",
            "args_schema",
        )
    )


def test_tool_catalog_lists_the_write_risk_tool_with_its_write_tag():
    """唯一的 write 風險 tool（D7 runtime.write_evidence）必須在目錄裡且標成 write。

    目錄本身不套 AGENT_WRITE_TOOLS_ENABLED —— 旗標與租戶白名單是呼叫期的閘門
    （app/tools.py::write_evidence），Builder picker 看得到但不代表叫得動。
    """
    with TestClient(app) as client:
        response = client.get("/tools", headers=auth_headers())

    assert response.status_code == 200
    tools = {item["name"]: item for item in response.json()}
    assert tools["runtime.write_evidence"] == {
        "name": "runtime.write_evidence",
        "kind": "local",
        "description": (
            "Deterministic, tenant-allowlisted evidence write sink requiring durable approval."
        ),
        "risk": "write",
        "returns": "dict",
    }
