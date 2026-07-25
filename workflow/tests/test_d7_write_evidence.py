import pytest

from app.engine.tool_registry import ToolContext
from app.runtime.backend import BackendRunClient
from app.runtime.events import RuntimeEvent
from app.runtime.write_evidence import BackendWriteEvidenceSink
from app.security import RequestContext


@pytest.mark.asyncio
async def test_write_evidence_sink_uses_backend_effect_identity_and_accepts_replay(monkeypatch) -> None:
    calls: list[tuple[str, dict, dict]] = []

    class Response:
        status_code = 200

        def raise_for_status(self) -> None:
            return None

        def json(self) -> dict:
            return {"version": 1, "outcome": "replayed"}

    class Client:
        async def post(self, path: str, *, headers: dict, json: dict, timeout) -> Response:
            calls.append((path, headers, json))
            return Response()

    monkeypatch.setattr("app.runtime.write_evidence.get_client", lambda: Client())
    ctx = ToolContext(tenant_id="demo-a", user_id="user-a", role="USER", run_id="aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")

    version = await BackendWriteEvidenceSink().write(ctx, "refund-1", "approved", effect_id="bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")

    assert version == 1
    assert calls == [
        (
            "/api/agent-runs/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/write-effects/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb/evidence",
            {"X-Internal-Token": "internal-dev-token", "X-Tenant-Id": "demo-a", "X-User-Id": "user-a", "X-User-Role": "USER"},
            {"record_id": "refund-1", "value": "approved"},
        )
    ]


@pytest.mark.asyncio
async def test_write_evidence_sink_fails_closed_without_durable_identity() -> None:
    ctx = ToolContext(tenant_id="demo-a", user_id="user-a", role="USER", run_id=None)
    with pytest.raises(RuntimeError, match="durable run"):
        await BackendWriteEvidenceSink().write(ctx, "refund-1", "approved", effect_id="effect-1")


@pytest.mark.asyncio
async def test_approved_write_event_is_metered_as_redacted_tool_telemetry(monkeypatch) -> None:
    client = BackendRunClient(owner="test")
    calls: list[tuple[str, dict]] = []

    async def request(_method: str, path: str, _ctx, *, json=None, **_kwargs):
        calls.append((path, json or {}))
        if path.endswith("/events"):
            return {
                "id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "snapshot_hash": "a" * 64,
                "status": "running", "state_version": 2, "lease_generation": 1,
                "checkpoint_generation": 0, "checkpoint_version": 0,
            }
        return {"accepted": True}

    monkeypatch.setattr(client, "_request_json", request)
    event = RuntimeEvent(
        event_id="bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
        event_type="approved_write_completed",
        node_id="approved_write",
        snapshot_hash="a" * 64,
        payload={
            "tool_name": "runtime.write_evidence", "latency_ms": 17,
            "usage_units": 0, "cost_units": 0, "agent_id": "agent-1",
            "agent_revision": 2, "skill_name": None, "skill_revision": None,
            "status": "ok",
        },
    )
    await client.append_events(
        "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
        RequestContext(tenant_id="demo-a", user_id="user-a", role="USER"),
        [event], expected_version=1, lease_token="lease", lease_generation=1,
    )

    telemetry = next(body for path, body in calls if path == "/api/operations/telemetry")
    assert telemetry == {
        "run_id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
        "event_id": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", "kind": "tool",
        "node_id": "approved_write", "usage_units": 0,
        "tool_name": "runtime.write_evidence", "skill_name": None,
        "skill_revision": None, "latency_ms": 17, "cost_units": 0,
        "agent_id": "agent-1", "agent_revision": 2,
    }
