from __future__ import annotations

import asyncio
import base64
import hashlib
import json
import uuid
from types import MethodType, SimpleNamespace
from typing import Any

import pytest
from langgraph.checkpoint.memory import InMemorySaver
from langgraph.types import Command

from app.runtime.backend import (
    BackendRunClient,
    BackendRunConflict,
    BackendRunError,
    LeaseRecord,
    RecoveryClaimResponse,
    RecoveryCommand,
    RunRecord,
)
from app.runtime.checkpoints import (
    checkpoint_config,
    checkpoint_ref,
    clone_checkpoint_generation,
    strict_serializer,
)
from app.runtime.graph import build_context, compile_runtime_graph, initial_state
from app.runtime.events import MAX_EVENT_PAYLOAD_JSON_BYTES, runtime_event
from app.runtime.facts import ProposedAction, caller_envelopes
from app.runtime.manager import RuntimeManagerConflict, RuntimeRunManager
from app.runtime.artifacts import RevisionArtifactReader
from app.runtime.model import ModelTurn
from app.runtime.models import (
    MAX_BACKEND_RESULT_JSON_BYTES,
    DirectAgentExecutionSnapshot,
    RuntimeCommand,
    backend_result_wire_size,
    canonical_json_bytes,
    canonical_json_sha256,
    parse_json_preserving_numbers,
)
from app.settings import settings
from app.runtime.policy import PreActionPolicy
from tests.test_agent_runtime import (
    FakeArtifactReader,
    FakeModel,
    request_context,
    snapshot,
)


class FakeBackend:
    def __init__(self, run_snapshot):
        self.snapshot = run_snapshot
        self.record = RunRecord(
            id=run_snapshot.run_id,
            snapshot_hash=run_snapshot.snapshot_hash,
            status="queued",
            state_version=0,
            checkpoint_ref=None,
            checkpoint_version=0,
            cancel_requested=False,
        )
        self.token = ""
        self.events: dict[str, dict[str, Any]] = {}
        self.recovery_items: list[RecoveryCommand] = []
        self.completed_commands: list[str] = []
        self.transition_calls = 0
        self.transition_payloads: list[dict[str, Any]] = []
        self.event_batch_sizes: list[int] = []
        self.fail_event_batch_call: int | None = None

    async def get_run(self, run_id, ctx):
        assert run_id == self.snapshot.run_id
        return self.record.model_copy(deep=True)

    async def execution_snapshot(self, run_id, ctx):
        return self.snapshot

    async def claim_lease(self, run_id, ctx, expected_version):
        assert expected_version == self.record.state_version
        self.token = f"lease-{self.record.state_version + 1}"
        self.record = self.record.model_copy(
            update={"state_version": self.record.state_version + 1}
        )
        return LeaseRecord(
            lease_token=self.token,
            lease_expires_at="2099-01-01T00:00:00Z",
            run=self.record,
        )

    async def transition(self, run_id, ctx, **kwargs):
        self.transition_calls += 1
        self.transition_payloads.append(dict(kwargs))
        assert kwargs["expected_version"] == self.record.state_version
        assert kwargs["lease_token"] == self.token
        self.record = self.record.model_copy(
            update={
                "status": kwargs["to_status"],
                "state_version": self.record.state_version + 1,
                "checkpoint_ref": kwargs.get("checkpoint_ref"),
                "checkpoint_version": (
                    kwargs.get("checkpoint_version")
                    if kwargs.get("checkpoint_version") is not None
                    else self.record.checkpoint_version
                ),
            }
        )
        return self.record.model_copy(deep=True)

    async def append_events(
        self, run_id, ctx, events, *, expected_version, lease_token, **kwargs
    ):
        assert expected_version == self.record.state_version
        assert lease_token == self.token
        self.event_batch_sizes.append(len(events))
        if self.fail_event_batch_call == len(self.event_batch_sizes):
            self.fail_event_batch_call = None
            raise BackendRunError("transient event append failure")
        for event in events:
            self.events[event.event_id] = event.as_backend_dict()
        self.record = self.record.model_copy(
            update={
                "event_ack_cursor": kwargs.get("event_cursor_start", 0) + len(events)
            }
        )
        return self.record.model_copy(deep=True)

    def public_resume(self) -> None:
        assert self.record.status == "waiting_input"
        self.record = self.record.model_copy(
            update={
                "status": "queued",
                "state_version": self.record.state_version + 1,
            }
        )

    def public_cancel_running(self) -> None:
        assert self.record.status == "running"
        self.record = self.record.model_copy(
            update={
                "cancel_requested": True,
                "state_version": self.record.state_version + 1,
            }
        )

    async def claim_recovery_commands(self, *, limit, lease_seconds):
        items, self.recovery_items = self.recovery_items[:limit], self.recovery_items[limit:]
        return RecoveryClaimResponse(items=items, has_more=bool(self.recovery_items))

    async def complete_recovery_dispatch(self, command, ctx):
        self.completed_commands.append(command.command_id)


async def wait_status(backend: FakeBackend, status: str) -> None:
    for _ in range(200):
        if backend.record.status == status:
            return
        await asyncio.sleep(0.01)
    raise AssertionError(f"run did not reach {status}: {backend.record}")


@pytest.mark.asyncio
async def test_lease_refresh_retries_transient_failure_without_losing_ownership(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    run_snapshot = snapshot()
    backend = FakeBackend(run_snapshot)
    calls = 0

    async def claim(run_id, ctx, expected_version):
        nonlocal calls
        calls += 1
        if calls == 1:
            raise BackendRunError("temporary")
        return LeaseRecord(
            lease_token="renewed",
            lease_expires_at="2099-01-01T00:00:00Z",
            run=backend.record,
        )

    sleeps = 0

    async def controlled_sleep(_seconds):
        nonlocal sleeps
        sleeps += 1
        if sleeps == 3:
            raise asyncio.CancelledError

    backend.claim_lease = claim  # type: ignore[method-assign]
    monkeypatch.setattr("app.runtime.manager.asyncio.sleep", controlled_sleep)
    control = SimpleNamespace(reasons=[], request_drain=lambda reason: control.reasons.append(reason))
    handle = SimpleNamespace(
        run_id=run_snapshot.run_id,
        ctx=request_context(),
        state_version=backend.record.state_version,
        lease_token="original",
        cancel_requested=False,
        lease_lost=False,
        state_lock=asyncio.Lock(),
        control=control,
    )
    manager = RuntimeRunManager(
        checkpointer=InMemorySaver(serde=strict_serializer()),
        backend=backend,
        model=FakeModel([]),
    )
    with pytest.raises(asyncio.CancelledError):
        await manager._renew_lease(handle)
    assert calls == 2
    assert handle.lease_token == "renewed"
    assert handle.lease_lost is False
    assert control.reasons == []


@pytest.mark.asyncio
async def test_lease_refresh_marks_only_confirmed_conflict_as_ownership_loss(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    run_snapshot = snapshot()
    backend = FakeBackend(run_snapshot)

    async def conflict(*_args, **_kwargs):
        raise BackendRunConflict("owned elsewhere")

    async def immediate_sleep(_seconds):
        return None

    backend.claim_lease = conflict  # type: ignore[method-assign]
    monkeypatch.setattr("app.runtime.manager.asyncio.sleep", immediate_sleep)
    control = SimpleNamespace(reasons=[], request_drain=lambda reason: control.reasons.append(reason))
    handle = SimpleNamespace(
        run_id=run_snapshot.run_id,
        ctx=request_context(),
        state_version=backend.record.state_version,
        lease_token="original",
        cancel_requested=False,
        lease_lost=False,
        state_lock=asyncio.Lock(),
        control=control,
    )
    manager = RuntimeRunManager(
        checkpointer=InMemorySaver(serde=strict_serializer()),
        backend=backend,
        model=FakeModel([]),
    )
    with pytest.raises(BackendRunConflict):
        await manager._renew_lease(handle)
    assert handle.lease_lost is True
    assert control.reasons == ["lease_lost"]


@pytest.mark.asyncio
async def test_manager_wait_resume_uses_same_checkpoint_and_cas() -> None:
    run_snapshot = snapshot()
    backend = FakeBackend(run_snapshot)
    model = FakeModel(
        [
            RuntimeCommand(kind="request_input", content="Which year?"),
            RuntimeCommand(kind="final", content="2025"),
        ]
    )
    manager = RuntimeRunManager(
        checkpointer=InMemorySaver(serde=strict_serializer()),
        backend=backend,
        model=model,
    )
    started = await manager.start(run_snapshot, "begin", request_context())
    assert started.status == "running"
    await wait_status(backend, "waiting_input")
    waiting_version = backend.record.checkpoint_version
    assert waiting_version == 1
    backend.public_resume()

    resumed = await manager.resume(
        run_snapshot.run_id, "2025", waiting_version, request_context()
    )
    assert resumed.status == "running"
    await wait_status(backend, "completed")
    assert backend.record.checkpoint_version == 2
    assert len(backend.events) > 0
    await manager.close()


@pytest.mark.asyncio
async def test_real_root_graph_uses_generation_specific_thread_with_empty_namespace() -> None:
    run_snapshot = snapshot()
    backend = FakeBackend(run_snapshot)
    backend.record = backend.record.model_copy(
        update={"lease_generation": 1, "checkpoint_generation": 0}
    )
    model = FakeModel(
        [
            RuntimeCommand(kind="request_input", content="Which year?"),
            RuntimeCommand(kind="final", content="2025"),
        ]
    )
    manager = RuntimeRunManager(
        checkpointer=InMemorySaver(serde=strict_serializer()),
        backend=backend,
        model=model,
    )
    command = RecoveryCommand(
        command_id="start-g1",
        run_id=run_snapshot.run_id,
        command_type="start",
        input={"message": "begin"},
        tenant_id=run_snapshot.caller.tenant_id,
        user_id=run_snapshot.caller.user_id,
        role=run_snapshot.caller.role,
        snapshot_hash=run_snapshot.snapshot_hash,
        run_status="queued",
        state_version=backend.record.state_version,
        lease_generation=1,
        checkpoint_generation=0,
        checkpoint_version=0,
        lease_token="lease-g1",
        claim_token="claim-g1",
        claim_expires_at="2099-01-01T00:00:00Z",
        dispatch_attempt=1,
    )
    backend.token = command.lease_token

    await manager._start_locked(
        run_snapshot,
        "begin",
        request_context(),
        claimed_command=command,
    )
    await wait_status(backend, "waiting_input")

    assert backend.record.checkpoint_ref is not None
    assert backend.record.checkpoint_ref.startswith("v2:1:")
    handle_config = checkpoint_config(
        tenant_id=run_snapshot.caller.tenant_id,
        user_id=run_snapshot.caller.user_id,
        run_id=run_snapshot.run_id,
        snapshot_hash=run_snapshot.snapshot_hash,
        lease_generation=1,
    )
    assert handle_config["configurable"]["checkpoint_ns"] == ""
    state = await manager.graph.aget_state(handle_config)
    assert state.interrupts
    waiting_ref = backend.record.checkpoint_ref
    waiting_version = backend.record.checkpoint_version
    backend.public_resume()
    canonical = canonical_json_bytes(
        run_snapshot.model_dump(mode="json", exclude={"snapshot_hash"})
    )
    resume_command = command.model_copy(
        update={
            "command_id": "resume-g1",
            "command_type": "resume",
            "input": {
                "message": "2025",
                "expected_checkpoint_version": waiting_version,
                "expected_checkpoint_ref": waiting_ref,
            },
            "state_version": backend.record.state_version,
            "checkpoint_generation": 1,
            "checkpoint_ref": waiting_ref,
            "checkpoint_version": waiting_version,
            "snapshot": {
                "snapshot_hash": run_snapshot.snapshot_hash,
                "snapshot_canonical_base64": base64.b64encode(canonical).decode("ascii"),
            },
        }
    )
    await manager._recover_resume_locked(resume_command, request_context())
    await wait_status(backend, "completed")
    running_transition = [
        item for item in backend.transition_payloads if item["to_status"] == "running"
    ][-1]
    assert running_transition["checkpoint_ref"] is None
    assert running_transition["checkpoint_version"] is None


@pytest.mark.asyncio
@pytest.mark.parametrize("consumed", [False, True])
async def test_g2_g3_resume_crash_window_classifies_progress(consumed: bool) -> None:
    raw = snapshot().model_dump(mode="python")
    raw.pop("snapshot_hash")
    raw["run_id"] = str(uuid.uuid4())
    raw["snapshot_hash"] = canonical_json_sha256(raw)
    run_snapshot = DirectAgentExecutionSnapshot.model_validate(raw)
    ctx = request_context()
    model = FakeModel(
        [
            RuntimeCommand(kind="request_input", content="Question?"),
            RuntimeCommand(kind="final", content="done"),
        ]
    )
    saver = InMemorySaver(serde=strict_serializer())
    graph = compile_runtime_graph(saver)
    runtime = build_context(
        snapshot=run_snapshot,
        request_context=ctx,
        model=model,
        artifact_reader=RevisionArtifactReader(),
    )
    manager = RuntimeRunManager(checkpointer=saver, model=model)
    manager.graph = graph

    def generation_config(generation: int) -> dict[str, Any]:
        return checkpoint_config(
            tenant_id=ctx.tenant_id,
            user_id=ctx.user_id,
            run_id=run_snapshot.run_id,
            snapshot_hash=run_snapshot.snapshot_hash,
            lease_generation=generation,
        )

    await graph.ainvoke(
        initial_state(run_snapshot, "private"),
        generation_config(1),
        context=runtime,
    )
    g1 = await graph.aget_state(generation_config(1))
    r1 = checkpoint_ref(g1.config, lease_generation=1)
    assert r1 is not None
    g2 = await clone_checkpoint_generation(
        saver, source_ref=r1, target_config=generation_config(2)
    )
    r2 = checkpoint_ref(g2, lease_generation=2)
    assert r2 is not None
    g2["configurable"].pop("checkpoint_id", None)
    source = r2
    if consumed:
        await graph.ainvoke(Command(resume="answer"), g2, context=runtime)
        source = checkpoint_ref(
            (await graph.aget_state(g2)).config, lease_generation=2
        )
        assert source is not None
    g3 = await clone_checkpoint_generation(
        saver, source_ref=source, target_config=generation_config(3)
    )
    g3_root = await graph.aget_state(g3)
    r3 = checkpoint_ref(g3, lease_generation=3)
    assert r3 is not None
    progress = await manager._lineage_progress(
        g3_root,
        r3,
        r1,
        run_id=run_snapshot.run_id,
        snapshot_hash=run_snapshot.snapshot_hash,
        ctx=ctx,
    )
    assert progress is consumed
    g3["configurable"].pop("checkpoint_id", None)
    if consumed:
        assert g3_root.values["status"] == "completed"
    else:
        await graph.ainvoke(Command(resume="answer"), g3, context=runtime)
        assert (await graph.aget_state(g3)).values["status"] == "completed"
    assert len(model.seen) == 2


class BlockingModel:
    def __init__(self):
        self.started = asyncio.Event()

    async def next_command(self, **kwargs):
        self.started.set()
        await asyncio.Event().wait()
        return ModelTurn(RuntimeCommand(kind="final", content="unreachable"))


@pytest.mark.asyncio
async def test_manager_cancel_stops_inflight_work_and_scrubs_checkpoint(
    monkeypatch,
) -> None:
    monkeypatch.setattr(settings, "runtime_cancel_grace_seconds", 0.05)
    run_snapshot = snapshot()
    backend = FakeBackend(run_snapshot)
    model = BlockingModel()
    saver = InMemorySaver(serde=strict_serializer())
    manager = RuntimeRunManager(
        checkpointer=saver,
        backend=backend,
        model=model,
    )
    await manager.start(run_snapshot, "begin", request_context())
    await asyncio.wait_for(model.started.wait(), timeout=1)
    version_before_replay = backend.record.state_version
    replayed = await manager.start(run_snapshot, "begin", request_context())
    assert replayed.status == "running"
    assert backend.record.state_version == version_before_replay
    resume_replay = await manager.resume(
        run_snapshot.run_id, "ignored replay", 0, request_context()
    )
    assert resume_replay.status == "running"
    assert backend.record.state_version == version_before_replay
    backend.public_cancel_running()
    expected = backend.record.state_version
    cancelled = await manager.cancel(
        run_snapshot.run_id,
        request_context(),
        expected_state_version=expected,
    )
    assert cancelled.status == "cancelled"
    assert backend.record.status == "cancelled"
    assert any(
        event["event_type"] == "run_cancelled"
        for event in backend.events.values()
    )
    config = checkpoint_config(
        tenant_id=run_snapshot.caller.tenant_id,
        user_id=run_snapshot.caller.user_id,
        run_id=run_snapshot.run_id,
        snapshot_hash=run_snapshot.snapshot_hash,
    )
    state = await manager.graph.aget_state(config)
    assert state.values["status"] == "cancelled"
    assert state.values.get("active_skill_scope") is None
    await manager.close()


@pytest.mark.asyncio
async def test_recovery_acks_terminal_cancel_after_private_checkpoint_scrub() -> None:
    run_snapshot = snapshot()
    backend = FakeBackend(run_snapshot)
    backend.record = backend.record.model_copy(
        update={"status": "cancelled", "state_version": 4, "cancel_requested": True}
    )
    backend.recovery_items = [
        RecoveryCommand(
            command_id="cancel-1",
            run_id=run_snapshot.run_id,
            command_type="cancel",
            input={"reason": "user"},
            tenant_id=run_snapshot.caller.tenant_id,
            user_id=run_snapshot.caller.user_id,
            role=run_snapshot.caller.role,
            snapshot_hash=run_snapshot.snapshot_hash,
            run_status="cancelled",
            state_version=4,
            checkpoint_version=1,
            claim_token="claim-1",
            claim_expires_at="2099-01-01T00:00:00Z",
            dispatch_attempt=1,
        )
    ]
    manager = RuntimeRunManager(
        checkpointer=InMemorySaver(serde=strict_serializer()),
        backend=backend,
        model=FakeModel([]),
    )
    config = checkpoint_config(
        tenant_id=run_snapshot.caller.tenant_id,
        user_id=run_snapshot.caller.user_id,
        run_id=run_snapshot.run_id,
        snapshot_hash=run_snapshot.snapshot_hash,
    )
    seeded = initial_state(run_snapshot, "private input")
    seeded["active_skill_scope"] = {
        "name": "private",
        "revision": 1,
        "kind": "agentic",
        "definition_sha256": "0" * 64,
        "instruction_sha256": "1" * 64,
        "effective_tools": [],
        "resource_paths": [],
    }
    seeded["pending_input"] = {"question": "private question"}
    await manager.graph.aupdate_state(config, seeded, as_node="preflight")

    assert await manager.recover_once() == 1
    assert await manager.recover_once() == 0
    assert backend.completed_commands == ["cancel-1"]
    assert backend.transition_calls == 0
    state = await manager.graph.aget_state(config)
    assert state.values["status"] == "cancelled"
    assert state.values.get("active_skill_scope") is None
    assert state.values.get("pending_input") is None
    await manager.close()


@pytest.mark.asyncio
@pytest.mark.parametrize("target_terminal", ["failed", "cancelled"])
async def test_deadline_cleanup_obeys_frozen_terminal_target(
    target_terminal: str,
) -> None:
    run_snapshot = snapshot()
    backend = FakeBackend(run_snapshot)
    backend.token = "lease-g2"
    backend.record = backend.record.model_copy(
        update={"status": "running", "state_version": 7, "lease_generation": 2}
    )
    manager = RuntimeRunManager(
        checkpointer=InMemorySaver(serde=strict_serializer()),
        backend=backend,
        model=FakeModel([]),
    )
    config = checkpoint_config(
        tenant_id=run_snapshot.caller.tenant_id,
        user_id=run_snapshot.caller.user_id,
        run_id=run_snapshot.run_id,
        snapshot_hash=run_snapshot.snapshot_hash,
        lease_generation=2,
    )
    config["configurable"]["checkpoint_id"] = (
        "11111111-1111-4111-8111-111111111111"
    )

    class CleanupGraph:
        values = {
            "messages": [{"role": "user", "content": "sensitive"}],
            "final_output": "sensitive",
            "verified_context": {"sensitive": True},
            "events": [{"event_type": "old"}],
        }

        async def aget_state(self, supplied):
            return SimpleNamespace(values=self.values, next=(), config=config)

        async def aupdate_state(self, supplied, update, **kwargs):
            self.values = {**self.values, **update}

    graph = CleanupGraph()
    manager.graph = graph  # type: ignore[assignment]
    command = RecoveryCommand(
        command_id="deadline-1",
        run_id=run_snapshot.run_id,
        command_type="deadline_cleanup",
        tenant_id=run_snapshot.caller.tenant_id,
        user_id=run_snapshot.caller.user_id,
        snapshot_hash=run_snapshot.snapshot_hash,
        run_status="running",
        target_terminal=target_terminal,
        state_version=7,
        lease_generation=2,
        checkpoint_generation=2,
        checkpoint_ref=checkpoint_ref(config),
        checkpoint_version=4,
        event_ack_cursor=3,
        lease_token=backend.token,
        claim_token="claim-deadline",
        claim_expires_at="2099-01-01T00:00:00Z",
        dispatch_attempt=1,
    )

    backend.record = backend.record.model_copy(update={"cancel_requested": True})
    await manager._cleanup_claimed_command(command, request_context())

    assert backend.record.status == target_terminal
    assert graph.values["messages"] == []
    assert graph.values["verified_context"] is None
    assert len(backend.events) == 1
    event = next(iter(backend.events.values()))
    assert event["event_type"] == "deadline_exceeded"
    assert event["payload"]["status"] == target_terminal


@pytest.mark.asyncio
async def test_recovery_preserves_new_interrupt_after_old_resume_was_consumed() -> None:
    run_snapshot = snapshot()
    backend = FakeBackend(run_snapshot)
    recovery_config = checkpoint_config(
        tenant_id=run_snapshot.caller.tenant_id,
        user_id=run_snapshot.caller.user_id,
        run_id=run_snapshot.run_id,
        snapshot_hash=run_snapshot.snapshot_hash,
    )
    thread_id = recovery_config["configurable"]["thread_id"]
    backend.record = backend.record.model_copy(
        update={
            "status": "running",
            "state_version": 6,
            "checkpoint_ref": f"{thread_id}:old-checkpoint",
            "checkpoint_version": 2,
        }
    )
    backend.recovery_items = [
        RecoveryCommand(
            command_id="resume-acked-1",
            run_id=run_snapshot.run_id,
            command_type="resume",
            input={
                "message": "durable answer",
                "expected_checkpoint_version": 1,
                "expected_checkpoint_ref": f"{thread_id}:old-checkpoint",
            },
            tenant_id=run_snapshot.caller.tenant_id,
            user_id=run_snapshot.caller.user_id,
            role=run_snapshot.caller.role,
            snapshot_hash=run_snapshot.snapshot_hash,
            run_status="running",
            state_version=6,
            checkpoint_version=2,
            claim_token="claim-resume-1",
            claim_expires_at="2099-01-01T00:00:00Z",
            dispatch_attempt=2,
        )
    ]
    manager = RuntimeRunManager(
        checkpointer=InMemorySaver(serde=strict_serializer()),
        backend=backend,
        model=FakeModel([]),
    )

    class ConsumedResumeGraph:
        def __init__(self) -> None:
            self.inputs: list[Any] = []

        async def aget_state(self, config):
            checkpoint_id = config["configurable"].get("checkpoint_id")
            if checkpoint_id == "old-checkpoint":
                return SimpleNamespace(
                    values={"status": "waiting_input", "events": []},
                    next=(),
                    interrupts=(object(),),
                    config=config,
                    parent_config=None,
                )
            current = {
                "configurable": {
                    **config["configurable"],
                    "checkpoint_id": "new-checkpoint",
                }
            }
            return SimpleNamespace(
                values={
                    "status": "running",
                    "events": [],
                    "pending_input": {"question": "new question"},
                },
                next=(),
                interrupts=(object(),),
                config=current,
                parent_config={
                    "configurable": {
                        **config["configurable"],
                        "checkpoint_id": "old-checkpoint",
                    }
                },
            )

        async def ainvoke(self, graph_input, config, **kwargs):
            self.inputs.append(graph_input)
            return {"status": "running"}

    graph = ConsumedResumeGraph()
    manager.graph = graph  # type: ignore[assignment]
    assert await manager.recover_once() == 1
    await manager.wait(run_snapshot.run_id)
    assert graph.inputs == [None]
    assert backend.completed_commands == ["resume-acked-1"]
    assert backend.record.status == "waiting_input"
    assert await manager.recover_once() == 0
    await manager.close()


@pytest.mark.asyncio
async def test_recovery_replays_resume_while_durable_interrupt_remains() -> None:
    run_snapshot = snapshot()
    backend = FakeBackend(run_snapshot)
    model = FakeModel(
        [
            RuntimeCommand(kind="request_input", content="Which year?"),
            RuntimeCommand(kind="final", content="2025"),
        ]
    )
    manager = RuntimeRunManager(
        checkpointer=InMemorySaver(serde=strict_serializer()),
        backend=backend,
        model=model,
    )
    await manager.start(run_snapshot, "begin", request_context())
    await wait_status(backend, "waiting_input")
    await manager.wait(run_snapshot.run_id)
    waiting_version = backend.record.checkpoint_version
    waiting = await manager.graph.aget_state(
        checkpoint_config(
            tenant_id=run_snapshot.caller.tenant_id,
            user_id=run_snapshot.caller.user_id,
            run_id=run_snapshot.run_id,
            snapshot_hash=run_snapshot.snapshot_hash,
        )
    )
    waiting_ref = checkpoint_ref(waiting.config)
    assert waiting_ref is not None
    backend.record = backend.record.model_copy(
        update={"status": "running", "state_version": backend.record.state_version + 1}
    )
    backend.recovery_items = [
        RecoveryCommand(
            command_id="resume-unconsumed-1",
            run_id=run_snapshot.run_id,
            command_type="resume",
            input={
                "message": "2025",
                "expected_checkpoint_version": waiting_version,
                "expected_checkpoint_ref": waiting_ref,
            },
            tenant_id=run_snapshot.caller.tenant_id,
            user_id=run_snapshot.caller.user_id,
            role=run_snapshot.caller.role,
            snapshot_hash=run_snapshot.snapshot_hash,
            run_status="running",
            state_version=backend.record.state_version,
            checkpoint_version=waiting_version,
            claim_token="claim-resume-2",
            claim_expires_at="2099-01-01T00:00:00Z",
            dispatch_attempt=2,
        )
    ]
    assert await manager.recover_once() == 1
    await wait_status(backend, "completed")
    assert backend.completed_commands == ["resume-unconsumed-1"]
    assert any(
        message.get("content") == "2025"
        for message in model.seen[-1]["messages"]
    )
    await manager.close()


@pytest.mark.asyncio
async def test_recovery_resume_with_unrelated_checkpoint_pin_fails_closed() -> None:
    run_snapshot = snapshot()
    backend = FakeBackend(run_snapshot)
    model = FakeModel([RuntimeCommand(kind="request_input", content="Question?")])
    manager = RuntimeRunManager(
        checkpointer=InMemorySaver(serde=strict_serializer()),
        backend=backend,
        model=model,
    )
    await manager.start(run_snapshot, "begin", request_context())
    await wait_status(backend, "waiting_input")
    await manager.wait(run_snapshot.run_id)
    backend.record = backend.record.model_copy(
        update={"status": "running", "state_version": backend.record.state_version + 1}
    )
    backend.recovery_items = [
        RecoveryCommand(
            command_id="resume-bad-pin",
            run_id=run_snapshot.run_id,
            command_type="resume",
            input={
                "message": "must not be injected",
                "expected_checkpoint_version": backend.record.checkpoint_version,
                "expected_checkpoint_ref": "unrelated:checkpoint",
            },
            tenant_id=run_snapshot.caller.tenant_id,
            user_id=run_snapshot.caller.user_id,
            role=run_snapshot.caller.role,
            snapshot_hash=run_snapshot.snapshot_hash,
            run_status="running",
            state_version=backend.record.state_version,
            checkpoint_version=backend.record.checkpoint_version,
            claim_token="claim-bad-pin",
            claim_expires_at="2099-01-01T00:00:00Z",
            dispatch_attempt=1,
        )
    ]
    assert await manager.recover_once() == 0
    assert backend.completed_commands == []
    assert backend.record.status == "running"
    assert len(model.seen) == 1
    await manager.close()


@pytest.mark.asyncio
async def test_recovery_rejects_same_thread_sibling_checkpoint() -> None:
    run_snapshot = snapshot()
    backend = FakeBackend(run_snapshot)
    config = checkpoint_config(
        tenant_id=run_snapshot.caller.tenant_id,
        user_id=run_snapshot.caller.user_id,
        run_id=run_snapshot.run_id,
        snapshot_hash=run_snapshot.snapshot_hash,
    )
    thread_id = config["configurable"]["thread_id"]
    sibling_ref = f"{thread_id}:sibling"
    backend.record = backend.record.model_copy(
        update={
            "status": "running",
            "state_version": 6,
            "checkpoint_ref": sibling_ref,
            "checkpoint_version": 2,
        }
    )
    backend.recovery_items = [
        RecoveryCommand(
            command_id="resume-sibling",
            run_id=run_snapshot.run_id,
            command_type="resume",
            input={
                "message": "must not replay",
                "expected_checkpoint_version": 2,
                "expected_checkpoint_ref": sibling_ref,
            },
            tenant_id=run_snapshot.caller.tenant_id,
            user_id=run_snapshot.caller.user_id,
            role=run_snapshot.caller.role,
            snapshot_hash=run_snapshot.snapshot_hash,
            run_status="running",
            state_version=6,
            checkpoint_version=2,
            claim_token="claim-sibling",
            claim_expires_at="2099-01-01T00:00:00Z",
            dispatch_attempt=2,
        )
    ]
    manager = RuntimeRunManager(
        checkpointer=InMemorySaver(serde=strict_serializer()),
        backend=backend,
        model=FakeModel([]),
    )

    class SiblingGraph:
        launched = False

        async def aget_state(self, requested):
            checkpoint_id = requested["configurable"].get("checkpoint_id")
            if checkpoint_id == "root":
                return SimpleNamespace(
                    config=requested,
                    parent_config=None,
                    values={"status": "running"},
                    next=(),
                    interrupts=(),
                )
            return SimpleNamespace(
                config={
                    "configurable": {
                        **requested["configurable"],
                        "checkpoint_id": "current",
                    }
                },
                parent_config={
                    "configurable": {
                        **requested["configurable"],
                        "checkpoint_id": "root",
                    }
                },
                values={"status": "running"},
                next=(),
                interrupts=(),
            )

        async def aget_state_history(self, requested, *, limit):
            # This sibling exists in thread history, but is not on current's
            # root->current parent chain.
            yield SimpleNamespace(
                config={
                    "configurable": {
                        **requested["configurable"],
                        "checkpoint_id": "sibling",
                    }
                }
            )

        async def ainvoke(self, *args, **kwargs):
            self.launched = True
            raise AssertionError("sibling recovery must not launch")

    graph = SiblingGraph()
    manager.graph = graph  # type: ignore[assignment]
    assert await manager.recover_once() == 0
    assert not graph.launched
    assert backend.completed_commands == []
    await manager.close()


@pytest.mark.asyncio
async def test_queued_resume_rejects_actual_checkpoint_sibling() -> None:
    run_snapshot = snapshot()
    backend = FakeBackend(run_snapshot)
    model = FakeModel([RuntimeCommand(kind="request_input", content="Question?")])
    manager = RuntimeRunManager(
        checkpointer=InMemorySaver(serde=strict_serializer()),
        backend=backend,
        model=model,
    )
    await manager.start(run_snapshot, "begin", request_context())
    await wait_status(backend, "waiting_input")
    await manager.wait(run_snapshot.run_id)
    waiting_version = backend.record.checkpoint_version
    backend.public_resume()
    thread_id = checkpoint_config(
        tenant_id=run_snapshot.caller.tenant_id,
        user_id=run_snapshot.caller.user_id,
        run_id=run_snapshot.run_id,
        snapshot_hash=run_snapshot.snapshot_hash,
    )["configurable"]["thread_id"]
    sibling_ref = f"{thread_id}:stale-sibling"
    backend.record = backend.record.model_copy(
        update={"checkpoint_ref": sibling_ref}
    )

    with pytest.raises(RuntimeManagerConflict, match="identity"):
        await manager.resume(
            run_snapshot.run_id,
            "must not inject",
            waiting_version,
            request_context(),
        )
    assert len(model.seen) == 1

    backend.recovery_items = [
        RecoveryCommand(
            command_id="queued-resume-sibling",
            run_id=run_snapshot.run_id,
            command_type="resume",
            input={
                "message": "must not inject",
                "expected_checkpoint_version": waiting_version,
                "expected_checkpoint_ref": sibling_ref,
            },
            tenant_id=run_snapshot.caller.tenant_id,
            user_id=run_snapshot.caller.user_id,
            role=run_snapshot.caller.role,
            snapshot_hash=run_snapshot.snapshot_hash,
            run_status="queued",
            state_version=backend.record.state_version,
            checkpoint_version=waiting_version,
            claim_token="claim-queued-sibling",
            claim_expires_at="2099-01-01T00:00:00Z",
            dispatch_attempt=1,
        )
    ]
    assert await manager.recover_once() == 0
    assert backend.completed_commands == []
    assert len(model.seen) == 1
    await manager.close()


@pytest.mark.asyncio
@pytest.mark.parametrize(
    ("stored_present", "actual_present"),
    [(False, False), (True, False), (False, True)],
)
async def test_fresh_queued_resume_requires_both_checkpoint_refs_nonblank(
    stored_present: bool,
    actual_present: bool,
) -> None:
    run_snapshot = snapshot()
    backend = FakeBackend(run_snapshot)
    config = checkpoint_config(
        tenant_id=run_snapshot.caller.tenant_id,
        user_id=run_snapshot.caller.user_id,
        run_id=run_snapshot.run_id,
        snapshot_hash=run_snapshot.snapshot_hash,
    )
    thread_id = config["configurable"]["thread_id"]
    valid_ref = f"{thread_id}:waiting"
    backend.record = backend.record.model_copy(
        update={
            "status": "queued",
            "checkpoint_ref": valid_ref if stored_present else None,
            "checkpoint_version": 1,
        }
    )
    manager = RuntimeRunManager(
        checkpointer=InMemorySaver(serde=strict_serializer()),
        backend=backend,
        model=FakeModel([]),
    )

    class WaitingGraph:
        async def aget_state(self, requested):
            actual = {
                "configurable": {
                    **requested["configurable"],
                    **({"checkpoint_id": "waiting"} if actual_present else {}),
                }
            }
            return SimpleNamespace(
                values={"status": "waiting_input"},
                next=(),
                interrupts=(object(),),
                config=actual,
            )

        async def ainvoke(self, *args, **kwargs):
            raise AssertionError("invalid checkpoint refs must not launch")

    manager.graph = WaitingGraph()  # type: ignore[assignment]
    with pytest.raises(RuntimeManagerConflict, match="identity"):
        await manager.resume(
            run_snapshot.run_id, "answer", 1, request_context()
        )
    assert backend.transition_calls == 0
    await manager.close()


@pytest.mark.asyncio
async def test_recovery_resume_missing_checkpoint_pin_is_not_acked() -> None:
    run_snapshot = snapshot()
    backend = FakeBackend(run_snapshot)
    backend.record = backend.record.model_copy(
        update={"status": "queued", "checkpoint_ref": None, "checkpoint_version": 1}
    )
    backend.recovery_items = [
        RecoveryCommand(
            command_id="resume-missing-pin",
            run_id=run_snapshot.run_id,
            command_type="resume",
            input={
                "message": "answer",
                "expected_checkpoint_version": 1,
            },
            tenant_id=run_snapshot.caller.tenant_id,
            user_id=run_snapshot.caller.user_id,
            role=run_snapshot.caller.role,
            snapshot_hash=run_snapshot.snapshot_hash,
            run_status="queued",
            state_version=backend.record.state_version,
            checkpoint_version=1,
            claim_token="claim-missing-pin",
            claim_expires_at="2099-01-01T00:00:00Z",
            dispatch_attempt=1,
        )
    ]
    manager = RuntimeRunManager(
        checkpointer=InMemorySaver(serde=strict_serializer()),
        backend=backend,
        model=FakeModel([]),
    )
    assert await manager.recover_once() == 0
    assert backend.completed_commands == []
    assert backend.transition_calls == 0
    await manager.close()


def recovery_cancel_command(run_snapshot, command_id: str) -> RecoveryCommand:
    return RecoveryCommand(
        command_id=command_id,
        run_id=run_snapshot.run_id,
        command_type="cancel",
        input={"reason": "test"},
        tenant_id=run_snapshot.caller.tenant_id,
        user_id=run_snapshot.caller.user_id,
        role=run_snapshot.caller.role,
        snapshot_hash=run_snapshot.snapshot_hash,
        run_status="cancelled",
        state_version=4,
        checkpoint_version=1,
        claim_token=f"claim-{command_id}",
        claim_expires_at="2099-01-01T00:00:00Z",
        dispatch_attempt=1,
    )


@pytest.mark.asyncio
async def test_recovery_isolates_transient_command_and_retries_later() -> None:
    run_snapshot = snapshot()
    backend = FakeBackend(run_snapshot)
    first = recovery_cancel_command(run_snapshot, "checkpoint-transient")
    second = recovery_cancel_command(run_snapshot, "valid-second")
    backend.recovery_items = [first, second]
    manager = RuntimeRunManager(
        checkpointer=InMemorySaver(serde=strict_serializer()),
        backend=backend,
        model=FakeModel([]),
    )
    attempts: list[str] = []

    async def dispatch(self, command, ctx):
        attempts.append(command.command_id)
        if command.command_id == first.command_id and attempts.count(first.command_id) == 1:
            raise OSError("transient checkpoint history failure")

    manager._dispatch_recovery = MethodType(dispatch, manager)  # type: ignore[method-assign]
    assert await manager.recover_once() == 1
    assert backend.completed_commands == [second.command_id]

    backend.recovery_items = [first]
    assert await manager.recover_once() == 1
    assert backend.completed_commands == [second.command_id, first.command_id]
    assert attempts == [first.command_id, second.command_id, first.command_id]
    await manager.close()


@pytest.mark.asyncio
async def test_recovery_loop_survives_scan_error_and_propagates_cancellation(
    monkeypatch,
) -> None:
    run_snapshot = snapshot()
    manager = RuntimeRunManager(
        checkpointer=InMemorySaver(serde=strict_serializer()),
        backend=FakeBackend(run_snapshot),
        model=FakeModel([]),
    )
    monkeypatch.setattr(settings, "runtime_recovery_interval_seconds", 0.01)
    recovered = asyncio.Event()
    calls = 0

    async def flaky_scan(self):
        nonlocal calls
        calls += 1
        if calls == 1:
            raise OSError("transient checkpointer failure")
        recovered.set()
        return 0

    manager.recover_once = MethodType(flaky_scan, manager)  # type: ignore[method-assign]
    task = asyncio.create_task(manager.recovery_loop())
    await asyncio.wait_for(recovered.wait(), timeout=1)
    assert not task.done()
    task.cancel()
    with pytest.raises(asyncio.CancelledError):
        await task

    async def cancelled_dispatch(self, command, ctx):
        raise asyncio.CancelledError

    manager._dispatch_recovery = MethodType(  # type: ignore[method-assign]
        cancelled_dispatch, manager
    )
    manager.backend.recovery_items = [
        recovery_cancel_command(run_snapshot, "cancelled-command")
    ]
    with pytest.raises(asyncio.CancelledError):
        await RuntimeRunManager.recover_once(manager)
    await manager.close()


@pytest.mark.asyncio
async def test_event_flush_chunks_and_safely_retries_partial_failure() -> None:
    run_snapshot = snapshot()
    backend = FakeBackend(run_snapshot)
    backend.fail_event_batch_call = 2
    events = [
        {
            "event_id": str(uuid.uuid5(uuid.NAMESPACE_URL, f"event-{index}")),
            "event_type": "tool_observation",
            "node_id": "tool",
            "snapshot_hash": run_snapshot.snapshot_hash,
            "payload": {"sequence": index},
        }
        for index in range(205)
    ]
    manager = RuntimeRunManager(
        checkpointer=InMemorySaver(serde=strict_serializer()),
        backend=backend,
        model=FakeModel([]),
    )

    class TerminalGraph:
        def __init__(self) -> None:
            self.completed = False

        async def aget_state(self, config):
            return SimpleNamespace(
                values=(
                    {
                        "status": "completed",
                        "final_output": "done",
                        "events": events,
                    }
                    if self.completed
                    else {}
                ),
                next=(),
                interrupts=(),
                config=config,
            )

        async def ainvoke(self, graph_input, config, **kwargs):
            self.completed = True
            return {"status": "completed", "final_output": "done"}

    manager.graph = TerminalGraph()  # type: ignore[assignment]
    await manager.start(run_snapshot, "begin", request_context())
    await manager.wait(run_snapshot.run_id)
    assert backend.record.status == "running"
    assert backend.event_batch_sizes == [100, 100]
    assert len(backend.events) == 100

    await manager.start(run_snapshot, "begin", request_context())
    await wait_status(backend, "completed")
    assert backend.event_batch_sizes == [100, 100, 100, 100, 5]
    assert all(size <= 100 for size in backend.event_batch_sizes)
    assert len(backend.events) == 205
    assert [
        backend.events[str(uuid.uuid5(uuid.NAMESPACE_URL, f"event-{index}"))][
            "payload"
        ]["sequence"]
        for index in range(205)
    ] == list(range(205))
    await manager.close()


def test_backend_result_wire_size_exact_boundary() -> None:
    overhead = backend_result_wire_size({"output": ""})
    at_limit = {"output": "a" * (MAX_BACKEND_RESULT_JSON_BYTES - overhead)}
    over_limit = {"output": at_limit["output"] + "a"}
    assert backend_result_wire_size(at_limit) == MAX_BACKEND_RESULT_JSON_BYTES
    assert backend_result_wire_size(over_limit) == MAX_BACKEND_RESULT_JSON_BYTES + 1


@pytest.mark.asyncio
@pytest.mark.parametrize(
    ("output", "expected_status"),
    [
        ("\x00" * 200_000, "failed"),
        ("\U0001f600" * 200_000, "completed"),
    ],
    ids=["escaped-control-over-limit", "unicode-within-limit"],
)
async def test_terminal_result_preflight_matches_utf8_wire_limit(
    output: str,
    expected_status: str,
) -> None:
    run_snapshot = snapshot()
    backend = FakeBackend(run_snapshot)
    manager = RuntimeRunManager(
        checkpointer=InMemorySaver(serde=strict_serializer()),
        backend=backend,
        model=FakeModel([]),
    )

    class ResultGraph:
        completed = False

        async def aget_state(self, config):
            return SimpleNamespace(
                values=(
                    {
                        "status": "completed",
                        "final_output": output,
                        "events": [],
                    }
                    if self.completed
                    else {}
                ),
                next=(),
                interrupts=(),
                config=config,
            )

        async def ainvoke(self, graph_input, config, **kwargs):
            self.completed = True
            return {"status": "completed", "final_output": output}

    manager.graph = ResultGraph()  # type: ignore[assignment]
    await manager.start(run_snapshot, "begin", request_context())
    await wait_status(backend, expected_status)
    terminal = backend.transition_payloads[-1]
    if expected_status == "failed":
        assert terminal["result"] is None
        assert terminal["error_code"] == "runtime_result_too_large"
        assert len(terminal["error_message"]) < 256
        assert not any(
            call["to_status"] == "completed"
            for call in backend.transition_payloads
        )
    else:
        assert terminal["result"] == {"output": output}
        assert (
            backend_result_wire_size(terminal["result"])
            <= MAX_BACKEND_RESULT_JSON_BYTES
        )
    await manager.close()


@pytest.mark.asyncio
async def test_max_rule_decision_event_is_aggregate_capped_and_flushes() -> None:
    rules = []
    for index in range(64):
        prefix = f"rule-{index:02}-"
        rule_id = prefix + ("x" * (1024 - len(prefix)))
        rules.append(
            {
                "id": rule_id,
                "name": rule_id,
                "enabled": True,
                "priority": index,
                "when": {
                    "fact": "caller.role",
                    "op": "eq",
                    "value": "ADMIN",
                },
                "then": [{"action": "deny", "reason": "policy"}],
            }
        )
    policy = PreActionPolicy(
        {"version": 1, "rules": rules},
        pinned_skills=[],
        registered_tools=[],
        roles=["ADMIN"],
    )
    decision = policy.decide(
        ProposedAction(action_type="response"),
        caller_envelopes(
            tenant_id="tenant-a", role="ADMIN", groups=[]
        ),
    )
    assert decision.summary is not None
    assert len(decision.summary["matched_rule_ids"]) == 64
    run_snapshot = snapshot()
    event = runtime_event(
        run_id=run_snapshot.run_id,
        snapshot_hash=run_snapshot.snapshot_hash,
        event_type="rule_decision",
        node_id="policy_gate",
        event_key="max-rules",
        payload=decision.summary,
    )
    encoded = json.dumps(
        event.payload,
        ensure_ascii=False,
        separators=(",", ":"),
        allow_nan=False,
    ).encode("utf-8")
    assert len(encoded) <= MAX_EVENT_PAYLOAD_JSON_BYTES
    assert event.payload["truncated"] is True
    assert event.payload["matched_rule_ids_total"] == 64
    assert 0 < len(event.payload["matched_rule_ids"]) < 64
    assert event.payload["matched_rule_ids"] == [
        item[:500]
        for item in decision.summary["matched_rule_ids"][
            : len(event.payload["matched_rule_ids"])
        ]
    ]

    backend = FakeBackend(run_snapshot)
    manager = RuntimeRunManager(
        checkpointer=InMemorySaver(serde=strict_serializer()),
        backend=backend,
        model=FakeModel([]),
    )

    class RuleTerminalGraph:
        completed = False

        async def aget_state(self, config):
            return SimpleNamespace(
                values=(
                    {
                        "status": "completed",
                        "final_output": "denied",
                        "events": [event.as_backend_dict()],
                    }
                    if self.completed
                    else {}
                ),
                next=(),
                interrupts=(),
                config=config,
            )

        async def ainvoke(self, graph_input, config, **kwargs):
            self.completed = True
            return {"status": "completed"}

    manager.graph = RuleTerminalGraph()  # type: ignore[assignment]
    await manager.start(run_snapshot, "begin", request_context())
    await wait_status(backend, "completed")
    assert len(backend.events) == 1
    await manager.close()


@pytest.mark.asyncio
async def test_model_failure_durably_scrubs_scope_and_emits_one_terminal() -> None:
    run_snapshot = snapshot(with_skill=True)
    backend = FakeBackend(run_snapshot)
    manager = RuntimeRunManager(
        checkpointer=InMemorySaver(serde=strict_serializer()),
        backend=backend,
        model=FakeModel(
            [RuntimeCommand(kind="load_skill", name="research-skill")]
        ),
        artifact_reader=FakeArtifactReader(["local.calculator"]),
    )
    await manager.start(run_snapshot, "begin", request_context())
    await wait_status(backend, "failed")
    config = checkpoint_config(
        tenant_id=run_snapshot.caller.tenant_id,
        user_id=run_snapshot.caller.user_id,
        run_id=run_snapshot.run_id,
        snapshot_hash=run_snapshot.snapshot_hash,
    )
    state = await manager.graph.aget_state(config)
    assert state.values["status"] == "failed"
    assert state.values["error_code"] == "runtime_execution_failed"
    assert state.values.get("active_skill_scope") is None
    assert state.values.get("pending_command") is None
    assert state.values.get("pending_input") is None
    terminals = [
        item
        for item in state.values["events"]
        if item["event_type"] == "run_terminal"
    ]
    assert len(terminals) == 1
    assert terminals[0]["payload"]["status"] == "failed"
    await manager.close()


@pytest.mark.asyncio
async def test_oversized_result_is_graph_backend_event_consistent_failure() -> None:
    run_snapshot = snapshot()
    backend = FakeBackend(run_snapshot)
    manager = RuntimeRunManager(
        checkpointer=InMemorySaver(serde=strict_serializer()),
        backend=backend,
        model=FakeModel(
            [RuntimeCommand(kind="final", content="\x00" * 200_000)]
        ),
    )
    await manager.start(run_snapshot, "begin", request_context())
    await wait_status(backend, "failed")
    config = checkpoint_config(
        tenant_id=run_snapshot.caller.tenant_id,
        user_id=run_snapshot.caller.user_id,
        run_id=run_snapshot.run_id,
        snapshot_hash=run_snapshot.snapshot_hash,
    )
    state = await manager.graph.aget_state(config)
    assert state.values["status"] == "failed"
    assert state.values["error_code"] == "runtime_result_too_large"
    terminals = [
        item
        for item in state.values["events"]
        if item["event_type"] == "run_terminal"
    ]
    assert len(terminals) == 1
    assert terminals[0]["payload"] == {
        "status": "failed",
        "error_code": "runtime_result_too_large",
    }
    assert not any(
        item["event_type"] == "output_validated"
        for item in state.values["events"]
    )
    assert backend.transition_payloads[-1]["to_status"] == "failed"
    assert (
        backend.transition_payloads[-1]["error_code"]
        == "runtime_result_too_large"
    )
    await manager.close()


@pytest.mark.asyncio
async def test_backend_execution_snapshot_preserves_numeric_lexemes(
    monkeypatch,
) -> None:
    raw = snapshot().model_dump(mode="python")
    raw.pop("snapshot_hash")
    raw["agent"]["business_rules"]["numeric_vectors"] = parse_json_preserving_numbers(
        '{"scaled":1.230,"tiny":0.00000100,"exponent":1e+3}'
    )
    raw["snapshot_hash"] = canonical_json_sha256(raw)
    canonical_payload = {
        key: value for key, value in raw.items() if key != "snapshot_hash"
    }
    canonical_bytes = canonical_json_bytes(canonical_payload)
    response_bytes = json.dumps(
        {
            "snapshot_hash": hashlib.sha256(canonical_bytes).hexdigest(),
            "snapshot_canonical_base64": base64.b64encode(
                canonical_bytes
            ).decode("ascii"),
        },
        separators=(",", ":"),
    ).encode()

    class Response:
        status_code = 200
        content = response_bytes

        def raise_for_status(self):
            return None

        def json(self):
            raise AssertionError("execution artifact must not use response.json()")

    class Client:
        async def request(self, *args, **kwargs):
            return Response()

    monkeypatch.setattr("app.runtime.backend.get_client", lambda: Client())
    restored = await BackendRunClient().execution_snapshot(
        raw["run_id"], request_context()
    )
    restored.assert_hash()
    assert canonical_json_bytes(
        restored.canonical_payload()
    ) == canonical_json_bytes(
        {key: value for key, value in raw.items() if key != "snapshot_hash"}
    )


@pytest.mark.asyncio
async def test_timeout_uses_durable_failed_terminal_cleanup() -> None:
    raw = snapshot().model_dump(mode="python")
    raw.pop("snapshot_hash")
    raw["agent"]["runtime_limits"]["timeout_seconds"] = 1
    raw["snapshot_hash"] = canonical_json_sha256(raw)
    run_snapshot = DirectAgentExecutionSnapshot.model_validate(raw)
    backend = FakeBackend(run_snapshot)
    manager = RuntimeRunManager(
        checkpointer=InMemorySaver(serde=strict_serializer()),
        backend=backend,
        model=BlockingModel(),
    )
    await manager.start(run_snapshot, "begin", request_context())
    await wait_status(backend, "failed")
    config = checkpoint_config(
        tenant_id=run_snapshot.caller.tenant_id,
        user_id=run_snapshot.caller.user_id,
        run_id=run_snapshot.run_id,
        snapshot_hash=run_snapshot.snapshot_hash,
    )
    state = await manager.graph.aget_state(config)
    assert state.values["status"] == "failed"
    assert state.values["error_code"] == "runtime_timeout"
    assert state.values.get("active_skill_scope") is None
    assert state.values.get("pending_command") is None
    assert state.values.get("pending_input") is None
    assert sum(
        item["event_type"] == "run_terminal"
        for item in state.values["events"]
    ) == 1
    await manager.close()
