import pytest
from fastapi.testclient import TestClient

from app.main import app
from app.settings import settings
from tests.conftest import auth_headers

RUN_ID = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"
COMMAND_ID = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"


class Supervisor:
    """Records every accepted schedule so rejected requests are provable."""

    def __init__(self, scheduled=True):
        self.scheduled = scheduled
        self.calls = []

    def schedule(self, run_id, command_id, ctx):
        self.calls.append((run_id, command_id, ctx))
        return self.scheduled


def _install(monkeypatch, supervisor):
    monkeypatch.setattr(settings, "multi_agent_dispatch_enabled", True)
    if supervisor is None:
        monkeypatch.delattr(app.state, "root_runtime_supervisor", raising=False)
    else:
        monkeypatch.setattr(
            app.state, "root_runtime_supervisor", supervisor, raising=False
        )
    return TestClient(app)


def test_multi_agent_feature_off_is_404_before_auth_and_body_parsing(monkeypatch):
    monkeypatch.setattr(settings, "multi_agent_dispatch_enabled", False)
    response = TestClient(app).post(
        "/orchestrator-runs/root-1/dispatch",
        content=b"{not-json",
    )
    assert response.status_code == 404
    assert response.json() == {"detail": "Not Found"}


@pytest.mark.parametrize(
    "body, field",
    [
        ({"context": {}}, "command_id"),
        ({"command_id": COMMAND_ID, "context": {}, "root_run_id": RUN_ID}, "root_run_id"),
    ],
)
def test_authenticated_incomplete_backend_snapshot_never_reaches_dispatch(
    monkeypatch, body, field
):
    supervisor = Supervisor()
    client = _install(monkeypatch, supervisor)

    response = client.post(
        f"/orchestrator-runs/{RUN_ID}/dispatch", headers=auth_headers(), json=body
    )

    assert response.status_code == 422
    assert [error["loc"] for error in response.json()["detail"]] == [["body", field]]
    assert supervisor.calls == []


@pytest.mark.parametrize("context", [[], "ctx", 1])
def test_strict_mode_rejects_non_dict_context_before_dispatch(monkeypatch, context):
    supervisor = Supervisor()
    client = _install(monkeypatch, supervisor)

    response = client.post(
        f"/orchestrator-runs/{RUN_ID}/dispatch",
        headers=auth_headers(),
        json={"command_id": COMMAND_ID, "context": context},
    )

    assert response.status_code == 422
    assert [error["loc"] for error in response.json()["detail"]] == [
        ["body", "context"]
    ]
    assert supervisor.calls == []


@pytest.mark.parametrize(
    "command_id, status",
    [
        ("", 422),
        ("c", 202),
        ("c" * 128, 202),
        ("c" * 129, 422),
    ],
)
def test_command_id_length_bounds_gate_dispatch(monkeypatch, command_id, status):
    supervisor = Supervisor()
    client = _install(monkeypatch, supervisor)

    response = client.post(
        f"/orchestrator-runs/{RUN_ID}/dispatch",
        headers=auth_headers(),
        json={"command_id": command_id, "context": {}},
    )

    assert response.status_code == status
    if status == 202:
        assert [call[1] for call in supervisor.calls] == [command_id]
    else:
        assert [error["loc"] for error in response.json()["detail"]] == [
            ["body", "command_id"]
        ]
        assert supervisor.calls == []


def test_dispatch_accepts_platform_wire_and_schedules_without_waiting(monkeypatch):
    supervisor = Supervisor()
    client = _install(monkeypatch, supervisor)

    response = client.post(
        f"/orchestrator-runs/{RUN_ID}/dispatch",
        headers=auth_headers(),
        json={"command_id": COMMAND_ID, "context": {}},
    )

    assert response.status_code == 202
    assert response.json() == {
        "run_id": RUN_ID,
        "status": "accepted",
        "scheduled": True,
    }
    assert [(call[0], call[1]) for call in supervisor.calls] == [(RUN_ID, COMMAND_ID)]
    ctx = supervisor.calls[0][2]
    assert (ctx.tenant_id, ctx.user_id, ctx.role) == ("demo-a", "alice", "USER")


def test_repeated_command_is_accepted_but_not_scheduled_twice(monkeypatch):
    supervisor = Supervisor(scheduled=False)
    client = _install(monkeypatch, supervisor)

    response = client.post(
        f"/orchestrator-runs/{RUN_ID}/dispatch",
        headers=auth_headers(),
        json={"command_id": COMMAND_ID, "context": {}},
    )

    assert response.status_code == 202
    assert response.json()["scheduled"] is False


@pytest.mark.parametrize(
    "headers, status, error",
    [
        (auth_headers(token=None), 401, "unauthorized"),
        (auth_headers(token="wrong-token"), 401, "unauthorized"),
        (auth_headers(tenant_id=None), 400, "missing_context"),
        (auth_headers(tenant_id="   "), 400, "missing_context"),
        (auth_headers(user_id="  "), 400, "missing_context"),
        (auth_headers(role=None), 400, "missing_context"),
    ],
)
def test_dispatch_requires_token_and_nonblank_identity(
    monkeypatch, headers, status, error
):
    supervisor = Supervisor()
    client = _install(monkeypatch, supervisor)

    response = client.post(
        f"/orchestrator-runs/{RUN_ID}/dispatch",
        headers=headers,
        json={"command_id": COMMAND_ID, "context": {}},
    )

    assert response.status_code == status
    assert response.json()["detail"]["error"] == error
    assert supervisor.calls == []


@pytest.mark.parametrize(
    "headers",
    [
        auth_headers(user_id=None),
        auth_headers(role="   "),
    ],
)
def test_dispatch_rejects_missing_user_id_and_blank_role(monkeypatch, headers):
    supervisor = Supervisor()
    client = _install(monkeypatch, supervisor)

    response = client.post(
        f"/orchestrator-runs/{RUN_ID}/dispatch",
        headers=headers,
        json={"command_id": COMMAND_ID, "context": {}},
    )

    assert response.status_code == 400
    assert response.json()["detail"]["error"] == "missing_context"
    assert supervisor.calls == []


def test_dispatch_is_503_until_the_root_runtime_supervisor_is_ready(monkeypatch):
    client = _install(monkeypatch, None)

    response = client.post(
        f"/orchestrator-runs/{RUN_ID}/dispatch",
        headers=auth_headers(),
        json={"command_id": COMMAND_ID, "context": {}},
    )

    assert response.status_code == 503
    assert response.json()["detail"]["error"] == "multi_agent_runtime_unavailable"
