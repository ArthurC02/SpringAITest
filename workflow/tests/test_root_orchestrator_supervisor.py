import asyncio

import pytest

from app.runtime.orchestrator_supervisor import RootRuntimeSupervisor
from app.runtime.orchestrator_backend import OrchestratorBackendPermanentError
from app.security import RequestContext


class Backend:
    def __init__(self):
        self.claims = 0

    async def claim(self, run_id, command_id, ctx):
        self.claims += 1
        await asyncio.sleep(0)
        return None


@pytest.mark.asyncio
async def test_schedule_is_immediate_and_deduplicates_active_command():
    backend = Backend()
    supervisor = RootRuntimeSupervisor(backend, object())
    ctx = RequestContext(tenant_id="tenant", user_id="user", role="USER")

    assert supervisor.schedule("run", "command", ctx)
    assert not supervisor.schedule("run", "command", ctx)
    await asyncio.sleep(0.01)
    assert backend.claims == 1
    await supervisor.close()


@pytest.mark.asyncio
async def test_permanently_invalid_snapshot_is_cancelled_not_recovered():
    class InvalidClaim:
        run_id = "run"

        def snapshot(self):
            raise OrchestratorBackendPermanentError("invalid snapshot")

    class PermanentBackend(Backend):
        def __init__(self):
            super().__init__()
            self.cancelled = []

        async def cancel_root(self, run_id, ctx, reason):
            self.cancelled.append((run_id, reason))

    backend = PermanentBackend()
    supervisor = RootRuntimeSupervisor(backend, object())
    ctx = RequestContext(tenant_id="tenant", user_id="user", role="USER")

    await supervisor._run_claim(InvalidClaim(), ctx)

    assert backend.cancelled == [
        ("run", "Invalid immutable Root execution contract")
    ]
