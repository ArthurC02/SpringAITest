"""Background ownership and recovery for durable Root commands."""

from __future__ import annotations

import asyncio
import logging
from datetime import datetime, timezone

from app.runtime.manager import RuntimeRunManager
from app.runtime.checkpoints import PostgresCheckpointStore
from app.runtime.orchestrator_backend import (
    OrchestratorBackendClient,
    OrchestratorBackendPermanentError,
    RootCommandClaim,
)
from app.runtime.orchestrator_production import build_production_root
from app.security import RequestContext
from app.settings import settings

logger = logging.getLogger(__name__)


class RootRuntimeSupervisor:
    def __init__(
        self, backend: OrchestratorBackendClient, manager: RuntimeRunManager, checkpoints: PostgresCheckpointStore | None = None
    ) -> None:
        self.backend = backend
        self.manager = manager
        self.checkpoints = checkpoints
        self._tasks: dict[str, asyncio.Task[None]] = {}
        self._closed = False
        self._recovery_task: asyncio.Task[None] | None = None

    def start(self) -> None:
        if self._recovery_task is None:
            self._recovery_task = asyncio.create_task(self._recovery_loop())

    def schedule(
        self, run_id: str, command_id: str, ctx: RequestContext
    ) -> bool:
        key = f"{run_id}:{command_id}"
        existing = self._tasks.get(key)
        if existing is not None and not existing.done():
            return False
        task = asyncio.create_task(
            self._claim_and_run(run_id, command_id, ctx), name=f"root:{key}"
        )
        self._tasks[key] = task
        task.add_done_callback(lambda done: self._finished(key, done))
        return True

    async def _claim_and_run(
        self, run_id: str, command_id: str, ctx: RequestContext
    ) -> None:
        claim = await self.backend.claim(run_id, command_id, ctx)
        if claim is not None:
            await self._run_claim(claim, ctx)

    async def _run_claim(
        self, claim: RootCommandClaim, ctx: RequestContext
    ) -> None:
        try:
            snapshot = claim.snapshot()
        except OrchestratorBackendPermanentError:
            await self.backend.cancel_root(
                claim.run_id, ctx, "Invalid immutable Root execution contract"
            )
            return
        if (
            snapshot.caller.tenant_id != ctx.tenant_id
            or snapshot.caller.user_id != ctx.user_id
            or snapshot.caller.role != ctx.role
        ):
            await self.backend.cancel_root(
                claim.run_id, ctx, "Root recovery identity does not match snapshot"
            )
            return
        context: dict = {}
        context_round = 1
        if claim.command_type == "resume":
            if not claim.resume_input or not claim.checkpoint_ref or claim.checkpoint_version != 1:
                await self.backend.cancel_root(claim.run_id, ctx, "Invalid Root resume checkpoint authority")
                return
            try:
                checkpoint = await self.checkpoints.get_root_context_checkpoint(claim.checkpoint_ref)
                if checkpoint != {**checkpoint, "root_run_id": claim.run_id, "snapshot_hash": claim.snapshot_hash, "tenant_id": ctx.tenant_id, "user_id": ctx.user_id, "stage": "context", "audit": checkpoint.get("audit", [])}:
                    raise ValueError("Root checkpoint authority does not match claim")
            except Exception:
                await self.backend.cancel_root(claim.run_id, ctx, "Invalid Root resume checkpoint")
                return
            prior_inputs = checkpoint.get("trusted_resume_inputs", [])
            prior_round = checkpoint.get("context_round")
            if (
                not isinstance(prior_round, int)
                or isinstance(prior_round, bool)
                or not 1 <= prior_round <= snapshot.limits.max_context_rounds
                or not isinstance(prior_inputs, list)
                or len(prior_inputs) != prior_round - 1
                or not all(
                    isinstance(item, str) and 0 < len(item) <= 16_384
                    for item in prior_inputs
                )
            ):
                await self.backend.cancel_root(claim.run_id, ctx, "Invalid Root resume checkpoint")
                return
            if prior_round >= snapshot.limits.max_context_rounds:
                await self.backend.cancel_root(
                    claim.run_id, ctx, "Root context round budget exhausted"
                )
                return
            context_round = prior_round + 1
            context = {"user_input": claim.resume_input, "trusted_resume_inputs": prior_inputs}
        runtime = build_production_root(self.backend, self.manager, ctx)
        try:
            deadline_at = getattr(claim, "deadline_at", None)
            deadline = datetime.fromisoformat(
                deadline_at.replace("Z", "+00:00")
            ) if deadline_at else None
            remaining_deadline_seconds = (
                deadline.astimezone(timezone.utc) - datetime.now(timezone.utc)
            ).total_seconds() if deadline else snapshot.limits.timeout_seconds
        except (TypeError, ValueError) as exc:
            raise OrchestratorBackendPermanentError(
                "Backend returned an invalid Root deadline"
            ) from exc
        if remaining_deadline_seconds <= 0:
            await self.backend.cancel_root(
                claim.run_id, ctx, "Root run deadline expired"
            )
            return
        execution = asyncio.create_task(
            runtime.execute(
                snapshot,
                context,
                cancel_on_task_cancel=False,
                remaining_deadline_seconds=remaining_deadline_seconds,
                context_round=context_round,
            )
        )
        try:
            while True:
                try:
                    result = await asyncio.wait_for(
                        asyncio.shield(execution),
                        timeout=settings.multi_agent_heartbeat_seconds,
                    )
                    break
                except TimeoutError:
                    await self.backend.renew_claim(claim, ctx)
        except BaseException:
            execution.cancel()
            await asyncio.gather(execution, return_exceptions=True)
            raise
        await self.backend.complete_dispatch(claim, ctx)
        current = await self.backend.get_root(claim.run_id, ctx)
        if current.status in {"completed", "failed", "cancelled", "timed_out"}:
            return
        if result.status == "waiting_input":
            if self.checkpoints is None: raise RuntimeError("Root checkpoint store is unavailable")
            accumulated = [*context.get("trusted_resume_inputs", []), *([context["user_input"]] if context.get("user_input") else [])]
            if (
                len(accumulated) != context_round - 1
                or any(
                    not isinstance(item, str) or not 0 < len(item) <= 16_384
                    for item in accumulated
                )
            ):
                await self.backend.cancel_root(claim.run_id, ctx, "Invalid Root resume checkpoint")
                return
            reference, version = await self.checkpoints.put_root_context_checkpoint({"root_run_id": claim.run_id, "snapshot_hash": claim.snapshot_hash, "tenant_id": ctx.tenant_id, "user_id": ctx.user_id, "stage": "context", "audit": result.audit, "trusted_resume_inputs": accumulated, "context_round": context_round})
            await self.backend.transition_root(claim, ctx, to_status="waiting_input", result=result.model_dump(mode="json"), error_code=None, error_message=None, events=[{"event_type": item["event_type"], "payload": item} for item in result.audit], checkpoint_ref=reference, checkpoint_version=version)
            return
        terminal = (
            result.status
            if result.status in {"completed", "failed", "cancelled", "timed_out"}
            else "failed"
        )
        try:
            await self.backend.transition_root(
                claim,
                ctx,
                to_status=terminal,
                result=result.model_dump(mode="json"),
                error_code=None if terminal == "completed" else result.status,
                error_message=(
                    None if terminal == "completed" else "; ".join(result.limitations)[:500]
                ),
                events=[
                    {"event_type": item["event_type"], "payload": item}
                    for item in result.audit
                ],
            )
        except OrchestratorBackendPermanentError:
            await self.backend.cancel_root(
                claim.run_id, ctx, "Root terminal payload rejected permanently"
            )

    async def recover_once(self) -> None:
        response = await self.backend.claim_recovery()
        for item in response.items:
            key = f"{item.claim.run_id}:{item.claim.command_id}"
            if key in self._tasks and not self._tasks[key].done():
                continue
            ctx = RequestContext(
                tenant_id=item.tenant_id, user_id=item.user_id, role=item.role
            )
            task = asyncio.create_task(self._run_claim(item.claim, ctx))
            self._tasks[key] = task
            task.add_done_callback(lambda done, key=key: self._finished(key, done))

    def _finished(self, key: str, task: asyncio.Task[None]) -> None:
        self._tasks.pop(key, None)
        if task.cancelled():
            return
        error = task.exception()
        if error is not None:
            logger.warning(
                "Root command execution failed",
                exc_info=(type(error), error, error.__traceback__),
            )

    async def _recovery_loop(self) -> None:
        while not self._closed:
            try:
                await self.recover_once()
            except asyncio.CancelledError:
                raise
            except Exception:
                logger.warning("Root recovery scan failed", exc_info=True)
            await asyncio.sleep(settings.runtime_recovery_interval_seconds)

    async def close(self) -> None:
        self._closed = True
        if self._recovery_task is not None:
            self._recovery_task.cancel()
        tasks = [*self._tasks.values()]
        if self._recovery_task is not None:
            tasks.append(self._recovery_task)
        for task in self._tasks.values():
            task.cancel()
        if tasks:
            await asyncio.gather(*tasks, return_exceptions=True)
