import asyncio
import json
import os
from types import SimpleNamespace

import pytest

from app.runtime.orchestrator_supervisor import RootRuntimeSupervisor
from app.runtime.orchestrator_backend import OrchestratorBackendPermanentError
from app.runtime.checkpoints import PostgresCheckpointStore
from app.runtime.orchestrator import RootRunResult
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


@pytest.mark.asyncio
@pytest.mark.parametrize(
    "checkpoint",
    [
        ValueError("invalid HMAC"),
        {"root_run_id": "other-run", "snapshot_hash": "a" * 64, "tenant_id": "tenant", "user_id": "user", "stage": "context"},
        {"root_run_id": "run", "snapshot_hash": "b" * 64, "tenant_id": "other-tenant", "user_id": "user", "stage": "context"},
        {"root_run_id": "run", "snapshot_hash": "b" * 64, "tenant_id": "tenant", "user_id": "other-user", "stage": "context"},
        {"root_run_id": "run", "snapshot_hash": "b" * 64, "tenant_id": "tenant", "user_id": "user", "stage": "other"},
    ],
)
async def test_resume_rejects_tampered_or_foreign_checkpoint_before_dispatch(
    monkeypatch, checkpoint
):
    class Caller:
        tenant_id = "tenant"
        user_id = "user"
        role = "USER"

    class Snapshot:
        caller = Caller()

    class ResumeClaim:
        run_id = "run"
        command_type = "resume"
        resume_input = "additional details"
        checkpoint_ref = "rctx1:checkpoint:hash:mac"
        checkpoint_version = 1
        snapshot_hash = "b" * 64

        def snapshot(self):
            return Snapshot()

    class ResumeBackend(Backend):
        def __init__(self):
            super().__init__()
            self.cancelled = []

        async def cancel_root(self, run_id, ctx, reason):
            self.cancelled.append((run_id, reason))

    class Checkpoints:
        async def get_root_context_checkpoint(self, reference):
            assert reference == ResumeClaim.checkpoint_ref
            if isinstance(checkpoint, Exception):
                raise checkpoint
            return checkpoint

    def must_not_dispatch(*_args, **_kwargs):
        raise AssertionError("a rejected resume must not create child work")

    monkeypatch.setattr(
        "app.runtime.orchestrator_supervisor.build_production_root", must_not_dispatch
    )
    backend = ResumeBackend()
    supervisor = RootRuntimeSupervisor(backend, object(), Checkpoints())
    ctx = RequestContext(tenant_id="tenant", user_id="user", role="USER")

    await supervisor._run_claim(ResumeClaim(), ctx)

    assert backend.cancelled == [("run", "Invalid Root resume checkpoint")]


def _fake_checkpoint_store():
    """A PostgreSQL-shaped store whose single row stays inspectable/tamperable."""

    class Cursor:
        async def fetchone(self):
            return connection.row

    class Connection:
        row = None

        async def execute(self, statement, params=None):
            if statement.startswith("INSERT INTO workflow_root_context_checkpoint"):
                ident, canonical, digest = params
                # PostgreSQL JSONB text normalizes whitespace, unlike the submitted bytes.
                self.row = {
                    "payload": json.dumps(json.loads(canonical)),
                    "payload_sha256": digest,
                }
            return Cursor()

    class ConnectionScope:
        async def __aenter__(self):
            return connection

        async def __aexit__(self, *_args):
            return False

    class Pool:
        def connection(self):
            return ConnectionScope()

    connection = Connection()
    store = PostgresCheckpointStore("postgresql://test")
    store._pool = Pool()
    return store, connection


@pytest.mark.asyncio
async def test_root_checkpoint_validates_hmac_and_jsonb_canonical_round_trip():
    store, connection = _fake_checkpoint_store()
    payload = {"root_run_id": "run", "snapshot_hash": "b" * 64, "tenant_id": "tenant", "user_id": "user", "stage": "context", "audit": []}

    reference, version = await store.put_root_context_checkpoint(payload)

    assert version == 1
    assert await store.get_root_context_checkpoint(reference) == payload
    with pytest.raises(ValueError, match="signature"):
        await store.get_root_context_checkpoint(
            reference[:-1] + ("0" if reference[-1] != "0" else "1")
        )
    ident, digest, mac = reference.split(":")[1:]
    for malformed in (
        f"rctx0:{ident}:{digest}:{mac}",
        f"{ident}:{digest}:{mac}",
        f"rctx1:{ident}:{digest}:{mac}:extra",
    ):
        with pytest.raises(ValueError, match="invalid Root context checkpoint reference"):
            await store.get_root_context_checkpoint(malformed)

    connection.row = None
    with pytest.raises(ValueError, match="missing or corrupt"):
        await store.get_root_context_checkpoint(reference)


@pytest.mark.asyncio
async def test_stored_root_checkpoint_payload_tamper_is_rejected():
    """A signed reference is not enough: the stored bytes are rehashed on read."""
    store, connection = _fake_checkpoint_store()
    payload = {"root_run_id": "run", "snapshot_hash": "b" * 64, "tenant_id": "tenant", "user_id": "user", "stage": "context", "audit": []}

    reference, _ = await store.put_root_context_checkpoint(payload)
    tampered = json.loads(connection.row["payload"])
    tampered["tenant_id"] = "other-tenant"
    # The digest column keeps its original value, as a database-level rewrite would.
    connection.row["payload"] = json.dumps(tampered)

    with pytest.raises(ValueError, match="missing or corrupt"):
        await store.get_root_context_checkpoint(reference)


@pytest.mark.asyncio
async def test_recover_once_reclaims_inflight_commands_and_skips_active_keys():
    executed = []

    class RecoveryBackend(Backend):
        def __init__(self, items):
            super().__init__()
            self.items = items

        async def claim_recovery(self):
            return SimpleNamespace(items=self.items, has_more=False)

    item = SimpleNamespace(
        tenant_id="tenant-b",
        user_id="user-b",
        role="ADMIN",
        claim=SimpleNamespace(run_id="run-1", command_id="command-1"),
    )
    supervisor = RootRuntimeSupervisor(RecoveryBackend([item]), object())

    async def record(claim, ctx):
        executed.append((claim.run_id, ctx))

    supervisor._run_claim = record

    await supervisor.recover_once()
    await asyncio.gather(*list(supervisor._tasks.values()), return_exceptions=True)

    assert [run_id for run_id, _ in executed] == ["run-1"]
    ctx = executed[0][1]
    assert (ctx.tenant_id, ctx.user_id, ctx.role) == ("tenant-b", "user-b", "ADMIN")

    # A command already owned by this process must not be claimed twice.
    running = asyncio.create_task(asyncio.sleep(5))
    supervisor._tasks["run-1:command-1"] = running
    await supervisor.recover_once()
    assert len(executed) == 1
    running.cancel()
    await supervisor.close()


@pytest.mark.asyncio
async def test_root_checkpoint_round_trips_through_real_postgres_when_configured():
    dsn = os.getenv("CHECKPOINT_DATABASE_URL")
    if not dsn:
        pytest.skip("CHECKPOINT_DATABASE_URL is absent")
    store = PostgresCheckpointStore(dsn)
    await store.open()
    payload = {"root_run_id": "real-run", "snapshot_hash": "c" * 64, "tenant_id": "tenant", "user_id": "user", "stage": "context", "audit": []}
    reference, _ = await store.put_root_context_checkpoint(payload)
    try:
        assert await store.get_root_context_checkpoint(reference) == payload
    finally:
        async with store._pool.connection() as connection:
            await connection.execute(
                "DELETE FROM workflow_root_context_checkpoint WHERE id=%s",
                (reference.split(":")[1],),
            )
        await store.close()


@pytest.mark.asyncio
async def test_context_round_is_checkpointed_across_restart_and_caps_before_assessment(
    monkeypatch,
):
    class Caller:
        tenant_id = "tenant"
        user_id = "user"
        role = "USER"

    class Limits:
        max_context_rounds = 2
        timeout_seconds = 30

    class Snapshot:
        caller = Caller()
        limits = Limits()

    class Claim:
        def __init__(self, command_type, checkpoint_ref=None, resume_input=None):
            self.run_id = "run"
            self.command_type = command_type
            self.checkpoint_ref = checkpoint_ref
            self.resume_input = resume_input
            self.checkpoint_version = 1 if checkpoint_ref else None
            self.snapshot_hash = "a" * 64

        def snapshot(self):
            return Snapshot()

    class Checkpoints:
        def __init__(self):
            self.values = {}
            self.number = 0

        async def put_root_context_checkpoint(self, value):
            self.number += 1
            reference = f"rctx1:checkpoint-{self.number}:digest:mac"
            self.values[reference] = value
            return reference, 1

        async def get_root_context_checkpoint(self, reference):
            return self.values[reference]

    class Backend:
        def __init__(self):
            self.transitions = []
            self.cancelled = []

        async def complete_dispatch(self, *_args):
            pass

        async def get_root(self, *_args):
            return type("Root", (), {"status": "running"})()

        async def transition_root(self, claim, _ctx, **kwargs):
            self.transitions.append((claim.command_type, kwargs))

        async def cancel_root(self, run_id, _ctx, reason):
            self.cancelled.append((run_id, reason))

    rounds = []

    class Runtime:
        async def execute(self, _snapshot, _context, **kwargs):
            round_number = kwargs["context_round"]
            rounds.append(round_number)
            return RootRunResult(
                status="waiting_input",
                accepted_results=[],
                audit=[{"event_type": "context_assessed", "context_round": round_number}],
                limitations=["context remained insufficient"],
            )

    backend = Backend()
    checkpoints = Checkpoints()
    ctx = RequestContext(tenant_id="tenant", user_id="user", role="USER")
    monkeypatch.setattr(
        "app.runtime.orchestrator_supervisor.build_production_root",
        lambda *_args: Runtime(),
    )

    # New supervisors model a service restart between each durable command.
    await RootRuntimeSupervisor(backend, object(), checkpoints)._run_claim(
        Claim("start"), ctx
    )
    first_ref = backend.transitions[-1][1]["checkpoint_ref"]
    assert checkpoints.values[first_ref]["context_round"] == 1
    assert checkpoints.values[first_ref]["trusted_resume_inputs"] == []

    await RootRuntimeSupervisor(backend, object(), checkpoints)._run_claim(
        Claim("resume", first_ref, "alpha=42"), ctx
    )
    second_ref = backend.transitions[-1][1]["checkpoint_ref"]
    assert checkpoints.values[second_ref]["context_round"] == 2
    assert checkpoints.values[second_ref]["trusted_resume_inputs"] == ["alpha=42"]
    assert rounds == [1, 2]

    await RootRuntimeSupervisor(backend, object(), checkpoints)._run_claim(
        Claim("resume", second_ref, "beta=43"), ctx
    )
    assert rounds == [1, 2]
    assert backend.cancelled == [("run", "Root context round budget exhausted")]
