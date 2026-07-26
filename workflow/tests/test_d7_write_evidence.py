from types import SimpleNamespace

import httpx
import pytest
from langgraph.checkpoint.memory import InMemorySaver

from app.engine import tool_registry
from app.engine.tool_registry import ToolContext
from app.runtime.backend import (
    ApprovalExecutionClaim,
    ApprovalExecutionIdentity,
    BackendRunClient,
    EffectClaim,
    LeaseRecord,
    RunRecord,
)
from app.runtime.checkpoints import checkpoint_config, strict_serializer
from app.runtime.events import RuntimeEvent
from app.runtime.manager import RuntimeManagerConflict, RuntimeRunManager
from app.runtime.tool_boundary import ToolObservation, effective_tool_names
from app.runtime.write_evidence import BackendWriteEvidenceSink, WriteEvidenceError
from app.security import RequestContext
from app.settings import settings
from tests.test_agent_runtime import snapshot as agent_snapshot

WRITE_TOOLS = frozenset(
    spec.name for spec in tool_registry.all_specs() if spec.risk == "write"
)
APPROVAL_ID = "dddddddd-dddd-4ddd-8ddd-dddddddddddd"
EFFECT_ID = "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee"
CHECKPOINT_ID = "018f6f21-6c42-7abc-8def-0123456789ab"
RUN_SNAPSHOT = agent_snapshot(tools=["runtime.write_evidence"])
CHECKPOINT_REF = "v2:1:{thread}:{checkpoint}".format(
    thread=checkpoint_config(
        tenant_id=RUN_SNAPSHOT.caller.tenant_id,
        user_id=RUN_SNAPSHOT.caller.user_id,
        run_id=RUN_SNAPSHOT.run_id,
        snapshot_hash=RUN_SNAPSHOT.snapshot_hash,
        lease_generation=1,
    )["configurable"]["thread_id"],
    checkpoint=CHECKPOINT_ID,
)


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
@pytest.mark.parametrize(
    "run_id, effect_id",
    [
        (None, "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
        ("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", ""),
        (None, ""),
    ],
)
async def test_write_evidence_sink_fails_closed_without_durable_identity(
    run_id, effect_id
) -> None:
    ctx = ToolContext(tenant_id="demo-a", user_id="user-a", role="USER", run_id=run_id)
    with pytest.raises(WriteEvidenceError, match="durable run and effect identities"):
        await BackendWriteEvidenceSink().write(ctx, "refund-1", "approved", effect_id=effect_id)


def _install_sink_client(monkeypatch, status_code: int, body) -> None:
    class Response:
        def __init__(self):
            self.status_code = status_code

        def raise_for_status(self) -> None:
            if status_code >= 400:
                raise httpx.HTTPStatusError(
                    "error", request=httpx.Request("POST", "http://backend"), response=None
                )

        def json(self):
            return body

    class Client:
        async def post(self, path: str, *, headers: dict, json: dict, timeout) -> Response:
            return Response()

    monkeypatch.setattr("app.runtime.write_evidence.get_client", lambda: Client())


@pytest.mark.asyncio
@pytest.mark.parametrize(
    "status_code, body, message",
    [
        (404, {"version": 1}, "durable write evidence was rejected"),
        (409, {"version": 1}, "durable write evidence was rejected"),
        (200, {"version": 0}, "invalid durable evidence"),
        (200, {"outcome": "written"}, "invalid durable evidence"),
        (200, {"version": "1"}, "invalid durable evidence"),
        (200, ["not", "an", "object"], "invalid durable evidence"),
        # JSON booleans are `int` subclasses in Python: `True` would otherwise
        # pass `isinstance(version, int) and version >= 1` and be returned as a
        # durable evidence version. A boolean version is always a broken Backend
        # response, so it fails closed.
        (200, {"version": True}, "invalid durable evidence"),
        (200, {"version": False}, "invalid durable evidence"),
        (500, {"version": 1}, "durable write evidence is unavailable"),
    ],
)
async def test_write_evidence_sink_fail_closed_matrix(
    monkeypatch, status_code, body, message
) -> None:
    _install_sink_client(monkeypatch, status_code, body)
    ctx = ToolContext(
        tenant_id="demo-a", user_id="user-a", role="USER",
        run_id="aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
    )

    with pytest.raises(WriteEvidenceError, match=message):
        await BackendWriteEvidenceSink().write(
            ctx, "refund-1", "approved",
            effect_id="bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
        )


@pytest.mark.parametrize(
    "enabled, tools, tenants, expected",
    [
        (True, WRITE_TOOLS, {"tenant-甲"}, WRITE_TOOLS),
        (True, WRITE_TOOLS, {"tenant-other"}, frozenset()),
        (True, WRITE_TOOLS, set(), frozenset()),
        (True, set(), {"tenant-甲"}, frozenset()),
        (False, WRITE_TOOLS, {"tenant-甲"}, frozenset()),
    ],
)
def test_write_tool_stays_denied_outside_the_server_allowlists(
    monkeypatch, enabled, tools, tenants, expected
) -> None:
    """Agent grants never suffice: the server allowlists gate every write tool."""
    monkeypatch.setattr(settings, "agent_write_tools_enabled", enabled)
    monkeypatch.setattr(settings, "agent_write_tools_allowlist", ",".join(sorted(tools)))
    monkeypatch.setattr(
        settings, "agent_write_tools_tenant_allowlist", ",".join(sorted(tenants))
    )
    granted = agent_snapshot(tools=sorted(WRITE_TOOLS))

    names = effective_tool_names(granted, artifact=None, rule_tools=None, deps=None)

    assert WRITE_TOOLS
    assert names & WRITE_TOOLS == expected


class ApprovalBackend:
    """Backend owns the approval/effect ledger; Workflow only consumes it once."""

    def __init__(self, run_snapshot, *, outcome="granted", status="queued", cancel=False):
        self.snapshot = run_snapshot
        self.outcome = outcome
        self.record = RunRecord(
            id=run_snapshot.run_id,
            snapshot_hash=run_snapshot.snapshot_hash,
            status=status,
            state_version=3,
            checkpoint_ref=CHECKPOINT_REF,
            checkpoint_version=2,
            checkpoint_generation=1,
            lease_generation=1,
            cancel_requested=cancel,
        )
        self.consumed = []
        self.effects = []
        self.events = []
        self.transitions = []
        self.execution_completions = []

    async def claim_approval_execution(self, run_id, approval_id, ctx):
        return ApprovalExecutionClaim(
            approval_id=approval_id, run_id=run_id, tenant_id=ctx.tenant_id,
            approver_id="approver-1", claim_token="execute-claim",
        )

    async def approval_execution_identity(self, run_id, approval_id, ctx):
        return ApprovalExecutionIdentity(user_id="admin-1", role="ADMIN")

    async def get_run(self, run_id, ctx):
        return self.record.model_copy(deep=True)

    async def execution_snapshot(self, run_id, ctx):
        return self.snapshot

    async def claim_lease(self, run_id, ctx, expected_version):
        return LeaseRecord(
            lease_token="lease-1", lease_generation=1, lease_expires_at="2099-01-01T00:00:00Z",
            checkpoint_generation=1, checkpoint_ref=CHECKPOINT_REF, checkpoint_version=2,
            event_ack_cursor=0, run=self.record,
        )

    async def consume_approval(self, run_id, approval_id, ctx, **kwargs):
        self.consumed.append((approval_id, kwargs))
        return EffectClaim(effect_id=EFFECT_ID, outcome=self.outcome)

    async def complete_effect(self, run_id, effect_id, ctx, *, succeeded):
        self.effects.append((effect_id, succeeded))

    async def append_events(self, run_id, ctx, events, **kwargs):
        self.events.extend(events)
        return self.record.model_copy(update={"state_version": self.record.state_version + 1})

    async def transition(self, run_id, ctx, **kwargs):
        self.transitions.append(kwargs)
        return self.record.model_copy(
            update={"status": kwargs["to_status"], "checkpoint_version": kwargs["checkpoint_version"]}
        )

    async def complete_approval_execution(self, approval_id, claim_token, *, dead_letter=False):
        self.execution_completions.append((approval_id, claim_token, dead_letter))


def _approved_write_manager(monkeypatch, backend, run_snapshot):
    """A manager whose only fake is the checkpoint read and the write tool."""
    manager = RuntimeRunManager(
        checkpointer=InMemorySaver(serde=strict_serializer()),
        backend=backend,
        model=SimpleNamespace(),
    )

    async def aget_state(_config):
        return SimpleNamespace(
            values={
                "pending_approval": {
                    "command": {
                        "kind": "tool_call",
                        "name": "runtime.write_evidence",
                        "arguments": {"record_id": "refund-1", "value": "approved"},
                    },
                    "action_fingerprint": "fingerprint-1",
                }
            }
        )

    manager.graph = SimpleNamespace(aget_state=aget_state)
    invocations = []

    async def invoke(**kwargs):
        invocations.append(kwargs)
        return ToolObservation(content='{"status":"written"}', fingerprint="fingerprint-1")

    monkeypatch.setattr("app.runtime.manager.invoke_approved_write_tool", invoke)
    return manager, invocations


def _approval_context():
    return RequestContext(tenant_id="tenant-甲", user_id="approver-1", role="USER")


@pytest.mark.asyncio
async def test_approved_write_consumes_the_effect_once_and_invokes_the_tool(monkeypatch):
    run_snapshot = RUN_SNAPSHOT
    backend = ApprovalBackend(run_snapshot, outcome="granted")
    manager, invocations = _approved_write_manager(monkeypatch, backend, run_snapshot)

    result = await manager.execute_approved_write(
        run_snapshot.run_id, APPROVAL_ID, _approval_context()
    )

    assert result.status == "completed"
    assert len(invocations) == 1
    assert invocations[0]["effect_id"] == EFFECT_ID
    assert backend.consumed[0][1]["action_fingerprint"] == "fingerprint-1"
    assert backend.effects == [(EFFECT_ID, True)]
    assert backend.events[0].payload["status"] == "ok"
    assert backend.transitions[-1]["to_status"] == "completed"
    assert backend.execution_completions == [(APPROVAL_ID, "execute-claim", False)]


@pytest.mark.asyncio
async def test_replayed_approval_never_invokes_the_write_tool_a_second_time(monkeypatch):
    """Backend already recorded the effect: the replay must be bookkeeping only."""
    run_snapshot = RUN_SNAPSHOT
    backend = ApprovalBackend(run_snapshot, outcome="completed")
    manager, invocations = _approved_write_manager(monkeypatch, backend, run_snapshot)

    result = await manager.execute_approved_write(
        run_snapshot.run_id, APPROVAL_ID, _approval_context()
    )

    assert result.status == "completed"
    assert invocations == []
    assert backend.effects == []
    assert backend.events[0].payload["status"] == "replayed"
    assert backend.events[0].payload["tool_name"] == "runtime.write_evidence"
    assert backend.transitions[-1]["to_status"] == "completed"
    assert backend.execution_completions == [(APPROVAL_ID, "execute-claim", False)]


@pytest.mark.asyncio
@pytest.mark.parametrize("outcome", ["rejected", "expired", "waiting"])
async def test_unresolvable_approval_outcome_is_a_dead_lettered_conflict(
    monkeypatch, outcome
):
    run_snapshot = RUN_SNAPSHOT
    backend = ApprovalBackend(run_snapshot, outcome=outcome)
    manager, invocations = _approved_write_manager(monkeypatch, backend, run_snapshot)

    with pytest.raises(RuntimeManagerConflict, match="cannot be safely resumed"):
        await manager.execute_approved_write(
            run_snapshot.run_id, APPROVAL_ID, _approval_context()
        )

    assert invocations == []
    assert backend.transitions == []
    assert backend.execution_completions == [(APPROVAL_ID, "execute-claim", True)]


@pytest.mark.asyncio
async def test_cancelled_run_conflicts_before_the_effect_is_consumed(monkeypatch):
    run_snapshot = RUN_SNAPSHOT
    backend = ApprovalBackend(run_snapshot, cancel=True)
    manager, invocations = _approved_write_manager(monkeypatch, backend, run_snapshot)

    with pytest.raises(RuntimeManagerConflict, match="approved write was cancelled"):
        await manager.execute_approved_write(
            run_snapshot.run_id, APPROVAL_ID, _approval_context()
        )

    assert invocations == []
    assert backend.consumed == []
    assert backend.execution_completions == [(APPROVAL_ID, "execute-claim", True)]


@pytest.mark.asyncio
async def test_terminal_run_dead_letters_the_execute_claim_without_writing(monkeypatch):
    run_snapshot = RUN_SNAPSHOT
    backend = ApprovalBackend(run_snapshot, status="cancelled")
    manager, invocations = _approved_write_manager(monkeypatch, backend, run_snapshot)

    result = await manager.execute_approved_write(
        run_snapshot.run_id, APPROVAL_ID, _approval_context()
    )

    assert result.status == "cancelled"
    assert invocations == []
    assert backend.consumed == []
    assert backend.transitions == []
    assert backend.execution_completions == [(APPROVAL_ID, "execute-claim", True)]


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
