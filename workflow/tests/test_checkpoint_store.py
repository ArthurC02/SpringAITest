from __future__ import annotations

import asyncio
from datetime import UTC, datetime
from typing import Any

import pytest

import app.runtime.checkpoints as checkpoints


class _Cursor:
    def __init__(self, rows: list[dict[str, Any]] | None = None) -> None:
        self._rows = rows or []

    async def fetchall(self) -> list[dict[str, Any]]:
        return self._rows

    async def fetchone(self) -> dict[str, Any] | None:
        return self._rows[0] if self._rows else None


class _Connection:
    def __init__(self, pool: "_Pool") -> None:
        self._pool = pool

    async def execute(
        self, sql: str, params: tuple[Any, ...] | None = None
    ) -> _Cursor:
        self._pool.executed.append((sql, params))
        if self._pool.fail_sql and self._pool.fail_sql in sql:
            self._pool.fail_sql = None
            error = self._pool.fail_exception or RuntimeError("database setup failed")
            self._pool.fail_exception = None
            raise error
        if "pg_advisory_unlock" in sql and self._pool.block_unlock:
            self._pool.unlock_started.set()
            await self._pool.unlock_release.wait()
        if "pg_advisory_unlock" in sql and self._pool.unlock_exception is not None:
            error = self._pool.unlock_exception
            self._pool.unlock_exception = None
            raise error
        rows = []
        if "pg_try_advisory_lock" in sql:
            rows = [{"acquired": True}]
        elif "pg_advisory_unlock" in sql:
            rows = [{"unlocked": True}]
        elif sql == checkpoints._CHECKPOINT_SUMMARY_SQL:
            rows = self._pool.inventory_rows
        elif sql == checkpoints._CHECKPOINT_THREAD_SQL:
            rows = self._pool.thread_rows
        elif sql == checkpoints._CHECKPOINT_REFERENCE_SQL:
            rows = self._pool.reference_rows
        return _Cursor(rows)

    def transaction(self) -> "_TransactionContext":
        return _TransactionContext()


class _TransactionContext:
    async def __aenter__(self) -> None:
        return None

    async def __aexit__(self, *_: object) -> None:
        return None


class _ConnectionContext:
    def __init__(self, pool: "_Pool") -> None:
        self._connection = _Connection(pool)

    async def __aenter__(self) -> _Connection:
        return self._connection

    async def __aexit__(self, *_: object) -> None:
        return None


class _Pool:
    instances: list["_Pool"] = []

    def __init__(self, *_: object, **__: object) -> None:
        self.executed: list[tuple[str, tuple[Any, ...] | None]] = []
        self.inventory_rows: list[dict[str, Any]] = []
        self.thread_rows: list[dict[str, Any]] = []
        self.reference_rows: list[dict[str, Any]] = []
        self.fail_sql: str | None = None
        self.fail_exception: BaseException | None = None
        self.open_calls = 0
        self.wait_calls = 0
        self.close_calls = 0
        self.block_unlock = False
        self.unlock_started = asyncio.Event()
        self.unlock_release = asyncio.Event()
        self.unlock_exception: BaseException | None = None
        self.instances.append(self)

    async def open(self) -> None:
        self.open_calls += 1
        await asyncio.sleep(0)

    async def wait(self) -> None:
        self.wait_calls += 1

    async def close(self) -> None:
        self.close_calls += 1

    def connection(self) -> _ConnectionContext:
        return _ConnectionContext(self)


class _Saver:
    instances: list["_Saver"] = []

    def __init__(self, *, conn: _Pool, serde: object) -> None:
        self.conn = conn
        self.serde = serde
        self.setup_calls = 0
        self.instances.append(self)

    async def setup(self) -> None:
        self.setup_calls += 1


@pytest.fixture(autouse=True)
def _fake_postgres(monkeypatch: pytest.MonkeyPatch) -> None:
    _Pool.instances.clear()
    _Saver.instances.clear()
    monkeypatch.setattr(checkpoints, "AsyncConnectionPool", _Pool)
    monkeypatch.setattr(checkpoints, "AsyncPostgresSaver", _Saver)


@pytest.mark.asyncio
async def test_open_serializes_setup_and_reopen_is_idempotent() -> None:
    store = checkpoints.PostgresCheckpointStore("postgresql://unused")

    first, concurrent = await asyncio.gather(store.open(), store.open())

    assert first is concurrent
    assert len(_Pool.instances) == 1
    assert len(_Saver.instances) == 1
    assert _Saver.instances[0].setup_calls == 1
    assert checkpoints._ROOT_CONTEXT_SCHEMA_SQL in {
        sql for sql, _ in _Pool.instances[0].executed
    }

    await store.close()
    reopened = await store.open()

    assert reopened is not first
    assert len(_Pool.instances) == 2
    assert _Saver.instances[1].setup_calls == 1
    assert checkpoints._ROOT_CONTEXT_SCHEMA_SQL in {
        sql for sql, _ in _Pool.instances[1].executed
    }
    await store.close()


@pytest.mark.asyncio
async def test_put_executes_insert_without_ddl() -> None:
    store = checkpoints.PostgresCheckpointStore("postgresql://unused")
    await store.open()
    pool = _Pool.instances[0]
    pool.executed.clear()

    reference, version = await store.put_root_context_checkpoint({"scope": "root"})

    assert reference.startswith("rctx1:")
    assert version == 1
    assert len(pool.executed) == 1
    sql, params = pool.executed[0]
    assert sql.lstrip().startswith("INSERT INTO workflow_root_context_checkpoint")
    assert "CREATE" not in sql.upper()
    assert params is not None
    await store.close()


@pytest.mark.asyncio
@pytest.mark.parametrize("failed_sql", ["pg_try_advisory_lock", "CREATE TABLE"])
async def test_open_failure_closes_private_pool_and_reopen_retries(
    failed_sql: str,
) -> None:
    store = checkpoints.PostgresCheckpointStore("postgresql://unused")
    original = checkpoints.AsyncConnectionPool

    class _FailFirstPool(_Pool):
        attempts = 0

        def __init__(self, *args: object, **kwargs: object) -> None:
            super().__init__(*args, **kwargs)
            type(self).attempts += 1
            if type(self).attempts == 1:
                self.fail_sql = failed_sql

    checkpoints.AsyncConnectionPool = _FailFirstPool
    try:
        with pytest.raises(RuntimeError, match="database setup failed"):
            await store.open()
        assert store.saver is None
        assert store._pool is None
        assert _Pool.instances[0].close_calls == 1

        saver = await store.open()
        assert saver is store.saver
    finally:
        checkpoints.AsyncConnectionPool = original
        await store.close()


@pytest.mark.asyncio
async def test_saver_setup_failure_closes_pool_and_reopen_retries(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    store = checkpoints.PostgresCheckpointStore("postgresql://unused")

    class _FailFirstSaver(_Saver):
        attempts = 0

        async def setup(self) -> None:
            type(self).attempts += 1
            if type(self).attempts == 1:
                raise RuntimeError("saver setup failed")
            await super().setup()

    monkeypatch.setattr(checkpoints, "AsyncPostgresSaver", _FailFirstSaver)
    with pytest.raises(RuntimeError, match="saver setup failed"):
        await store.open()
    assert store.saver is None
    assert store._pool is None
    assert _Pool.instances[0].close_calls == 1

    assert await store.open() is store.saver
    await store.close()


@pytest.mark.asyncio
async def test_cancel_during_unlock_waits_for_unlock_before_releasing_connection(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    class _BlockingUnlockPool(_Pool):
        def __init__(self, *args: object, **kwargs: object) -> None:
            super().__init__(*args, **kwargs)
            self.block_unlock = True

    monkeypatch.setattr(checkpoints, "AsyncConnectionPool", _BlockingUnlockPool)
    store = checkpoints.PostgresCheckpointStore("postgresql://unused")
    opening = asyncio.create_task(store.open())
    while not _Pool.instances:
        await asyncio.sleep(0)
    pool = _Pool.instances[0]
    await pool.unlock_started.wait()

    opening.cancel()
    await asyncio.sleep(0)
    assert not opening.done()
    assert pool.close_calls == 0

    pool.unlock_release.set()
    with pytest.raises(asyncio.CancelledError):
        await opening
    assert pool.close_calls == 1
    assert store._pool is None
    assert store.saver is None


@pytest.mark.asyncio
async def test_unlock_exception_closes_unpublished_pool_and_reopen_retries(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    class _FailFirstUnlockPool(_Pool):
        attempts = 0

        def __init__(self, *args: object, **kwargs: object) -> None:
            super().__init__(*args, **kwargs)
            type(self).attempts += 1
            if type(self).attempts == 1:
                self.unlock_exception = RuntimeError("unlock failed")

    monkeypatch.setattr(checkpoints, "AsyncConnectionPool", _FailFirstUnlockPool)
    store = checkpoints.PostgresCheckpointStore("postgresql://unused")
    with pytest.raises(RuntimeError, match="unlock failed"):
        await store.open()
    assert _Pool.instances[0].close_calls == 1
    assert store._pool is None
    assert store.saver is None

    assert await store.open() is store.saver
    await store.close()


@pytest.mark.asyncio
async def test_inventory_is_read_only_and_never_infers_terminal_eligibility() -> None:
    store = checkpoints.PostgresCheckpointStore("postgresql://unused")
    await store.open()
    pool = _Pool.instances[0]
    oldest = datetime(2026, 1, 1, tzinfo=UTC)
    pool.inventory_rows = [
        {
            "scope": "table",
            "name": "checkpoints",
            "rows": 4,
            "logical_row_bytes_estimate": 512,
            "oldest_at": oldest,
            "age_basis": "checkpoint_ts",
        },
    ]
    pool.thread_rows = [
        {
            "scope": "thread",
            "name": "thread-hash",
            "rows": 3,
            "logical_row_bytes_estimate": 384,
            "oldest_at": oldest,
            "age_basis": "checkpoint_ts",
        },
    ]
    pool.reference_rows = [
        {
            "scope": "reference",
            "name": "root-context:00000000-0000-0000-0000-000000000001",
            "rows": 1,
            "logical_row_bytes_estimate": 128,
            "oldest_at": oldest,
            "age_basis": "created_at",
        },
    ]
    pool.executed.clear()

    summary = await store.checkpoint_inventory()
    assert {item["scope"] for item in summary["items"]} == {"table"}
    assert summary["threads"]["included"] is False
    assert summary["references"]["included"] is False

    report = await store.checkpoint_inventory(
        include_threads=True, include_references=True
    )

    assert report["terminal_authority"] == "unavailable"
    assert {item["scope"] for item in report["items"]} == {
        "table",
        "thread",
        "reference",
    }
    assert all(item["classification"] == "unclassified" for item in report["items"])
    assert all("eligible" not in item for item in report["items"])
    assert all(item["oldest_at"] == oldest for item in report["items"])
    assert report["measurement"] == {
        "field": "logical_row_bytes_estimate",
        "reclaimable": False,
        "includes_indexes": False,
        "includes_bloat": False,
        "shared_blobs_may_repeat_across_references": True,
    }
    for sql, _ in pool.executed:
        upper = sql.upper()
        assert "DELETE" not in upper
        assert "UPDATE" not in upper
        assert "INSERT" not in upper
    await store.close()


@pytest.mark.asyncio
async def test_inventory_reference_page_is_bounded_and_timeout_is_safe() -> None:
    store = checkpoints.PostgresCheckpointStore("postgresql://unused")
    await store.open()
    pool = _Pool.instances[0]
    pool.reference_rows = [
        {
            "scope": "reference",
            "name": f"reference-{index}",
            "rows": 1,
            "logical_row_bytes_estimate": 10,
            "oldest_at": None,
            "age_basis": "unavailable",
        }
        for index in range(2)
    ]
    pool.thread_rows = [
        {
            "scope": "thread",
            "name": f"thread-{index}",
            "rows": 1,
            "logical_row_bytes_estimate": 10,
            "oldest_at": None,
            "age_basis": "checkpoint_ts",
        }
        for index in range(2)
    ]

    report = await store.checkpoint_inventory(
        include_threads=True,
        thread_cursor="thread-0",
        thread_limit=1,
        include_references=True,
        reference_cursor="reference-0",
        reference_limit=1,
    )
    assert len([item for item in report["items"] if item["scope"] == "thread"]) == 1
    assert len([item for item in report["items"] if item["scope"] == "reference"]) == 1
    assert report["threads"]["next_cursor"] == "thread-0"
    assert report["references"]["next_cursor"] == "reference-0"
    reference_call = next(
        call for call in pool.executed if call[0] == checkpoints._CHECKPOINT_REFERENCE_SQL
    )
    assert reference_call[1] == ("reference-0", "reference-0", 2)
    thread_call = next(
        call for call in pool.executed if call[0] == checkpoints._CHECKPOINT_THREAD_SQL
    )
    assert thread_call[1] == ("thread-0", "thread-0", 2)

    with pytest.raises(ValueError, match="between 1 and 100"):
        await store.checkpoint_inventory(include_references=True, reference_limit=101)
    with pytest.raises(ValueError, match="between 1 and 100"):
        await store.checkpoint_inventory(include_threads=True, thread_limit=101)

    pool.fail_sql = checkpoints._CHECKPOINT_SUMMARY_SQL
    pool.fail_exception = checkpoints.QueryCanceled("statement timeout")
    with pytest.raises(RuntimeError, match="inventory query timed out"):
        await store.checkpoint_inventory()
    await store.close()
