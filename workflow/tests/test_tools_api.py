from fastapi.testclient import TestClient

from app.main import app
from tests.conftest import auth_headers


def test_tool_catalog_is_authenticated():
    with TestClient(app) as client:
        response = client.get("/tools")

    assert response.status_code == 401


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
        for forbidden in ("endpoint", "token", "x-internal", "/api/", "http://", "https://")
    )
