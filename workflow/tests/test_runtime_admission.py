import asyncio
from types import SimpleNamespace
from types import MethodType

import pytest
from langgraph.checkpoint.memory import InMemorySaver
from pydantic import ValidationError

from app.runtime.admission import AdmissionClosed, AdmissionCoordinator
from app.runtime.api import _call
from app.runtime.checkpoints import strict_serializer
from app.runtime.manager import RuntimeAdmissionRejected, RuntimeRunManager
from app.runtime.orchestrator_supervisor import RootRuntimeSupervisor
from app.security import RequestContext
from app.settings import Settings


def _coordinator(*, active: int = 1, queued: int = 1, mode: str = "enforce"):
    return AdmissionCoordinator(
        mode=mode,
        root_active=active,
        root_queue=queued,
        direct_active=active,
        direct_queue=queued,
    )


@pytest.mark.parametrize("active_limit", [1, 2, 10])
@pytest.mark.asyncio
async def test_admission_enforces_active_and_queue_limits(active_limit: int) -> None:
    coordinator = _coordinator(active=active_limit, queued=active_limit)
    active = [coordinator.reserve("root") for _ in range(active_limit)]
    queued = [coordinator.reserve("root") for _ in range(active_limit)]

    assert all(lease is not None for lease in active + queued)
    assert coordinator.reserve("root") is None
    assert coordinator.metrics("root").active == active_limit
    assert coordinator.metrics("root").queued == active_limit
    assert coordinator.metrics("root").rejected == 1

    active[0].release()  # type: ignore[union-attr]
    await queued[0].wait()  # type: ignore[union-attr]
    assert coordinator.metrics("root").active == active_limit
    assert coordinator.metrics("root").queued == active_limit - 1

    for lease in [*active[1:], *queued]:
        lease.release()  # type: ignore[union-attr]
    assert coordinator.metrics("root").active == 0
    assert coordinator.metrics("root").queued == 0


@pytest.mark.asyncio
async def test_cancelled_waiter_does_not_leak_capacity() -> None:
    coordinator = _coordinator()
    active = coordinator.reserve("direct")
    queued = coordinator.reserve("direct")
    waiter = asyncio.create_task(queued.wait())  # type: ignore[union-attr]
    waiter.cancel()
    with pytest.raises(asyncio.CancelledError):
        await waiter

    queued.release()  # type: ignore[union-attr]
    active.release()  # type: ignore[union-attr]
    assert coordinator.metrics("direct").active == 0
    assert coordinator.metrics("direct").queued == 0


@pytest.mark.asyncio
async def test_close_wakes_queued_work_and_active_release_drains_pool() -> None:
    coordinator = _coordinator()
    active = coordinator.reserve("direct")
    queued = coordinator.reserve("direct")
    coordinator.close("direct")

    with pytest.raises(AdmissionClosed):
        await queued.wait()  # type: ignore[union-attr]
    active.release()  # type: ignore[union-attr]
    assert coordinator.reserve("direct") is None
    assert coordinator.metrics("direct").active == 0
    assert coordinator.metrics("direct").queued == 0


@pytest.mark.asyncio
async def test_root_and_direct_pools_cannot_deadlock_each_other() -> None:
    coordinator = _coordinator(active=1, queued=0)
    root = coordinator.reserve("root")
    direct = coordinator.reserve("direct")

    assert root is not None
    assert direct is not None
    await asyncio.gather(root.wait(), direct.wait())
    root.release()
    direct.release()


@pytest.mark.asyncio
async def test_root_production_schedule_rejects_only_after_bounded_queue() -> None:
    coordinator = _coordinator(active=1, queued=1)
    release = asyncio.Event()

    class Backend:
        async def claim(self, *_args):
            await release.wait()
            return None

    supervisor = RootRuntimeSupervisor(
        Backend(), SimpleNamespace(admission=coordinator)
    )
    ctx = RequestContext(tenant_id="tenant", user_id="user", role="USER")

    assert supervisor.schedule("run-1", "command", ctx)
    assert supervisor.schedule("run-2", "command", ctx)
    assert not supervisor.schedule("run-3", "command", ctx)
    await asyncio.sleep(0)
    assert coordinator.metrics("root").active == 1
    assert coordinator.metrics("root").queued == 1

    release.set()
    await asyncio.gather(*supervisor._tasks.values())
    assert coordinator.metrics("root").active == 0
    await supervisor.close()


@pytest.mark.asyncio
async def test_root_recovery_claim_batch_is_limited_by_immediate_permits() -> None:
    coordinator = _coordinator(active=2, queued=10)

    class Backend:
        seen_limit = None

        async def claim_recovery(self, *, limit):
            self.seen_limit = limit
            return SimpleNamespace(items=[])

    backend = Backend()
    supervisor = RootRuntimeSupervisor(
        backend, SimpleNamespace(admission=coordinator)
    )
    await supervisor.recover_once()

    assert backend.seen_limit == 2
    assert coordinator.metrics("root").active == 0


@pytest.mark.asyncio
async def test_root_close_before_first_task_step_releases_owned_lease() -> None:
    coordinator = _coordinator(active=1, queued=0)

    class Backend:
        async def claim(self, *_args):
            raise AssertionError("task cancelled before its first step must not claim")

    supervisor = RootRuntimeSupervisor(
        Backend(), SimpleNamespace(admission=coordinator)
    )
    ctx = RequestContext(tenant_id="tenant", user_id="user", role="USER")

    assert supervisor.schedule("run", "command", ctx)
    await supervisor.close()

    assert coordinator.metrics("root").active == 0
    assert coordinator.metrics("root").queued == 0


def _manager_with_admission(coordinator, backend=None):
    return RuntimeRunManager(
        checkpointer=InMemorySaver(serde=strict_serializer()),
        backend=backend or SimpleNamespace(),
        model=SimpleNamespace(),
        admission=coordinator,
    )


@pytest.mark.asyncio
async def test_approved_http_execution_uses_direct_capacity_before_run_lock() -> None:
    coordinator = _coordinator(active=1, queued=0)
    manager = _manager_with_admission(coordinator)
    entered = asyncio.Event()
    release = asyncio.Event()

    async def admitted(self, run_id, *_args, **_kwargs):
        entered.set()
        await release.wait()
        return SimpleNamespace(run_id=run_id)

    manager._execute_approved_write_admitted = MethodType(admitted, manager)
    ctx = RequestContext(tenant_id="tenant", user_id="user", role="USER")
    first = asyncio.create_task(manager.execute_approved_write("run-1", "a-1", ctx))
    await entered.wait()

    with pytest.raises(RuntimeAdmissionRejected):
        await manager.execute_approved_write("run-2", "a-2", ctx)
    assert coordinator.metrics("direct").active == 1
    assert coordinator.metrics("direct").rejected == 1

    release.set()
    await first
    assert coordinator.metrics("direct").active == 0


@pytest.mark.asyncio
@pytest.mark.parametrize("active_limit", [1, 2])
async def test_approved_recovery_claims_only_immediate_capacity_and_releases_all(
    active_limit: int,
) -> None:
    coordinator = _coordinator(active=active_limit, queued=10)

    class Backend:
        seen_limit = None

        async def claim_approval_recovery(self, *, limit):
            self.seen_limit = limit
            return [
                {
                    "tenant_id": "tenant",
                    "approver_id": "user",
                    "run_id": f"run-{index}",
                    "approval_id": f"approval-{index}",
                    "claim_token": f"claim-{index}",
                }
                for index in range(limit)
            ]

    backend = Backend()
    manager = _manager_with_admission(coordinator, backend)
    running = 0
    max_running = 0

    async def admitted(self, run_id, *_args, **_kwargs):
        nonlocal running, max_running
        running += 1
        max_running = max(max_running, running)
        await asyncio.sleep(0)
        running -= 1
        return SimpleNamespace(run_id=run_id)

    manager._execute_approved_write_admitted = MethodType(admitted, manager)

    assert await manager.recover_approved_writes_once() == active_limit
    assert backend.seen_limit == active_limit
    assert max_running == active_limit
    assert coordinator.metrics("direct").active == 0
    assert coordinator.metrics("direct").queued == 0


@pytest.mark.asyncio
async def test_approved_recovery_exception_and_cancellation_release_capacity() -> None:
    coordinator = _coordinator(active=1, queued=0)

    class Backend:
        async def claim_approval_recovery(self, *, limit):
            assert limit == 1
            return [{
                "tenant_id": "tenant",
                "approver_id": "user",
                "run_id": "run",
                "approval_id": "approval",
                "claim_token": "claim",
            }]

    manager = _manager_with_admission(coordinator, Backend())

    async def failed(self, *_args, **_kwargs):
        raise RuntimeError("boom")

    manager._execute_approved_write_admitted = MethodType(failed, manager)
    assert await manager.recover_approved_writes_once() == 0
    assert coordinator.metrics("direct").active == 0

    entered = asyncio.Event()

    async def blocked(self, *_args, **_kwargs):
        entered.set()
        await asyncio.Event().wait()

    manager._execute_approved_write_admitted = MethodType(blocked, manager)
    task = asyncio.create_task(manager.recover_approved_writes_once())
    await entered.wait()
    task.cancel()
    with pytest.raises(asyncio.CancelledError):
        await task
    assert coordinator.metrics("direct").active == 0


@pytest.mark.asyncio
async def test_direct_busy_contract_is_503_with_retry_after() -> None:
    async def rejected():
        raise RuntimeAdmissionRejected("full")

    with pytest.raises(Exception) as caught:
        await _call(rejected())
    assert caught.value.status_code == 503
    assert caught.value.headers["Retry-After"] == "1"


def test_admission_settings_defaults_and_audited_hard_ceilings() -> None:
    configured = Settings(_env_file=None)
    assert (
        configured.runtime_root_active_limit,
        configured.runtime_root_queue_limit,
        configured.runtime_direct_active_limit,
        configured.runtime_direct_queue_limit,
    ) == (8, 16, 16, 32)

    with pytest.raises(ValidationError):
        Settings(_env_file=None, runtime_root_active_limit=65)
    with pytest.raises(ValidationError):
        Settings(_env_file=None, runtime_direct_queue_limit=257)


@pytest.mark.asyncio
async def test_observe_and_off_modes_do_not_reject() -> None:
    for mode in ("observe", "off"):
        coordinator = _coordinator(active=1, queued=0, mode=mode)
        leases = [coordinator.reserve("root") for _ in range(10)]
        assert all(lease is not None for lease in leases)
        for lease in leases:
            lease.release()  # type: ignore[union-attr]
