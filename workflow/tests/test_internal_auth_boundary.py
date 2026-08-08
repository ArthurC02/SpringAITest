"""內部信任邊界的護欄：每條 API 路由都掛了認證依賴，且認證先於 body 驗證。

workflow 的內部認證靠每條路由自掛 `Depends(...)`（對照 backend 是單一
InternalTokenMiddleware，漏不了），所以「有沒有掛」需要測試守著，不能靠人工逐條核。
"""

from __future__ import annotations

import pytest
from fastapi.routing import APIRoute
from fastapi.testclient import TestClient
from starlette.routing import Route

from app.main import app
from app.runtime import api as runtime_api
from app.runtime import orchestrator_api
from app.security import get_context, require_internal
from app.settings import settings
from tests.conftest import auth_headers

# 需要租戶 context 的認證入口。它們都間接以 require_internal 為第一道；只掛 token 的
# 維運端點另列 TOKEN_ONLY_PATHS，避免新 tenant API 誤掛 require_internal 也通過盤點。
CONTEXT_AUTH_DEPENDENCIES = {
    get_context,  # 內部密鑰 + 租戶/角色 context
    runtime_api.agent_runtime_context,  # 旗標 + 密鑰 + 三個身分標頭（D3）
    runtime_api.approved_write_context,  # D7 寫入旗標 404 → 再走 D3 身分閘門
    orchestrator_api.root_runtime_context,  # 旗標 + 密鑰 + 三個身分標頭（D5）
}

TOKEN_ONLY_PATHS = {
    "/checkpoint-retention/run": "無租戶語意的全域維運端點",
}

# 刻意免認證的路由，逐項一行理由。窄名單：不在此列又沒有認證依賴 → 測試變紅。
UNAUTHENTICATED_PATHS = {
    "/health": "容器/LB 探針：compose healthcheck 沒有內部密鑰",
    "/health/live": "同上，純行程存活，不碰任何依賴",
    "/health/ready": "同上，就緒探針，回報內容不含租戶資料",
    # 以下是 FastAPI 自建的（非本服務 handler），內容只有 schema，不碰租戶資料。
    "/openapi.json": "FastAPI 內建 schema 端點",
    "/docs": "FastAPI 內建 Swagger UI",
    "/docs/oauth2-redirect": "Swagger UI 的 OAuth2 轉址頁",
    "/redoc": "FastAPI 內建 ReDoc UI",
}


def _dependency_calls(dependant) -> set:
    """遞迴收集一條路由 dependant 樹上的所有依賴 callable。"""
    calls = set()
    for sub in dependant.dependencies:
        if sub.call is not None:
            calls.add(sub.call)
        calls |= _dependency_calls(sub)
    return calls


def _walk_routes(routes) -> list:
    """攤平 app.routes。

    include_router 掛上的路由在 FastAPI 0.139 被包成 `_IncludedRouter`（沒有 path、
    也沒有 routes，真正的路由在 original_router 底下）。只認 APIRoute 的天真寫法會把
    /agent-runs、/orchestrator-runs、/workflow-designer、/evals 整批漏掉、測試變成假綠燈，
    所以這裡遞迴展開，並且對認不得的節點型別直接失敗（升級 FastAPI 時寧可紅，不要靜默漏掃）。
    """
    flat: list = []
    for route in routes:
        nested = getattr(route, "routes", None)
        if nested is None:
            included = getattr(route, "original_router", None)
            nested = None if included is None else included.routes
        if nested is not None:
            flat.extend(_walk_routes(nested))
        elif isinstance(route, Route):  # APIRoute 是 Route 的子類
            flat.append(route)
        else:
            raise AssertionError(f"未知的路由節點型別，無法稽核：{type(route)!r}")
    return flat


def _has_required_auth(route: Route) -> bool:
    if not isinstance(route, APIRoute):
        return False
    calls = _dependency_calls(route.dependant)
    if route.path in TOKEN_ONLY_PATHS:
        return require_internal in calls
    return bool(calls & CONTEXT_AUTH_DEPENDENCIES)


def test_every_api_route_declares_an_authentication_dependency() -> None:
    unprotected = [
        f"{sorted(route.methods or [])} {route.path}"
        for route in _walk_routes(app.routes)
        if route.path not in UNAUTHENTICATED_PATHS and not _has_required_auth(route)
    ]
    assert unprotected == [], (
        f"這些路由沒掛認證依賴，也不在豁免名單裡：{unprotected}"
    )


def test_the_route_walk_actually_reaches_the_included_routers() -> None:
    """釘住展開本身：漏掃就會讓上面那條斷言變成永遠通過的假綠燈。"""
    paths = {route.path for route in _walk_routes(app.routes)}
    assert {
        "/skills/{name}/invoke",
        "/agent-runs/{run_id}/start",
        "/agent-runs/{run_id}/approvals/{approval_id}/execute",
        "/orchestrator-runs/{run_id}/dispatch",
        "/workflow-designer/validate",
        "/evals/run",
    } <= paths


def test_the_exemption_allowlist_has_no_stale_entries() -> None:
    """豁免名單只能收錄真的存在的路由，刪路由時不留下悄悄放行的萬用洞。"""
    existing = {
        route.path for route in _walk_routes(app.routes)
    }
    assert set(UNAUTHENTICATED_PATHS) <= existing
    assert set(TOKEN_ONLY_PATHS) <= existing


@pytest.mark.parametrize(
    "path, flag, body",
    [
        ("/agent-runs/run-1/start", "agent_test_run_enabled", {}),
        ("/agent-runs/run-1/resume", "agent_test_run_enabled", {"command_id": ""}),
        ("/agent-runs/run-1/cancel", "agent_test_run_enabled", {"command_id": 7}),
        (
            "/orchestrator-runs/run-1/dispatch",
            "multi_agent_dispatch_enabled",
            {"context": "not-a-dict"},
        ),
    ],
    ids=["start-missing-field", "resume-empty", "cancel-wrong-type", "dispatch-context"],
)
def test_unauthenticated_malformed_body_is_401_without_field_details(
    monkeypatch: pytest.MonkeyPatch, path: str, flag: str, body: dict
) -> None:
    """未帶 token 的呼叫者不得靠畸形 body 的 422 欄位細節反推 schema。

    認證必須先於 pydantic body 驗證觸發（backend 的 AdminOnlyAttribute 明文禁止相反順序）。
    """
    monkeypatch.setattr(settings, flag, True)

    response = TestClient(app).post(path, headers=auth_headers(token=None), json=body)

    assert response.status_code == 401
    assert response.json()["detail"]["error"] == "unauthorized"
    assert "command_id" not in response.text
    assert "context" not in response.text


@pytest.mark.parametrize(
    "path, flag, body",
    [
        ("/agent-runs/run-1/start", "agent_test_run_enabled", {}),
        (
            "/orchestrator-runs/run-1/dispatch",
            "multi_agent_dispatch_enabled",
            {"context": "not-a-dict"},
        ),
    ],
    ids=["agent-runs", "orchestrator-runs"],
)
def test_authenticated_malformed_body_still_gets_422(
    monkeypatch: pytest.MonkeyPatch, path: str, flag: str, body: dict
) -> None:
    """決策表的另一半：認證通過後，畸形 body 照樣拿到可修的 422 欄位細節。"""
    monkeypatch.setattr(settings, flag, True)
    monkeypatch.setattr(app.state, "agent_runtime_manager", None, raising=False)
    monkeypatch.delattr(app.state, "root_runtime_supervisor", raising=False)

    response = TestClient(app).post(path, headers=auth_headers(), json=body)

    assert response.status_code == 422
