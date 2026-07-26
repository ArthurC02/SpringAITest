from __future__ import annotations

import pytest
from fastapi.testclient import TestClient

from app.main import app
from app.runtime.backend import BackendRunConflict, BackendRunError
from app.runtime.manager import RuntimeManagerConflict, RuntimeManagerError
from app.runtime.models import RuntimeRunResult
from app.runtime.models import (
    DirectAgentExecutionSnapshot,
    canonical_json_sha256,
)
from app.settings import settings
from tests.conftest import auth_headers
from tests.test_agent_runtime import snapshot

RUN_ID = "271c9de5-d772-48d7-8236-b6a46f4588f5"


class FakeManager:
    def __init__(self):
        self.calls = []

    async def dispatch_command(self, run_id, command_id, ctx):
        self.calls.append((command_id, run_id, ctx))
        return RuntimeRunResult(
            run_id=run_id,
            status="running",
            snapshot_hash="d" * 64,
            checkpoint_version=0,
        )


@pytest.fixture
def runtime_manager(monkeypatch) -> FakeManager:
    """app.state 是 module 級單例：直接指派而不還原，這個 fake 會漏給同一個 session
    的其他測試（test_business_rules.py 等也 import 同一個 app）。monkeypatch 會在
    測試結束後移除（原本不存在時走 delattr），順序隨機化下也不留殘留狀態。"""
    manager = FakeManager()
    monkeypatch.setattr(app.state, "agent_runtime_manager", manager, raising=False)
    return manager


def test_runtime_feature_off_returns_404_before_auth(monkeypatch) -> None:
    monkeypatch.setattr(settings, "agent_test_run_enabled", False)
    response = TestClient(app).post(
        "/agent-runs/271c9de5-d772-48d7-8236-b6a46f4588f5/start",
        json={},
    )
    assert response.status_code == 404


def test_runtime_internal_endpoint_contracts(monkeypatch, runtime_manager) -> None:
    monkeypatch.setattr(settings, "agent_test_run_enabled", True)
    manager = runtime_manager
    raw = snapshot().model_dump(mode="json", exclude={"snapshot_hash"})
    raw["caller"]["tenant_id"] = "tenant-a"
    raw["snapshot_hash"] = canonical_json_sha256(raw)
    run_snapshot = DirectAgentExecutionSnapshot.model_validate(raw)
    headers = auth_headers(
        tenant_id=run_snapshot.caller.tenant_id,
        user_id=run_snapshot.caller.user_id,
        role=run_snapshot.caller.role,
    )
    client = TestClient(app)
    start = client.post(
        f"/agent-runs/{run_snapshot.run_id}/start",
        headers=headers,
        json={"command_id": "cmd-start-1"},
    )
    assert start.status_code == 202
    resume = client.post(
        f"/agent-runs/{run_snapshot.run_id}/resume",
        headers=headers,
        json={"command_id": "cmd-resume-1"},
    )
    assert resume.status_code == 202
    cancel = client.post(
        f"/agent-runs/{run_snapshot.run_id}/cancel",
        headers=headers,
        json={"command_id": "cmd-cancel-1"},
    )
    assert cancel.status_code == 202
    assert [item[0] for item in manager.calls] == [
        "cmd-start-1",
        "cmd-resume-1",
        "cmd-cancel-1",
    ]


class RaisingManager:
    def __init__(self, error: Exception):
        self.error = error
        self.calls = 0

    async def dispatch_command(self, run_id, command_id, ctx):
        self.calls += 1
        raise self.error

    async def execute_approved_write(self, run_id, approval_id, ctx):
        self.calls += 1
        raise self.error


@pytest.mark.parametrize(
    ("error", "status", "code"),
    [
        (RuntimeManagerConflict("owned"), 409, "agent_run_conflict"),
        (BackendRunConflict("cas"), 409, "agent_run_conflict"),
        (RuntimeManagerError("bad"), 400, "agent_run_invalid"),
        (ValueError("bad ref"), 400, "agent_run_invalid"),
        (BackendRunError("down"), 503, "agent_runtime_dependency_failed"),
    ],
    ids=["manager-conflict", "backend-conflict", "manager-error", "value-error", "backend-error"],
)
def test_runtime_api_error_mapping(
    monkeypatch: pytest.MonkeyPatch, error: Exception, status: int, code: str
) -> None:
    """`_call` 的三條例外映射過去零覆蓋,錯一條就會把衝突當成 500 丟給 Backend。"""
    monkeypatch.setattr(settings, "agent_test_run_enabled", True)
    manager = RaisingManager(error)
    monkeypatch.setattr(app.state, "agent_runtime_manager", manager, raising=False)

    response = TestClient(app).post(
        f"/agent-runs/{RUN_ID}/start",
        headers=auth_headers(),
        json={"command_id": "cmd-1"},
    )

    assert response.status_code == status
    assert response.json()["detail"]["error"] == code
    assert manager.calls == 1


@pytest.mark.parametrize(
    ("headers", "status", "code"),
    [
        (auth_headers(token=None), 401, "unauthorized"),
        (auth_headers(token="wrong-token"), 401, "unauthorized"),
        (auth_headers(tenant_id=None), 400, "missing_context"),
        (auth_headers(user_id=None), 400, "missing_context"),
        (auth_headers(role=None), 400, "missing_context"),
        (auth_headers(tenant_id="   "), 400, "missing_context"),
    ],
    ids=["no-token", "wrong-token", "no-tenant", "no-user", "no-role", "blank-tenant"],
)
def test_runtime_api_rejects_incomplete_identity(
    monkeypatch: pytest.MonkeyPatch, runtime_manager, headers: dict, status: int, code: str
) -> None:
    monkeypatch.setattr(settings, "agent_test_run_enabled", True)

    response = TestClient(app).post(
        f"/agent-runs/{RUN_ID}/start", headers=headers, json={"command_id": "cmd-1"}
    )

    assert response.status_code == status
    assert response.json()["detail"]["error"] == code
    assert runtime_manager.calls == []


def test_runtime_api_reports_unavailable_manager(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setattr(settings, "agent_test_run_enabled", True)
    monkeypatch.setattr(app.state, "agent_runtime_manager", None, raising=False)

    response = TestClient(app).post(
        f"/agent-runs/{RUN_ID}/cancel",
        headers=auth_headers(),
        json={"command_id": "cmd-1"},
    )

    assert response.status_code == 503
    assert response.json()["detail"]["error"] == "agent_runtime_unavailable"


@pytest.mark.parametrize("write_enabled", [False, True])
def test_approved_write_endpoint_follows_the_write_flag(
    monkeypatch: pytest.MonkeyPatch, write_enabled: bool
) -> None:
    monkeypatch.setattr(settings, "agent_test_run_enabled", True)
    monkeypatch.setattr(settings, "agent_write_tools_enabled", write_enabled)
    calls: list[tuple[str, str]] = []

    class WriteManager:
        async def execute_approved_write(self, run_id, approval_id, ctx):
            calls.append((run_id, approval_id))
            return RuntimeRunResult(
                run_id=run_id,
                status="completed",
                snapshot_hash="d" * 64,
                checkpoint_version=3,
            )

    monkeypatch.setattr(
        app.state, "agent_runtime_manager", WriteManager(), raising=False
    )
    approval_id = "3f8b2f5e-6c42-4abc-8def-0123456789ab"

    response = TestClient(app).post(
        f"/agent-runs/{RUN_ID}/approvals/{approval_id}/execute",
        headers=auth_headers(),
        json={},
    )

    if write_enabled:
        assert response.status_code == 202
        assert calls == [(RUN_ID, approval_id)]
    else:
        assert response.status_code == 404
        assert calls == []
