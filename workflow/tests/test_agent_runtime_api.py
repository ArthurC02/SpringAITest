from __future__ import annotations

from fastapi.testclient import TestClient

from app.main import app
from app.runtime.models import RuntimeRunResult
from app.runtime.models import (
    DirectAgentExecutionSnapshot,
    canonical_json_sha256,
)
from app.settings import settings
from tests.conftest import auth_headers
from tests.test_agent_runtime import snapshot


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


def test_runtime_feature_off_returns_404_before_auth(monkeypatch) -> None:
    monkeypatch.setattr(settings, "agent_test_run_enabled", False)
    response = TestClient(app).post(
        "/agent-runs/271c9de5-d772-48d7-8236-b6a46f4588f5/start",
        json={},
    )
    assert response.status_code == 404


def test_runtime_internal_endpoint_contracts(monkeypatch) -> None:
    monkeypatch.setattr(settings, "agent_test_run_enabled", True)
    manager = FakeManager()
    app.state.agent_runtime_manager = manager
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
