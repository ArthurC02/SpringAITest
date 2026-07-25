from __future__ import annotations

import asyncio
import logging
from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Any, Literal

from langgraph.errors import GraphDrained
from langgraph.runtime import RunControl
from langgraph.types import Command

from app.runtime.artifacts import RevisionArtifactReader
from app.runtime.backend import (
    BackendRunClient,
    BackendRunConflict,
    BackendRunError,
    LeaseRecord,
    RecoveryCommand,
    RunRecord,
)
from app.runtime.checkpoints import (
    checkpoint_config,
    checkpoint_ref,
    clone_checkpoint_generation,
    config_from_checkpoint_ref,
)
from app.runtime.events import RuntimeEvent, runtime_event
from app.runtime.graph import (
    RuntimeGraphContext,
    RuntimePreflightError,
    build_context,
    compile_runtime_graph,
    initial_state,
)
from app.runtime.model import LangChainRuntimeModel, RuntimeModel
from app.runtime.models import (
    DirectAgentExecutionSnapshot,
    RuntimeRunResult,
    SnapshotCanonicalEnvelope,
)
from app.runtime.models import (
    MAX_BACKEND_RESULT_JSON_BYTES,
    backend_result_wire_size,
)
from app.security import RequestContext
from app.settings import settings


class RuntimeManagerError(RuntimeError):
    pass


class RuntimeManagerConflict(RuntimeManagerError):
    pass


class RuntimeLineageInvalid(RuntimeManagerError):
    pass


logger = logging.getLogger(__name__)


def _authoritative_ref_config(
    value: str,
    *,
    generation: int,
    run_id: str,
    snapshot_hash: str,
    ctx: RequestContext,
) -> dict[str, Any]:
    expected = checkpoint_config(
        tenant_id=ctx.tenant_id,
        user_id=ctx.user_id,
        run_id=run_id,
        snapshot_hash=snapshot_hash,
        lease_generation=generation,
    )
    return config_from_checkpoint_ref(
        value,
        expected_thread_id=expected["configurable"]["thread_id"],
        expected_generation=generation,
    )


@dataclass
class _RunHandle:
    run_id: str
    ctx: RequestContext
    snapshot: DirectAgentExecutionSnapshot
    graph_context: RuntimeGraphContext
    config: dict[str, Any]
    lease_token: str
    state_version: int
    checkpoint_version: int
    control: RunControl
    operation: Literal["start", "resume", "recovery"]
    lease_generation: int = 0
    event_ack_cursor: int = 0
    deadline_at: str = "2099-01-01T00:00:00Z"
    task: asyncio.Task[None] | None = None
    cancel_requested: bool = False
    lease_lost: bool = False
    shutdown_requested: bool = False
    state_lock: asyncio.Lock = None  # type: ignore[assignment]

    def __post_init__(self) -> None:
        if self.state_lock is None:
            self.state_lock = asyncio.Lock()


class RuntimeRunManager:
    """Supervisor for durable direct-Agent executions.

    LangGraph owns checkpoint state. Backend owns run status/lineage through
    lease-protected CAS. This class is the only bridge between those sources.
    """

    def __init__(
        self,
        *,
        checkpointer: Any,
        backend: BackendRunClient | None = None,
        model: RuntimeModel | None = None,
        artifact_reader: RevisionArtifactReader | None = None,
        deps: Any = None,
    ):
        self.graph = compile_runtime_graph(checkpointer)
        self.checkpointer = checkpointer
        self.backend = backend or BackendRunClient()
        self.model = model or LangChainRuntimeModel()
        self.artifact_reader = artifact_reader or RevisionArtifactReader()
        self.deps = deps
        self._handles: dict[str, _RunHandle] = {}
        self._run_locks: dict[str, asyncio.Lock] = {}
        self._lock = asyncio.Lock()
        self._closed = False

    async def dispatch_command(
        self, run_id: str, command_id: str, ctx: RequestContext
    ) -> RuntimeRunResult:
        async with await self._run_lock(run_id):
            command = await self.backend.claim_command(run_id, command_id, ctx)
            if command is None:
                record = await self.backend.get_run(run_id, ctx)
                return RuntimeRunResult(
                    run_id=run_id,
                    status=record.status,
                    snapshot_hash=record.snapshot_hash,
                    checkpoint_version=record.checkpoint_version,
                )
            try:
                async with self._lock:
                    prior = self._handles.get(run_id)
                    if (
                        prior is not None
                        and prior.lease_generation != command.lease_generation
                    ):
                        prior.lease_lost = True
                        prior.control.request_drain("lease_generation_changed")
                        self._handles.pop(run_id, None)
                if command.command_type == "start":
                    envelope = SnapshotCanonicalEnvelope.model_validate(command.snapshot)
                    snapshot = envelope.decode()
                    if (
                        snapshot.run_id != run_id
                        or snapshot.snapshot_hash != command.snapshot_hash
                    ):
                        raise RuntimeManagerError("claimed snapshot identity is invalid")
                    result = await self._start_locked(
                        snapshot,
                        str(command.input.get("message") or ""),
                        ctx,
                        operation="start",
                        claimed_command=command,
                    )
                elif command.command_type == "resume":
                    await self._recover_resume_locked(command, ctx)
                    record = await self.backend.get_run(run_id, ctx)
                    result = RuntimeRunResult(
                        run_id=run_id,
                        status=record.status,
                        snapshot_hash=record.snapshot_hash,
                        checkpoint_version=record.checkpoint_version,
                    )
                else:
                    await self._cleanup_claimed_command(command, ctx)
                    result = RuntimeRunResult(
                        run_id=run_id,
                        status=command.target_terminal or "cancelled",
                        snapshot_hash=command.snapshot_hash,
                        checkpoint_version=command.checkpoint_version + 1,
                    )
                await self.backend.complete_recovery_dispatch(command, ctx)
                return result
            except RuntimeLineageInvalid:
                await self._fail_owned_preflight(
                    command,
                    ctx,
                    error_code="resume_lineage_invalid",
                    error_message="Resume checkpoint lineage was rejected.",
                )
                await self.backend.complete_recovery_dispatch(command, ctx)
                return RuntimeRunResult(
                    run_id=run_id,
                    status="failed",
                    snapshot_hash=command.snapshot_hash,
                    checkpoint_version=command.checkpoint_version + 1,
                    message="Resume checkpoint lineage was rejected.",
                )
            except (ValueError, RuntimePreflightError):
                await self._fail_owned_preflight(command, ctx)
                await self.backend.complete_recovery_dispatch(command, ctx)
                return RuntimeRunResult(
                    run_id=run_id,
                    status="failed",
                    snapshot_hash=command.snapshot_hash,
                    checkpoint_version=command.checkpoint_version,
                    message="Execution preflight was rejected.",
                )

    async def start_command(
        self, run_id: str, command_id: str, ctx: RequestContext
    ) -> RuntimeRunResult:
        return await self.dispatch_command(run_id, command_id, ctx)

    async def _fail_owned_preflight(
        self,
        command: RecoveryCommand,
        ctx: RequestContext,
        *,
        error_code: str = "runtime_preflight_invalid",
        error_message: str = "Execution preflight was rejected.",
    ) -> None:
        target_config = checkpoint_config(
            tenant_id=ctx.tenant_id,
            user_id=ctx.user_id,
            run_id=command.run_id,
            snapshot_hash=command.snapshot_hash,
            lease_generation=command.lease_generation,
        )
        if command.checkpoint_ref:
            try:
                _authoritative_ref_config(
                    command.checkpoint_ref,
                    generation=command.checkpoint_generation,
                    run_id=command.run_id,
                    snapshot_hash=command.snapshot_hash,
                    ctx=ctx,
                )
                config = (
                    _authoritative_ref_config(
                        command.checkpoint_ref,
                        generation=command.checkpoint_generation,
                        run_id=command.run_id,
                        snapshot_hash=command.snapshot_hash,
                        ctx=ctx,
                    )
                    if command.checkpoint_generation == command.lease_generation
                    else await clone_checkpoint_generation(
                        self.checkpointer,
                        source_ref=command.checkpoint_ref,
                        target_config=target_config,
                    )
                )
            except ValueError:
                config = target_config
        else:
            config = target_config
        prior_state = await self.graph.aget_state(config)
        prior_values = dict(prior_state.values or {})
        prior_terminal = next(
            (
                item
                for item in (prior_values.get("events") or [])
                if isinstance(item, dict)
                and item.get("event_type") == "run_terminal"
                and (item.get("payload") or {}).get("error_code") == error_code
            ),
            None,
        )
        event_cursor_base = int(
            (
                prior_values.get("event_cursor_base")
                if prior_terminal is not None
                else command.event_ack_cursor
            )
            or command.event_ack_cursor
        )
        await self.graph.aupdate_state(
            config,
            {
                "run_id": command.run_id,
                "snapshot_hash": command.snapshot_hash,
                "messages": [],
                "final_output": None,
                "verified_context": None,
                "active_skill_scope": None,
                "pending_command": None,
                "pending_input": None,
                "rule_allowed_tools": None,
                "status": "failed",
                "error_code": error_code,
                "events": [prior_terminal] if prior_terminal is not None else [],
                "event_cursor_base": event_cursor_base,
            },
            as_node="finalize",
        )
        failed_state = await self.graph.aget_state(config)
        state_version = command.state_version
        checkpoint_version = command.checkpoint_version
        event_cursor = command.event_ack_cursor
        if command.run_status == "queued":
            promoted = await self.backend.transition(
                command.run_id,
                ctx,
                expected_version=state_version,
                lease_token=command.lease_token,
                lease_generation=command.lease_generation,
                expected_event_ack_cursor=event_cursor,
                to_status="running",
                checkpoint_ref=checkpoint_ref(
                    failed_state.config,
                    lease_generation=command.lease_generation,
                ),
                checkpoint_version=checkpoint_version + 1,
            )
            state_version = promoted.state_version
            checkpoint_version = promoted.checkpoint_version
        event = runtime_event(
            run_id=command.run_id,
            snapshot_hash=command.snapshot_hash,
            event_type="run_terminal",
            node_id="supervisor",
            event_key=f"{error_code}:g{command.lease_generation}",
            payload={
                "status": "failed",
                "error_code": error_code,
            },
        )
        if prior_terminal is None:
            await self.graph.aupdate_state(
                config,
                {
                    "events": [event.as_backend_dict()],
                    "event_cursor_base": event_cursor_base,
                },
                as_node="finalize",
            )
        if event_cursor == event_cursor_base:
            record = await self.backend.append_events(
                command.run_id,
                ctx,
                [event],
                expected_version=state_version,
                lease_token=command.lease_token,
                lease_generation=command.lease_generation,
                event_cursor_start=event_cursor,
            )
        elif event_cursor == event_cursor_base + 1:
            record = await self.backend.get_run(command.run_id, ctx)
        else:
            raise RuntimeLineageInvalid("terminal audit cursor is inconsistent")
        terminal_state = await self.graph.aget_state(config)
        await self.backend.transition(
            command.run_id,
            ctx,
            expected_version=record.state_version,
            lease_token=command.lease_token,
            lease_generation=command.lease_generation,
            expected_event_ack_cursor=record.event_ack_cursor,
            to_status="failed",
            checkpoint_ref=checkpoint_ref(
                terminal_state.config,
                lease_generation=command.lease_generation,
            ),
            checkpoint_version=checkpoint_version + 1,
            error_code=error_code,
            error_message=error_message,
        )

    async def start(
        self,
        snapshot: DirectAgentExecutionSnapshot,
        message: str,
        ctx: RequestContext,
    ) -> RuntimeRunResult:
        async with await self._run_lock(snapshot.run_id):
            return await self._start_locked(snapshot, message, ctx)

    async def _start_locked(
        self,
        snapshot: DirectAgentExecutionSnapshot,
        message: str,
        ctx: RequestContext,
        *,
        operation: Literal["start", "recovery"] = "start",
        claimed_command: RecoveryCommand | None = None,
    ) -> RuntimeRunResult:
        if snapshot.run_id == "" or not message.strip():
            raise RuntimeManagerError("run and message are required")
        existing = (
            None
            if claimed_command is not None
            else await self._existing_result(
                snapshot.run_id, snapshot.snapshot_hash, ctx
            )
        )
        if existing is not None:
            return existing
        graph_context = build_context(
            snapshot=snapshot,
            request_context=ctx,
            model=self.model,
            artifact_reader=self.artifact_reader,
            deps=self.deps,
        )
        record = await self.backend.get_run(snapshot.run_id, ctx)
        if (
            record.snapshot_hash != snapshot.snapshot_hash
            or record.status not in {"queued", "running"}
        ):
            raise RuntimeManagerConflict("Backend run does not match the start snapshot")
        config = checkpoint_config(
            tenant_id=ctx.tenant_id,
            user_id=ctx.user_id,
            run_id=snapshot.run_id,
            snapshot_hash=snapshot.snapshot_hash,
            lease_generation=(
                claimed_command.lease_generation if claimed_command is not None else None
            ),
        )
        if claimed_command is None:
            checkpoint = await self.graph.aget_state(config)
            recovering = bool(checkpoint.values or checkpoint.next)
        else:
            # The claim DTO is the sole recovery authority. _launch will use
            # its pinned checkpoint ref or clone it into the claimed generation.
            recovering = claimed_command.checkpoint_ref is not None
        return await self._launch(
            record=record,
            snapshot=snapshot,
            graph_context=graph_context,
            ctx=ctx,
            config=config,
            graph_input=None if recovering else initial_state(snapshot, message.strip()),
            operation=operation,
            claimed_command=claimed_command,
        )

    async def resume(
        self,
        run_id: str,
        message: str,
        expected_checkpoint_version: int,
        ctx: RequestContext,
    ) -> RuntimeRunResult:
        async with await self._run_lock(run_id):
            return await self._resume_locked(
                run_id, message, expected_checkpoint_version, ctx
            )

    async def _resume_locked(
        self,
        run_id: str,
        message: str,
        expected_checkpoint_version: int,
        ctx: RequestContext,
        *,
        operation: Literal["resume", "recovery"] = "resume",
    ) -> RuntimeRunResult:
        if not message.strip():
            raise RuntimeManagerError("resume message is required")
        record = await self.backend.get_run(run_id, ctx)
        stale_task: asyncio.Task[None] | None = None
        async with self._lock:
            handle = self._handles.get(run_id)
            if (
                record.status == "queued"
                and handle is not None
                and handle.operation != "resume"
                and handle.task is not None
            ):
                stale_task = handle.task
        if stale_task is not None:
            await asyncio.shield(stale_task)
            async with self._lock:
                if self._handles.get(run_id) is handle and stale_task.done():
                    self._handles.pop(run_id, None)
            record = await self.backend.get_run(run_id, ctx)
        existing = await self._existing_result(run_id, None, ctx)
        if existing is not None:
            return existing
        if (
            record.status != "queued"
            or record.checkpoint_version != expected_checkpoint_version
        ):
            raise RuntimeManagerConflict("run is not resumable at that checkpoint")
        snapshot = await self.backend.execution_snapshot(run_id, ctx)
        graph_context = build_context(
            snapshot=snapshot,
            request_context=ctx,
            model=self.model,
            artifact_reader=self.artifact_reader,
            deps=self.deps,
        )
        config = checkpoint_config(
            tenant_id=ctx.tenant_id,
            user_id=ctx.user_id,
            run_id=run_id,
            snapshot_hash=snapshot.snapshot_hash,
        )
        checkpoint = await self.graph.aget_state(config)
        if not checkpoint.interrupts:
            raise RuntimeManagerConflict("run has no durable input interrupt")
        actual_checkpoint_ref = checkpoint_ref(checkpoint.config)
        stored_checkpoint_ref = (record.checkpoint_ref or "").strip()
        if (
            not actual_checkpoint_ref
            or not stored_checkpoint_ref
            or actual_checkpoint_ref != stored_checkpoint_ref
        ):
            raise RuntimeManagerConflict(
                "queued resume checkpoint identity changed"
            )
        return await self._launch(
            record=record,
            snapshot=snapshot,
            graph_context=graph_context,
            ctx=ctx,
            config=config,
            graph_input=Command(resume=message.strip()),
            operation=operation,
        )

    async def cancel(
        self,
        run_id: str,
        ctx: RequestContext,
        *,
        expected_state_version: int | None,
    ) -> RuntimeRunResult:
        async with await self._run_lock(run_id):
            return await self._cancel_locked(
                run_id, ctx, expected_state_version=expected_state_version
            )

    async def _cancel_locked(
        self,
        run_id: str,
        ctx: RequestContext,
        *,
        expected_state_version: int | None,
    ) -> RuntimeRunResult:
        record = await self.backend.get_run(run_id, ctx)
        if (
            expected_state_version is not None
            and record.state_version != expected_state_version
        ):
            raise RuntimeManagerConflict("run state version changed before cancellation")
        snapshot = await self.backend.execution_snapshot(run_id, ctx)
        config = checkpoint_config(
            tenant_id=ctx.tenant_id,
            user_id=ctx.user_id,
            run_id=run_id,
            snapshot_hash=snapshot.snapshot_hash,
        )
        async with self._lock:
            handle = self._handles.get(run_id)
            if handle is not None:
                handle.cancel_requested = True
                handle.control.request_drain("cancelled")
        if handle is not None and handle.task is not None:
            try:
                await asyncio.wait_for(
                    asyncio.shield(handle.task),
                    timeout=settings.runtime_cancel_grace_seconds,
                )
            except TimeoutError:
                handle.task.cancel()
                try:
                    await handle.task
                except asyncio.CancelledError:
                    pass

        # Persist a scrubbed terminal checkpoint even for a waiting run whose
        # public Backend cancel already made it terminal.
        current = await self.backend.get_run(run_id, ctx)
        if current.status != "cancelled":
            if handle is not None:
                lease = await self.backend.claim_lease(
                    run_id, ctx, current.state_version
                )
                handle.lease_token = lease.lease_token
                handle.state_version = lease.run.state_version
                lease_token = handle.lease_token
                state_version = handle.state_version
            else:
                lease = await self.backend.claim_lease(
                    run_id, ctx, current.state_version
                )
                lease_token = lease.lease_token
                state_version = lease.run.state_version
            state = await self.graph.aget_state(config)
            if handle is not None:
                await self._flush_state_events(handle, dict(state.values or {}))
                state_version = handle.state_version
            event = runtime_event(
                run_id=run_id,
                snapshot_hash=snapshot.snapshot_hash,
                event_type="run_cancelled",
                node_id="supervisor",
                event_key=f"cancel:{current.checkpoint_version}",
                payload={"status": "cancelled"},
            )
            await self.backend.append_events(
                run_id,
                ctx,
                [event],
                expected_version=state_version,
                lease_token=lease_token,
            )
            current = await self.backend.transition(
                run_id,
                ctx,
                expected_version=state_version,
                lease_token=lease_token,
                to_status="cancelled",
                checkpoint_ref=checkpoint_ref(
                    state.config
                ),
                checkpoint_version=current.checkpoint_version + 1,
            )
        await self._scrub_cancelled_checkpoint(config)
        async with self._lock:
            self._handles.pop(run_id, None)
        return RuntimeRunResult(
            run_id=run_id,
            status="cancelled",
            snapshot_hash=snapshot.snapshot_hash,
            checkpoint_version=current.checkpoint_version,
        )

    async def wait(self, run_id: str) -> None:
        async with self._lock:
            handle = self._handles.get(run_id)
            task = handle.task if handle else None
        if task is not None:
            await asyncio.shield(task)

    async def close(self) -> None:
        self._closed = True
        async with self._lock:
            handles = list(self._handles.values())
        for handle in handles:
            handle.shutdown_requested = True
            handle.control.request_drain("shutdown")
        for handle in handles:
            if handle.task is None:
                continue
            try:
                await asyncio.wait_for(
                    asyncio.shield(handle.task),
                    timeout=settings.runtime_cancel_grace_seconds,
                )
            except TimeoutError:
                handle.task.cancel()
        if handles:
            await asyncio.gather(
                *(handle.task for handle in handles if handle.task is not None),
                return_exceptions=True,
            )
        async with self._lock:
            self._handles.clear()

    async def _launch(
        self,
        *,
        record: RunRecord,
        snapshot: DirectAgentExecutionSnapshot,
        graph_context: RuntimeGraphContext,
        ctx: RequestContext,
        config: dict[str, Any],
        graph_input: Any,
        operation: Literal["start", "resume", "recovery"],
        claimed_command: RecoveryCommand | None = None,
    ) -> RuntimeRunResult:
        if self._closed:
            raise RuntimeManagerError("runtime manager is shutting down")
        async with self._lock:
            existing = self._handles.get(snapshot.run_id)
            if existing is not None and existing.task is not None and not existing.task.done():
                if (
                    existing.snapshot.snapshot_hash != snapshot.snapshot_hash
                    or existing.ctx != ctx
                ):
                    raise RuntimeManagerConflict(
                        "run already has a worker with different authority"
                    )
                return RuntimeRunResult(
                    run_id=snapshot.run_id,
                    status="running",
                    snapshot_hash=snapshot.snapshot_hash,
                    checkpoint_version=existing.checkpoint_version,
                )
            try:
                promoted_ref: str | None = None
                promoted_version: int | None = None
                lease = (
                    LeaseRecord(
                        lease_token=claimed_command.lease_token,
                        lease_generation=claimed_command.lease_generation,
                        lease_expires_at=claimed_command.lease_expires_at,
                        checkpoint_generation=claimed_command.checkpoint_generation,
                        checkpoint_ref=claimed_command.checkpoint_ref,
                        checkpoint_version=claimed_command.checkpoint_version,
                        event_ack_cursor=claimed_command.event_ack_cursor,
                        run=record,
                    )
                    if claimed_command is not None
                    else await self.backend.claim_lease(
                    snapshot.run_id, ctx, record.state_version
                    )
                )
                authority_state_version = lease.run.state_version
                authority_event_cursor = lease.event_ack_cursor
                if lease.lease_generation > 0:
                    if lease.checkpoint_ref:
                        _authoritative_ref_config(
                            lease.checkpoint_ref,
                            generation=lease.checkpoint_generation,
                            run_id=snapshot.run_id,
                            snapshot_hash=snapshot.snapshot_hash,
                            ctx=ctx,
                        )
                        if lease.checkpoint_generation == lease.lease_generation:
                            config = _authoritative_ref_config(
                                lease.checkpoint_ref,
                                generation=lease.checkpoint_generation,
                                run_id=snapshot.run_id,
                                snapshot_hash=snapshot.snapshot_hash,
                                ctx=ctx,
                            )
                            # Execute from the authoritative generation head.
                            # The pinned id was used for identity validation;
                            # retaining it here can branch instead of resuming
                            # the root graph's latest interrupt.
                            config["configurable"].pop("checkpoint_id", None)
                        else:
                            source_config = _authoritative_ref_config(
                                lease.checkpoint_ref,
                                generation=lease.checkpoint_generation,
                                run_id=snapshot.run_id,
                                snapshot_hash=snapshot.snapshot_hash,
                                ctx=ctx,
                            )
                            head_config = {
                                "configurable": {
                                    **source_config["configurable"],
                                }
                            }
                            head_config["configurable"].pop("checkpoint_id", None)
                            source_head = await self.graph.aget_state(head_config)
                            source_head_ref = checkpoint_ref(
                                source_head.config,
                                lease_generation=lease.checkpoint_generation,
                            )
                            if (
                                source_head_ref != lease.checkpoint_ref
                                and not await self._checkpoint_is_ancestor(
                                    source_head,
                                    lease.checkpoint_ref,
                                    lease_generation=lease.checkpoint_generation,
                                )
                            ):
                                raise RuntimeLineageInvalid(
                                    "checkpoint generation head is unrelated"
                                )
                            config = await clone_checkpoint_generation(
                                self.checkpointer,
                                source_ref=source_head_ref or lease.checkpoint_ref,
                                target_config=checkpoint_config(
                                    tenant_id=ctx.tenant_id,
                                    user_id=ctx.user_id,
                                    run_id=snapshot.run_id,
                                    snapshot_hash=snapshot.snapshot_hash,
                                    lease_generation=lease.lease_generation,
                                ),
                            )
                            promoted_ref = checkpoint_ref(
                                config, lease_generation=lease.lease_generation
                            )
                            # A generation-only clone preserves the logical
                            # checkpoint version exposed to resume callers.
                            # The new lease generation/ref and run state CAS
                            # fence the physical checkpoint copy. Incrementing
                            # here would make a still-valid waiting interrupt
                            # appear stale after a worker restart.
                            promoted_version = record.checkpoint_version
                    else:
                        config = checkpoint_config(
                            tenant_id=ctx.tenant_id,
                            user_id=ctx.user_id,
                            run_id=snapshot.run_id,
                            snapshot_hash=snapshot.snapshot_hash,
                            lease_generation=lease.lease_generation,
                        )
                        if (
                            claimed_command is not None
                            and claimed_command.command_type == "start"
                            and isinstance(graph_input, dict)
                        ):
                            preflight_event = runtime_event(
                                run_id=snapshot.run_id,
                                snapshot_hash=snapshot.snapshot_hash,
                                event_type="run_preflight",
                                node_id="preflight",
                                event_key="0:0",
                                payload={
                                    "status": "ok",
                                    "agent_revision": snapshot.agent.revision,
                                },
                            )
                            graph_input["events"] = [
                                preflight_event.as_backend_dict()
                            ]
                            await self.graph.aupdate_state(
                                config, graph_input, as_node="preflight"
                            )
                            seeded = await self.graph.aget_state(config)
                            promoted_ref = checkpoint_ref(
                                seeded.config,
                                lease_generation=lease.lease_generation,
                            )
                            promoted_version = record.checkpoint_version + 1
                            graph_input = None
                if record.status == "queued" or promoted_ref is not None:
                    running = await self.backend.transition(
                        snapshot.run_id,
                        ctx,
                        expected_version=authority_state_version,
                        lease_token=lease.lease_token,
                        lease_generation=lease.lease_generation,
                        expected_event_ack_cursor=authority_event_cursor,
                        to_status="running",
                        checkpoint_ref=promoted_ref,
                        checkpoint_version=promoted_version,
                    )
                else:
                    running = lease.run
                checkpoint_state = await self.graph.aget_state(config)
                buffered_events: list[RuntimeEvent] = []
                for raw in (checkpoint_state.values or {}).get("events") or []:
                    try:
                        buffered_events.append(
                            RuntimeEvent(
                                event_id=raw["event_id"],
                                event_type=raw["event_type"],
                                node_id=raw.get("node_id") or "",
                                snapshot_hash=raw["snapshot_hash"],
                                payload=raw.get("payload") or {},
                            )
                        )
                    except (KeyError, TypeError, ValueError):
                        continue
                for offset in range(
                    authority_event_cursor, len(buffered_events), 100
                ):
                    running = await self.backend.append_events(
                        snapshot.run_id,
                        ctx,
                        buffered_events[offset : offset + 100],
                        expected_version=running.state_version,
                        lease_token=lease.lease_token,
                        lease_generation=lease.lease_generation,
                        event_cursor_start=offset,
                    )
                    authority_event_cursor = running.event_ack_cursor
            except BackendRunConflict as exc:
                raise RuntimeManagerConflict("run lease or state changed") from exc
            handle = _RunHandle(
                run_id=snapshot.run_id,
                ctx=ctx,
                snapshot=snapshot,
                graph_context=graph_context,
                config=config,
                lease_token=lease.lease_token,
                state_version=running.state_version,
                checkpoint_version=running.checkpoint_version,
                lease_generation=lease.lease_generation,
                event_ack_cursor=authority_event_cursor,
                deadline_at=(
                    claimed_command.deadline_at
                    if claimed_command is not None
                    else running.deadline_at
                ),
                control=RunControl(),
                operation=operation,
            )
            handle.task = asyncio.create_task(
                self._supervise(handle, graph_input),
                name=f"agent-run:{snapshot.run_id}",
            )
            self._handles[snapshot.run_id] = handle
        return RuntimeRunResult(
            run_id=snapshot.run_id,
            status="running",
            snapshot_hash=snapshot.snapshot_hash,
            checkpoint_version=running.checkpoint_version,
        )

    async def _supervise(self, handle: _RunHandle, graph_input: Any) -> None:
        lease_task = asyncio.create_task(self._renew_lease(handle))
        terminal_status = "failed"
        error_code: str | None = None
        try:
            deadline = datetime.fromisoformat(handle.deadline_at.replace("Z", "+00:00"))
            remaining = max(
                0.0, (deadline - datetime.now(timezone.utc)).total_seconds()
            )
            timeout_seconds = min(
                float(handle.graph_context.limits.timeout_seconds), remaining
            )
            async with asyncio.timeout(timeout_seconds):
                await self.graph.ainvoke(
                    graph_input,
                    handle.config,
                    context=handle.graph_context,
                    control=handle.control,
                )
        except GraphDrained:
            if not handle.cancel_requested:
                error_code = "runtime_drained"
        except TimeoutError:
            error_code = "runtime_timeout"
        except asyncio.CancelledError:
            if not handle.cancel_requested:
                error_code = "runtime_cancelled_unexpectedly"
        except Exception:
            error_code = "runtime_execution_failed"
        finally:
            lease_task.cancel()
            lease_results = await asyncio.gather(lease_task, return_exceptions=True)
            if (
                lease_results
                and isinstance(lease_results[0], Exception)
                and not isinstance(lease_results[0], asyncio.CancelledError)
                and not handle.cancel_requested
            ):
                error_code = "runtime_lease_lost"

        if handle.shutdown_requested:
            try:
                snapshot = await self.graph.aget_state(handle.config)
                await self._flush_state_events(handle, dict(snapshot.values or {}))
            except (BackendRunError, BackendRunConflict):
                pass
            finally:
                async with self._lock:
                    if self._handles.get(handle.run_id) is handle:
                        self._handles.pop(handle.run_id, None)
            return
        if handle.cancel_requested:
            await self._finalize_cancel_handle(handle)
            return
        if handle.lease_lost:
            # Ownership was conclusively lost. Do not mutate the private
            # checkpoint or attempt a stale-lease terminal transition.
            async with self._lock:
                if self._handles.get(handle.run_id) is handle:
                    self._handles.pop(handle.run_id, None)
            return
        try:
            snapshot = await self.graph.aget_state(handle.config)
            values = dict(snapshot.values or {})
            if error_code is not None:
                await self._terminalize_failed_handle(handle, error_code)
                return
            await self._flush_state_events(handle, values)
            ref = checkpoint_ref(
                snapshot.config, lease_generation=handle.lease_generation or None
            )
            checkpoint_version = handle.checkpoint_version + 1
            if snapshot.interrupts:
                terminal_status = "waiting_input"
                pending = values.get("pending_input") or {}
                await self._transition_handle(
                    handle,
                    to_status=terminal_status,
                    checkpoint_ref_value=ref,
                    checkpoint_version=checkpoint_version,
                    pending_input={
                        "question": str(pending.get("question") or "")[:2_000]
                    },
                )
            elif values.get("status") == "completed":
                result = {"output": values.get("final_output") or ""}
                if backend_result_wire_size(result) > MAX_BACKEND_RESULT_JSON_BYTES:
                    terminal_status = "failed"
                    await self._transition_handle(
                        handle,
                        to_status=terminal_status,
                        checkpoint_ref_value=ref,
                        checkpoint_version=checkpoint_version,
                        error_code="runtime_result_too_large",
                        error_message=(
                            "Agent output exceeded the persisted result limit."
                        ),
                    )
                else:
                    terminal_status = "completed"
                    await self._transition_handle(
                        handle,
                        to_status=terminal_status,
                        checkpoint_ref_value=ref,
                        checkpoint_version=checkpoint_version,
                        result=result,
                    )
            else:
                error_code = str(values.get("error_code") or "runtime_failed")
                await self._transition_handle(
                    handle,
                    to_status="failed",
                    checkpoint_ref_value=ref,
                    checkpoint_version=checkpoint_version,
                    error_code=error_code,
                error_message="Agent runtime could not complete the request.",
                )
        except (BackendRunError, BackendRunConflict):
            # Backend is the run-status authority. A failed CAS is observable
            # there and must never be hidden by a local fallback transition.
            pass
        finally:
            async with self._lock:
                if self._handles.get(handle.run_id) is handle:
                    self._handles.pop(handle.run_id, None)

    async def _terminalize_failed_handle(
        self, handle: _RunHandle, error_code: str
    ) -> None:
        snapshot = await self.graph.aget_state(handle.config)
        values = dict(snapshot.values or {})
        events = list(values.get("events") or [])
        matching_terminal = any(
            isinstance(item, dict)
            and item.get("event_type") == "run_terminal"
            and (item.get("payload") or {}).get("status") == "failed"
            and (item.get("payload") or {}).get("error_code") == error_code
            for item in events
        )
        if not matching_terminal:
            events = [
                item
                for item in events
                if not (
                    isinstance(item, dict)
                    and item.get("event_type") == "run_terminal"
                )
            ]
            event = runtime_event(
                run_id=handle.run_id,
                snapshot_hash=handle.snapshot.snapshot_hash,
                event_type="run_terminal",
                node_id="supervisor",
                event_key=f"failed:{error_code}:{handle.checkpoint_version}",
                payload={"status": "failed", "error_code": error_code},
            )
            events.append(event.as_backend_dict())
        await self.graph.aupdate_state(
            handle.config,
            {
                "status": "failed",
                "error_code": error_code,
                "active_skill_scope": None,
                "pending_command": None,
                "pending_input": None,
                "rule_allowed_tools": None,
                "events": events,
            },
            as_node="finalize",
        )
        terminal = await self.graph.aget_state(handle.config)
        await self._flush_state_events(handle, dict(terminal.values or {}))
        await self._transition_handle(
            handle,
            to_status="failed",
            checkpoint_ref_value=checkpoint_ref(
                terminal.config, lease_generation=handle.lease_generation or None
            ),
            checkpoint_version=handle.checkpoint_version + 1,
            error_code=error_code,
            error_message="Agent runtime ended safely before completion.",
        )

    async def _renew_lease(self, handle: _RunHandle) -> None:
        interval = max(2.0, settings.runtime_lease_seconds / 2)
        while True:
            await asyncio.sleep(interval)
            try:
                async with handle.state_lock:
                    lease = await self.backend.claim_lease(
                        handle.run_id, handle.ctx, handle.state_version
                    )
                    if lease.lease_generation != getattr(handle, "lease_generation", 0):
                        handle.lease_lost = True
                        handle.control.request_drain("lease_generation_changed")
                        raise BackendRunConflict("lease generation changed")
                    handle.lease_token = lease.lease_token
                    handle.state_version = lease.run.state_version
                    if hasattr(handle, "event_ack_cursor"):
                        handle.event_ack_cursor = lease.event_ack_cursor
                    if lease.run.cancel_requested:
                        handle.cancel_requested = True
                        handle.control.request_drain("cancelled")
            except BackendRunConflict:
                handle.lease_lost = True
                handle.control.request_drain("lease_lost")
                raise
            except BackendRunError:
                # A transport/server failure does not prove ownership loss.
                # Keep the current lease and retry on the next interval.
                logger.warning(
                    "Transient runtime lease refresh failure for run %s",
                    handle.run_id,
                    exc_info=True,
                )
            except Exception:
                logger.exception(
                    "Unexpected runtime lease refresh failure for run %s; retrying",
                    handle.run_id,
                )

    async def _finalize_cancel_handle(self, handle: _RunHandle) -> None:
        try:
            current = await self.backend.get_run(handle.run_id, handle.ctx)
            if current.status == "cancelled":
                await self._scrub_cancelled_checkpoint(handle.config)
                return
            lease = await self.backend.claim_lease(
                handle.run_id, handle.ctx, current.state_version
            )
            handle.lease_token = lease.lease_token
            handle.state_version = lease.run.state_version
            handle.checkpoint_version = lease.run.checkpoint_version
            state = await self.graph.aget_state(handle.config)
            await self._flush_state_events(handle, dict(state.values or {}))
            event = runtime_event(
                run_id=handle.run_id,
                snapshot_hash=handle.snapshot.snapshot_hash,
                event_type="run_cancelled",
                node_id="supervisor",
                event_key=f"cancel:{handle.checkpoint_version}",
                payload={"status": "cancelled"},
            )
            async with handle.state_lock:
                record = await self.backend.append_events(
                    handle.run_id,
                    handle.ctx,
                    [event],
                    expected_version=handle.state_version,
                    lease_token=handle.lease_token,
                    lease_generation=handle.lease_generation,
                    event_cursor_start=handle.event_ack_cursor,
                )
                handle.state_version = record.state_version
                handle.checkpoint_version = record.checkpoint_version
                handle.event_ack_cursor = record.event_ack_cursor
            await self._scrub_cancelled_checkpoint(handle.config)
            scrubbed = await self.graph.aget_state(handle.config)
            await self._transition_handle(
                handle,
                to_status="cancelled",
                checkpoint_ref_value=checkpoint_ref(
                    scrubbed.config, lease_generation=handle.lease_generation or None
                ),
                checkpoint_version=handle.checkpoint_version + 1,
            )
        except (BackendRunError, BackendRunConflict):
            pass
        finally:
            async with self._lock:
                if self._handles.get(handle.run_id) is handle:
                    self._handles.pop(handle.run_id, None)

    async def _transition_handle(
        self,
        handle: _RunHandle,
        *,
        to_status: str,
        checkpoint_ref_value: str | None,
        checkpoint_version: int,
        pending_input: dict[str, Any] | None = None,
        result: dict[str, Any] | None = None,
        error_code: str | None = None,
        error_message: str | None = None,
    ) -> None:
        async with handle.state_lock:
            record = await self.backend.transition(
                handle.run_id,
                handle.ctx,
                expected_version=handle.state_version,
                lease_token=handle.lease_token,
                lease_generation=handle.lease_generation,
                expected_event_ack_cursor=handle.event_ack_cursor,
                to_status=to_status,
                checkpoint_ref=checkpoint_ref_value,
                checkpoint_version=checkpoint_version,
                pending_input=pending_input,
                result=result,
                error_code=error_code,
                error_message=error_message,
            )
            handle.state_version = record.state_version
            handle.checkpoint_version = record.checkpoint_version

    async def _flush_state_events(
        self, handle: _RunHandle, state: dict[str, Any]
    ) -> None:
        events = []
        for raw in state.get("events") or []:
            try:
                events.append(
                    RuntimeEvent(
                        event_id=raw["event_id"],
                        event_type=raw["event_type"],
                        node_id=raw.get("node_id") or "",
                        snapshot_hash=raw["snapshot_hash"],
                        payload=raw.get("payload") or {},
                    )
                )
            except (KeyError, TypeError, ValueError):
                continue
        if len(events) > handle.event_ack_cursor:
            for offset in range(handle.event_ack_cursor, len(events), 100):
                batch = events[offset : offset + 100]
                async with handle.state_lock:
                    record = await self.backend.append_events(
                        handle.run_id,
                        handle.ctx,
                        batch,
                        expected_version=handle.state_version,
                        lease_token=handle.lease_token,
                        lease_generation=handle.lease_generation,
                        event_cursor_start=offset,
                    )
                    handle.state_version = record.state_version
                    handle.checkpoint_version = record.checkpoint_version
                    handle.event_ack_cursor = record.event_ack_cursor

    async def _existing_result(
        self,
        run_id: str,
        snapshot_hash: str | None,
        ctx: RequestContext,
    ) -> RuntimeRunResult | None:
        async with self._lock:
            handle = self._handles.get(run_id)
            if (
                handle is None
                or handle.task is None
                or handle.task.done()
            ):
                return None
            if (
                handle.ctx != ctx
                or (
                    snapshot_hash is not None
                    and handle.snapshot.snapshot_hash != snapshot_hash
                )
            ):
                raise RuntimeManagerConflict(
                    "run already has a worker with different authority"
                )
            return RuntimeRunResult(
                run_id=run_id,
                status="running",
                snapshot_hash=handle.snapshot.snapshot_hash,
                checkpoint_version=handle.checkpoint_version,
            )

    async def _run_lock(self, run_id: str) -> asyncio.Lock:
        async with self._lock:
            return self._run_locks.setdefault(run_id, asyncio.Lock())

    async def recover_once(self) -> int:
        claimed = await self.backend.claim_recovery_commands(
            limit=settings.runtime_recovery_batch_size,
            lease_seconds=settings.runtime_lease_seconds,
        )
        completed = 0
        for command in claimed.items:
            ctx = RequestContext(
                tenant_id=command.tenant_id,
                user_id=command.user_id,
                role=command.role,
            )
            try:
                await self._dispatch_recovery(command, ctx)
                await self.backend.complete_recovery_dispatch(command, ctx)
                completed += 1
            except (RuntimeLineageInvalid, ValueError, RuntimePreflightError) as exc:
                try:
                    if command.command_type in {"cancel", "deadline_cleanup"}:
                        await self._cleanup_claimed_command(command, ctx)
                    else:
                        lineage = isinstance(exc, RuntimeLineageInvalid)
                        await self._fail_owned_preflight(
                            command,
                            ctx,
                            error_code=(
                                "resume_lineage_invalid"
                                if lineage
                                else "runtime_preflight_invalid"
                            ),
                            error_message=(
                                "Resume checkpoint lineage was rejected."
                                if lineage
                                else "Execution preflight was rejected."
                            ),
                        )
                    await self.backend.complete_recovery_dispatch(command, ctx)
                    completed += 1
                except Exception:
                    logger.warning(
                        "Deterministic recovery failure could not be terminalized",
                        exc_info=True,
                        extra={
                            "run_id": command.run_id,
                            "command_id": command.command_id,
                        },
                    )
                continue
            except asyncio.CancelledError:
                raise
            except Exception:
                # Leave the command claimed; Backend will make it available
                # again after the bounded dispatch claim expires.
                logger.warning(
                    "Agent recovery command failed; leaving it unacknowledged",
                    exc_info=True,
                    extra={
                        "run_id": command.run_id,
                        "command_id": command.command_id,
                    },
                )
                continue
        return completed

    async def _dispatch_recovery(
        self, command: RecoveryCommand, ctx: RequestContext
    ) -> None:
        async with await self._run_lock(command.run_id):
            if command.command_type == "start":
                envelope = SnapshotCanonicalEnvelope.model_validate(command.snapshot)
                snapshot = envelope.decode()
                await self._start_locked(
                    snapshot,
                    str(command.input.get("message") or ""),
                    ctx,
                    operation="recovery",
                    claimed_command=command,
                )
            elif command.command_type == "resume":
                await self._recover_resume_locked(command, ctx)
            else:
                if command.lease_generation > 0:
                    await self._cleanup_claimed_command(command, ctx)
                else:
                    # Compatibility for pre-fencing recovery envelopes.
                    snapshot = await self.backend.execution_snapshot(
                        command.run_id, ctx
                    )
                    config = checkpoint_config(
                        tenant_id=ctx.tenant_id,
                        user_id=ctx.user_id,
                        run_id=command.run_id,
                        snapshot_hash=snapshot.snapshot_hash,
                    )
                    await self._scrub_cancelled_checkpoint(config)

    async def _cleanup_claimed_command(
        self, command: RecoveryCommand, ctx: RequestContext
    ) -> None:
        """Terminalize an owned cancel/deadline takeover without executing code."""
        if command.checkpoint_ref:
            _authoritative_ref_config(
                command.checkpoint_ref,
                generation=command.checkpoint_generation,
                run_id=command.run_id,
                snapshot_hash=command.snapshot_hash,
                ctx=ctx,
            )
            if command.checkpoint_generation == command.lease_generation:
                config = _authoritative_ref_config(
                    command.checkpoint_ref,
                    generation=command.checkpoint_generation,
                    run_id=command.run_id,
                    snapshot_hash=command.snapshot_hash,
                    ctx=ctx,
                )
            else:
                config = await clone_checkpoint_generation(
                    self.checkpointer,
                    source_ref=command.checkpoint_ref,
                    target_config=checkpoint_config(
                        tenant_id=ctx.tenant_id,
                        user_id=ctx.user_id,
                        run_id=command.run_id,
                        snapshot_hash=command.snapshot_hash,
                        lease_generation=command.lease_generation,
                    ),
                )
        else:
            config = checkpoint_config(
                tenant_id=ctx.tenant_id,
                user_id=ctx.user_id,
                run_id=command.run_id,
                snapshot_hash=command.snapshot_hash,
                lease_generation=command.lease_generation,
            )
        state = await self.graph.aget_state(config)
        values = dict(state.values or {})
        deadline = command.command_type == "deadline_cleanup"
        if deadline:
            if command.target_terminal not in {"failed", "cancelled"}:
                raise RuntimeManagerError(
                    "deadline cleanup is missing its frozen terminal target"
                )
            status = command.target_terminal
        else:
            status = command.target_terminal or "cancelled"
        error_code = "deadline_exceeded" if deadline and status == "failed" else None
        terminal_type = "deadline_exceeded" if deadline else "run_cancelled"
        terminal = runtime_event(
            run_id=command.run_id,
            snapshot_hash=command.snapshot_hash,
            event_type=terminal_type,
            node_id="supervisor",
            event_key=f"{status}:g{command.lease_generation}",
            payload={"status": status, **({"error_code": error_code} if error_code else {})},
        )
        # Cleanup generations retain no prior private/audit buffer. Backend's
        # cursor already represents events durably accepted from older G.
        events = [terminal.as_backend_dict()]
        await self.graph.aupdate_state(
            config,
            {
                "status": status,
                "error_code": error_code,
                "messages": [],
                "final_output": None,
                "verified_context": None,
                "active_skill_scope": None,
                "pending_command": None,
                "pending_input": None,
                "rule_allowed_tools": None,
                "events": events,
            },
            as_node="finalize",
        )
        terminal_state = await self.graph.aget_state(config)
        state_version = command.state_version
        cursor = command.event_ack_cursor
        record = await self.backend.append_events(
            command.run_id,
            ctx,
            [terminal],
            expected_version=state_version,
            lease_token=command.lease_token,
            lease_generation=command.lease_generation,
            event_cursor_start=cursor,
        )
        state_version = record.state_version
        cursor = record.event_ack_cursor
        await self.backend.transition(
            command.run_id,
            ctx,
            expected_version=state_version,
            lease_token=command.lease_token,
            lease_generation=command.lease_generation,
            expected_event_ack_cursor=cursor,
            to_status=status,
            checkpoint_ref=checkpoint_ref(
                terminal_state.config,
                lease_generation=command.lease_generation,
            ),
            checkpoint_version=command.checkpoint_version + 1,
            error_code=error_code,
            error_message=(
                "Agent runtime exceeded its authoritative deadline."
                if deadline
                else None
            ),
        )

    async def _recover_resume_locked(
        self, command: RecoveryCommand, ctx: RequestContext
    ) -> None:
        message = str(command.input.get("message") or "").strip()
        expected_checkpoint_version = int(
            command.input.get("expected_checkpoint_version", -1)
        )
        expected_checkpoint_ref = str(
            command.input.get("expected_checkpoint_ref") or ""
        ).strip()
        if not expected_checkpoint_ref:
            raise RuntimeLineageInvalid(
                "recovered resume is missing its checkpoint identity"
            )
        record = await self.backend.get_run(command.run_id, ctx)
        if record.snapshot_hash != command.snapshot_hash:
            raise RuntimeManagerConflict(
                "recovered resume snapshot authority changed"
            )
        if (
            command.lease_generation > 0
            and record.checkpoint_ref != command.checkpoint_ref
        ):
            raise RuntimeManagerConflict(
                "recovered resume Backend checkpoint pin changed"
            )
        if record.status not in {"queued", "running"}:
            raise RuntimeManagerConflict("recovered resume run is not executable")

        if command.lease_generation > 0:
            envelope = SnapshotCanonicalEnvelope.model_validate(command.snapshot)
            snapshot = envelope.decode()
        else:
            snapshot = await self.backend.execution_snapshot(command.run_id, ctx)
        graph_context = build_context(
            snapshot=snapshot,
            request_context=ctx,
            model=self.model,
            artifact_reader=self.artifact_reader,
            deps=self.deps,
        )
        pinned_config = (
            _authoritative_ref_config(
                command.checkpoint_ref or "",
                generation=command.checkpoint_generation,
                run_id=command.run_id,
                snapshot_hash=command.snapshot_hash,
                ctx=ctx,
            )
            if command.lease_generation > 0
            else checkpoint_config(
                tenant_id=ctx.tenant_id,
                user_id=ctx.user_id,
                run_id=command.run_id,
                snapshot_hash=snapshot.snapshot_hash,
            )
        )
        pinned = await self.graph.aget_state(pinned_config)
        if not pinned.values and not pinned.next:
            raise RuntimeLineageInvalid(
                "recovered resume has no durable checkpoint"
            )
        config = {
            "configurable": {
                **(pinned_config.get("configurable") or {}),
            }
        }
        config["configurable"].pop("checkpoint_id", None)
        checkpoint = await self.graph.aget_state(config)
        actual_checkpoint_ref = checkpoint_ref(
            checkpoint.config,
            lease_generation=command.checkpoint_generation or None,
        )
        pinned_ref = checkpoint_ref(
            pinned.config,
            lease_generation=command.checkpoint_generation or None,
        )
        if command.lease_generation == 0:
            pinned_ref = expected_checkpoint_ref
        origin_progress = await self._lineage_progress(
            pinned,
            pinned_ref or "",
            expected_checkpoint_ref,
            run_id=command.run_id,
            snapshot_hash=command.snapshot_hash,
            ctx=ctx,
        )
        if origin_progress is None:
            raise RuntimeLineageInvalid(
                "recovered resume checkpoint origin is unrelated"
            )
        if actual_checkpoint_ref == pinned_ref and not origin_progress:
            if (
                not message
                or not checkpoint.interrupts
                or (
                    pinned_ref == expected_checkpoint_ref
                    and record.checkpoint_version != expected_checkpoint_version
                )
            ):
                raise RuntimeLineageInvalid(
                    "recovered resume does not match the durable interrupt"
                )
            graph_input: Any = Command(resume=message)
        else:
            if origin_progress:
                graph_input = None
            elif not await self._checkpoint_is_ancestor(
                checkpoint,
                pinned_ref or "",
                lease_generation=command.checkpoint_generation or None,
            ):
                raise RuntimeLineageInvalid(
                    "recovered resume checkpoint identity is unrelated"
                )
            else:
                graph_input = None
        launch_command = command
        if (
            command.lease_generation > command.checkpoint_generation
            and actual_checkpoint_ref
            and actual_checkpoint_ref != command.checkpoint_ref
        ):
            launch_command = command.model_copy(
                update={"checkpoint_ref": actual_checkpoint_ref}
            )
        await self._launch(
            record=record,
            snapshot=snapshot,
            graph_context=graph_context,
            ctx=ctx,
            config=config,
            graph_input=graph_input,
            operation="recovery",
            claimed_command=(
                launch_command if command.lease_generation > 0 else None
            ),
        )

    async def _lineage_progress(
        self,
        checkpoint: Any,
        checkpoint_ref_value: str,
        expected_ref: str,
        *,
        run_id: str,
        snapshot_hash: str,
        ctx: RequestContext,
    ) -> bool | None:
        queue: list[tuple[Any, str, bool]] = [
            (checkpoint, checkpoint_ref_value, False)
        ]
        seen: set[tuple[str, bool]] = set()
        for _ in range(200):
            if not queue:
                return None
            current, current_ref, progressed = queue.pop(0)
            if current_ref == expected_ref:
                return progressed
            marker = (current_ref, progressed)
            if marker in seen:
                continue
            seen.add(marker)
            metadata = dict(getattr(current, "metadata", None) or {})
            seed_ref = str(metadata.get("logical_seed_ref") or "")
            if seed_ref:
                try:
                    generation = int(seed_ref.split(":", 3)[1])
                    seed = await self.graph.aget_state(
                        _authoritative_ref_config(
                            seed_ref,
                            generation=generation,
                            run_id=run_id,
                            snapshot_hash=snapshot_hash,
                            ctx=ctx,
                        )
                    )
                    queue.append((seed, seed_ref, progressed))
                except (ValueError, KeyError, IndexError):
                    pass
            parent_config = getattr(current, "parent_config", None)
            if parent_config:
                try:
                    generation = int(current_ref.split(":", 3)[1])
                    current_configurable = current.config.get("configurable") or {}
                    parent_configurable = parent_config.get("configurable") or {}
                    if (
                        parent_configurable.get("thread_id")
                        != current_configurable.get("thread_id")
                        or parent_configurable.get("checkpoint_ns", "") != ""
                    ):
                        continue
                    parent_ref = checkpoint_ref(
                        parent_config, lease_generation=generation
                    )
                    if parent_ref:
                        parent = await self.graph.aget_state(parent_config)
                        queue.append((parent, parent_ref, True))
                except (ValueError, IndexError):
                    pass
        return None

    async def _checkpoint_is_ancestor(
        self,
        current: Any,
        expected_ref: str,
        *,
        lease_generation: int | None = None,
    ) -> bool:
        current_configurable = current.config.get("configurable") or {}
        expected_thread = current_configurable.get("thread_id")
        expected_namespace = current_configurable.get("checkpoint_ns", "")
        parent_config = current.parent_config
        seen: set[str] = set()
        for _ in range(1_000):
            if not parent_config:
                return False
            configurable = parent_config.get("configurable") or {}
            if (
                configurable.get("thread_id") != expected_thread
                or configurable.get("checkpoint_ns", "") != expected_namespace
            ):
                return False
            parent_ref = checkpoint_ref(
                parent_config, lease_generation=lease_generation
            )
            if parent_ref is None or parent_ref in seen:
                return False
            if parent_ref == expected_ref:
                return True
            seen.add(parent_ref)
            parent = await self.graph.aget_state(parent_config)
            if checkpoint_ref(
                parent.config, lease_generation=lease_generation
            ) != parent_ref:
                return False
            parent_config = parent.parent_config
        return False

    async def recovery_loop(self) -> None:
        while not self._closed:
            try:
                await self.recover_once()
            except asyncio.CancelledError:
                raise
            except Exception:
                logger.warning(
                    "Agent recovery scan failed; retrying on the next interval",
                    exc_info=True,
                )
            try:
                await asyncio.sleep(settings.runtime_recovery_interval_seconds)
            except asyncio.CancelledError:
                raise

    async def _scrub_cancelled_checkpoint(self, config: dict[str, Any]) -> None:
        state = await self.graph.aget_state(config)
        if not state.values and not state.next:
            return
        await self.graph.aupdate_state(
            config,
            {
                "status": "cancelled",
                "active_skill_scope": None,
                "pending_command": None,
                "pending_input": None,
                "rule_allowed_tools": None,
            },
            as_node="finalize",
        )
