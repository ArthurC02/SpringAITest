from fastapi.testclient import TestClient

from app.main import app
from app.settings import settings
from tests.conftest import auth_headers


def test_multi_agent_feature_off_is_404_before_auth_and_body_parsing(monkeypatch):
    monkeypatch.setattr(settings, "multi_agent_dispatch_enabled", False)
    response = TestClient(app).post(
        "/orchestrator-runs/root-1/dispatch",
        content=b"{not-json",
    )
    assert response.status_code == 404
    assert response.json() == {"detail": "Not Found"}


def test_multi_agent_feature_on_rejects_incomplete_backend_snapshot(monkeypatch):
    monkeypatch.setattr(settings, "multi_agent_dispatch_enabled", True)
    response = TestClient(app).post(
        "/orchestrator-runs/root-1/dispatch",
        json={"context": {}},
    )
    assert response.status_code == 422


def test_dispatch_accepts_platform_wire_and_schedules_without_waiting(monkeypatch):
    class Supervisor:
        def schedule(self, run_id, command_id, ctx):
            return True

    monkeypatch.setattr(settings, "multi_agent_dispatch_enabled", True)
    monkeypatch.setattr(app.state, "root_runtime_supervisor", Supervisor(), raising=False)
    response = TestClient(app).post(
        "/orchestrator-runs/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa/dispatch",
        headers=auth_headers(),
        json={
            "command_id": "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
            "context": {},
        },
    )
    assert response.status_code == 202
    assert response.json() == {
        "run_id": "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
        "status": "accepted",
        "scheduled": True,
    }
