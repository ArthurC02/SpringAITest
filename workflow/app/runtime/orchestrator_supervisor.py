"""Background ownership and recovery for durable Root commands."""

from __future__ import annotations

import asyncio
import logging

from app.runtime.manager import RuntimeRunManager
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
        self, backend: OrchestratorBackendClient, manager: RuntimeRunManager
    ) -> None:
        self.backend = backend
        self.manager = manager
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
        runtime = build_production_root(self.backend, self.manager, ctx)
        execution = asyncio.create_task(
            runtime.execute(snapshot, {}, cancel_on_task_cancel=False)
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
        if current.status in {"completed", "failed", "cancelled"}:
            return
        terminal = (
            result.status
            if result.status in {"completed", "failed", "cancelled"}
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
