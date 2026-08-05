from __future__ import annotations

import asyncio
import hashlib
import hmac
import sys
import re
import uuid
import json
from collections import defaultdict
from typing import Any

from langgraph.checkpoint.base import BaseCheckpointSaver
from langgraph.checkpoint.postgres.aio import AsyncPostgresSaver
from langgraph.checkpoint.serde.jsonplus import JsonPlusSerializer
from psycopg.rows import dict_row
from psycopg.errors import QueryCanceled
from psycopg_pool import AsyncConnectionPool

from app.settings import settings

_THREAD_RE = re.compile(r"^[0-9a-f]{64}$")
# Workflow checkpoint DB lock namespace. Deliberately distinct from Backend's
# migration/bootstrap keys (823746292/823746291), even if operators point both
# services at one PostgreSQL database during local development.
_CHECKPOINT_SCHEMA_ADVISORY_LOCK_ID = 6_293_895_194_437_968_196
_CHECKPOINT_SCHEMA_LOCK_TIMEOUT_MS = 30_000
_CHECKPOINT_INVENTORY_TIMEOUT_MS = 10_000
_CHECKPOINT_REFERENCE_PAGE_MAX = 100
_ROOT_CONTEXT_SCHEMA_SQL = """
CREATE TABLE IF NOT EXISTS workflow_root_context_checkpoint (
    id uuid PRIMARY KEY,
    payload jsonb NOT NULL,
    payload_sha256 text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
)
"""
_CHECKPOINT_SUMMARY_SQL = """
WITH checkpoint_base AS (
    SELECT
        thread_id,
        checkpoint_ns,
        checkpoint_id,
        checkpoint,
        pg_column_size(c) AS row_bytes,
        (checkpoint ->> 'ts')::timestamptz AS created_at
    FROM checkpoints AS c
),
table_inventory AS (
    SELECT 'checkpoint_migrations' AS name, count(*) AS rows,
           coalesce(sum(pg_column_size(m)), 0) AS logical_row_bytes_estimate,
           NULL::timestamptz AS oldest_at, 'unavailable' AS age_basis
    FROM checkpoint_migrations AS m
    UNION ALL
    SELECT 'checkpoints', count(*), coalesce(sum(row_bytes), 0), min(created_at),
           'checkpoint_ts'
    FROM checkpoint_base
    UNION ALL
    SELECT 'checkpoint_blobs', count(*), coalesce(sum(pg_column_size(b)), 0),
           NULL::timestamptz, 'unavailable'
    FROM checkpoint_blobs AS b
    UNION ALL
    SELECT 'checkpoint_writes', count(*), coalesce(sum(pg_column_size(w)), 0),
           min(c.created_at), 'checkpoint_ts'
    FROM checkpoint_writes AS w
    LEFT JOIN checkpoint_base AS c
      ON c.thread_id = w.thread_id
     AND c.checkpoint_ns = w.checkpoint_ns
     AND c.checkpoint_id = w.checkpoint_id
    UNION ALL
    SELECT 'workflow_root_context_checkpoint', count(*),
           coalesce(sum(pg_column_size(r)), 0), min(created_at), 'created_at'
    FROM workflow_root_context_checkpoint AS r
)
SELECT 'table' AS scope, name, rows, logical_row_bytes_estimate, oldest_at,
       age_basis FROM table_inventory
ORDER BY name
"""
_CHECKPOINT_THREAD_SQL = """
WITH checkpoint_base AS (
    SELECT thread_id, pg_column_size(c) AS row_bytes,
           (checkpoint ->> 'ts')::timestamptz AS created_at
    FROM checkpoints AS c
),
thread_rows AS (
    SELECT thread_id, count(*) AS rows,
           coalesce(sum(row_bytes), 0) AS logical_row_bytes_estimate,
           min(created_at) AS oldest_at
    FROM checkpoint_base
    GROUP BY thread_id
    UNION ALL
    SELECT thread_id, count(*), coalesce(sum(pg_column_size(b)), 0), NULL::timestamptz
    FROM checkpoint_blobs AS b
    GROUP BY thread_id
    UNION ALL
    SELECT thread_id, count(*), coalesce(sum(pg_column_size(w)), 0), NULL::timestamptz
    FROM checkpoint_writes AS w
    GROUP BY thread_id
),
thread_inventory AS (
    SELECT thread_id AS name, sum(rows) AS rows,
           sum(logical_row_bytes_estimate) AS logical_row_bytes_estimate,
           min(oldest_at) AS oldest_at, 'checkpoint_ts' AS age_basis
    FROM thread_rows
    GROUP BY thread_id
)
SELECT 'thread' AS scope, name, rows, logical_row_bytes_estimate, oldest_at,
       age_basis FROM thread_inventory
WHERE (%s::text IS NULL OR name > %s::text)
ORDER BY name
LIMIT %s
"""
_CHECKPOINT_REFERENCE_SQL = """
WITH checkpoint_base AS (
    SELECT thread_id, checkpoint_ns, checkpoint_id, checkpoint,
           pg_column_size(c) AS row_bytes,
           (checkpoint ->> 'ts')::timestamptz AS created_at
    FROM checkpoints AS c
),
reference_inventory AS (
    SELECT
        'langgraph:' || c.thread_id || ':' || c.checkpoint_ns || ':' || c.checkpoint_id AS name,
        1
            + (SELECT count(*) FROM checkpoint_writes AS w
               WHERE w.thread_id = c.thread_id
                 AND w.checkpoint_ns = c.checkpoint_ns
                 AND w.checkpoint_id = c.checkpoint_id)
            + (SELECT count(*)
               FROM jsonb_each_text(c.checkpoint -> 'channel_versions') AS cv
               JOIN checkpoint_blobs AS b
                 ON b.thread_id = c.thread_id
                AND b.checkpoint_ns = c.checkpoint_ns
                AND b.channel = cv.key
                AND b.version = cv.value) AS rows,
        c.row_bytes
            + (SELECT coalesce(sum(pg_column_size(w)), 0)
               FROM checkpoint_writes AS w
               WHERE w.thread_id = c.thread_id
                 AND w.checkpoint_ns = c.checkpoint_ns
                 AND w.checkpoint_id = c.checkpoint_id)
            + (SELECT coalesce(sum(pg_column_size(b)), 0)
               FROM jsonb_each_text(c.checkpoint -> 'channel_versions') AS cv
               JOIN checkpoint_blobs AS b
                 ON b.thread_id = c.thread_id
                AND b.checkpoint_ns = c.checkpoint_ns
                AND b.channel = cv.key
                AND b.version = cv.value) AS logical_row_bytes_estimate,
        c.created_at AS oldest_at,
        'checkpoint_ts' AS age_basis
    FROM checkpoint_base AS c
    UNION ALL
    SELECT
        'root-context:' || id::text,
        1::bigint,
        pg_column_size(r)::bigint AS logical_row_bytes_estimate,
        created_at,
        'created_at' AS age_basis
    FROM workflow_root_context_checkpoint AS r
)
SELECT 'reference' AS scope, name, rows, logical_row_bytes_estimate, oldest_at,
       age_basis
FROM reference_inventory
WHERE (%s::text IS NULL OR name > %s::text)
ORDER BY name
LIMIT %s
"""


def install_windows_selector_event_loop_policy() -> bool:
    """psycopg async requires SelectorEventLoop on Windows.

    This is called while modules are imported, before uvicorn/pytest creates an
    event loop. Linux and macOS are untouched.
    """

    if sys.platform != "win32":
        return False
    selector = getattr(asyncio, "WindowsSelectorEventLoopPolicy", None)
    proactor = getattr(asyncio, "WindowsProactorEventLoopPolicy", None)
    if selector is None:
        return False
    current = asyncio.get_event_loop_policy()
    if proactor is not None and isinstance(current, proactor):
        asyncio.set_event_loop_policy(selector())
        return True
    return isinstance(current, selector)


# Runtime modules are imported before the ASGI server creates its loop. Doing
# this unconditionally on Windows is safe for the existing async HTTP stack and
# guarantees live Postgres checkpoint tests use psycopg's supported loop.
install_windows_selector_event_loop_policy()


def strict_serializer() -> JsonPlusSerializer:
    """No pickle and no arbitrary module reconstruction."""

    return JsonPlusSerializer(
        pickle_fallback=False,
        allowed_json_modules=None,
        allowed_msgpack_modules=None,
    )


def checkpoint_thread_id(
    *, tenant_id: str, user_id: str, run_id: str, snapshot_hash: str
) -> str:
    raw = "\0".join((tenant_id, user_id, run_id, snapshot_hash)).encode("utf-8")
    return hmac.new(
        settings.checkpoint_hmac_key.encode("utf-8"), raw, hashlib.sha256
    ).hexdigest()


def checkpoint_config(
    *,
    tenant_id: str,
    user_id: str,
    run_id: str,
    snapshot_hash: str,
    lease_generation: int | None = None,
) -> dict[str, Any]:
    base_thread_id = checkpoint_thread_id(
            tenant_id=tenant_id,
            user_id=user_id,
            run_id=run_id,
            snapshot_hash=snapshot_hash,
        )
    configurable: dict[str, Any] = {"thread_id": base_thread_id}
    if lease_generation is not None:
        if lease_generation < 1:
            raise ValueError("lease generation must be positive")
        configurable["thread_id"] = hashlib.sha256(
            f"{base_thread_id}:g:{lease_generation}".encode("ascii")
        ).hexdigest()
        configurable["lease_generation"] = lease_generation
    configurable["checkpoint_ns"] = ""
    return {"configurable": configurable}


def checkpoint_ref(
    config: dict[str, Any], *, lease_generation: int | None = None
) -> str | None:
    configurable = config.get("configurable") or {}
    checkpoint_id = configurable.get("checkpoint_id")
    thread_id = configurable.get("thread_id")
    if not checkpoint_id or not thread_id:
        return None
    generation = lease_generation or configurable.get("lease_generation")
    if generation is not None:
        if not isinstance(generation, int) or generation < 1:
            return None
        try:
            canonical_id = str(uuid.UUID(str(checkpoint_id)))
        except ValueError:
            return None
        if canonical_id != checkpoint_id or not _THREAD_RE.fullmatch(str(thread_id)):
            return None
        return f"v2:{generation}:{thread_id}:{canonical_id}"
    return f"{thread_id}:{checkpoint_id}"


def config_from_checkpoint_ref(
    value: str,
    *,
    expected_thread_id: str | None = None,
    expected_generation: int | None = None,
) -> dict[str, Any]:
    parts = value.split(":")
    if len(parts) != 4 or parts[0] != "v2":
        raise ValueError("checkpoint ref is not canonical v2")
    generation_text, thread_id, checkpoint_id = parts[1:]
    if (
        not generation_text.isdigit()
        or generation_text.startswith("0")
        or not _THREAD_RE.fullmatch(thread_id)
    ):
        raise ValueError("checkpoint ref has invalid fencing identity")
    try:
        canonical_id = str(uuid.UUID(checkpoint_id))
    except ValueError as exc:
        raise ValueError("checkpoint ref has invalid checkpoint UUID") from exc
    if canonical_id != checkpoint_id:
        raise ValueError("checkpoint ref checkpoint UUID is not canonical")
    if expected_thread_id is not None and thread_id != expected_thread_id:
        raise ValueError("checkpoint ref thread identity does not match authority")
    if expected_generation is not None and int(generation_text) != expected_generation:
        raise ValueError("checkpoint ref generation does not match authority")
    return {
        "configurable": {
            "thread_id": thread_id,
            "checkpoint_ns": "",
            "lease_generation": int(generation_text),
            "checkpoint_id": checkpoint_id,
        }
    }


async def clone_checkpoint_generation(
    saver: BaseCheckpointSaver[str],
    *,
    source_ref: str,
    target_config: dict[str, Any],
) -> dict[str, Any]:
    source_config = config_from_checkpoint_ref(source_ref)
    source = await saver.aget_tuple(source_config)
    if source is None:
        raise ValueError("checkpoint seed ref does not exist")
    target_configurable = target_config.get("configurable") or {}
    if (
        target_configurable.get("checkpoint_ns", "") != ""
        or not _THREAD_RE.fullmatch(str(target_configurable.get("thread_id") or ""))
        or not isinstance(target_configurable.get("lease_generation"), int)
        or target_configurable["lease_generation"] < 1
    ):
        raise ValueError("target generation config is invalid")
    metadata = dict(source.metadata)
    metadata["logical_seed_ref"] = source_ref
    metadata["parents"] = {}
    cloned_config = await saver.aput(
        target_config,
        source.checkpoint,
        metadata,
        source.checkpoint.get("channel_versions") or {},
    )
    pending_by_task: dict[str, list[tuple[str, Any]]] = defaultdict(list)
    for pending in source.pending_writes or []:
        task_id, channel, value = pending
        pending_by_task[str(task_id)].append((str(channel), value))
    for task_id, writes in pending_by_task.items():
        await saver.aput_writes(cloned_config, writes, task_id)
    cloned = await saver.aget_tuple(cloned_config)
    if (
        cloned is None
        or cloned.parent_config is not None
        or cloned.checkpoint != source.checkpoint
        or (cloned.pending_writes or []) != (source.pending_writes or [])
    ):
        raise ValueError("checkpoint generation clone verification failed")
    cloned_config = {
        "configurable": {
            **(cloned.config.get("configurable") or {}),
            "checkpoint_ns": "",
            "lease_generation": target_configurable["lease_generation"],
        }
    }
    return cloned_config


async def _acquire_schema_lock(conn: Any) -> None:
    try:
        async with asyncio.timeout(_CHECKPOINT_SCHEMA_LOCK_TIMEOUT_MS / 1_000):
            while True:
                cursor = await conn.execute(
                    "SELECT pg_try_advisory_lock(%s) AS acquired",
                    (_CHECKPOINT_SCHEMA_ADVISORY_LOCK_ID,),
                )
                row = await cursor.fetchone()
                if row and row["acquired"]:
                    return
                await asyncio.sleep(0.05)
    except TimeoutError as exc:
        raise RuntimeError("checkpoint schema advisory lock timed out") from exc


async def _release_schema_lock(conn: Any) -> None:
    async def unlock() -> None:
        cursor = await conn.execute(
            "SELECT pg_advisory_unlock(%s) AS unlocked",
            (_CHECKPOINT_SCHEMA_ADVISORY_LOCK_ID,),
        )
        row = await cursor.fetchone()
        if not row or not row["unlocked"]:
            raise RuntimeError("checkpoint schema advisory lock was not owned")

    unlock_task = asyncio.create_task(unlock())
    try:
        await asyncio.shield(unlock_task)
    except asyncio.CancelledError:
        # The pool connection must not be returned while its session lock may
        # still be held. Consume any unlock failure, then preserve cancellation.
        await asyncio.gather(unlock_task, return_exceptions=True)
        raise


class PostgresCheckpointStore:
    """Own the process-wide pool used by LangGraph's durable saver."""

    def __init__(self, dsn: str):
        if not dsn.strip():
            raise ValueError("checkpoint DSN must not be blank")
        self._dsn = dsn
        self._pool: AsyncConnectionPool[Any] | None = None
        self.saver: AsyncPostgresSaver | None = None
        self._open_lock = asyncio.Lock()

    async def open(self) -> AsyncPostgresSaver:
        async with self._open_lock:
            if self.saver is not None:
                return self.saver
            pool = AsyncConnectionPool(
                self._dsn,
                kwargs={
                    "autocommit": True,
                    "prepare_threshold": 0,
                    "row_factory": dict_row,
                },
                min_size=1,
                max_size=10,
                open=False,
            )
            try:
                await pool.open()
                await pool.wait()
                saver = AsyncPostgresSaver(
                    conn=pool,
                    serde=strict_serializer(),
                )
                async with pool.connection() as conn:
                    await _acquire_schema_lock(conn)
                    try:
                        # Keep the session lock on this checked-out connection.
                        # A transaction lock would deadlock LangGraph's CREATE
                        # INDEX CONCURRENTLY, which waits for older transactions.
                        await saver.setup()
                        await conn.execute(_ROOT_CONTEXT_SCHEMA_SQL)
                    finally:
                        await _release_schema_lock(conn)
            except BaseException:
                await pool.close()
                raise
            self._pool = pool
            self.saver = saver
            return saver

    async def close(self) -> None:
        async with self._open_lock:
            if self._pool is not None:
                await self._pool.close()
            self._pool = None
            self.saver = None

    async def put_root_context_checkpoint(self, payload: dict[str, Any]) -> tuple[str, int]:
        """Persist the pre-dispatch Root context gate state in Workflow's checkpoint DB.

        The opaque reference is HMAC-bound to its canonical bytes; Backend stores only
        that reference and is never trusted to manufacture or alter checkpoint state.
        """
        if self._pool is None:
            raise RuntimeError("root checkpoint store is not open")
        canonical = json.dumps(payload, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
        digest = hashlib.sha256(canonical.encode()).hexdigest()
        ident = str(uuid.uuid4())
        mac = hmac.new(settings.checkpoint_hmac_key.encode(), f"{ident}:{digest}".encode(), hashlib.sha256).hexdigest()
        async with self._pool.connection() as conn:
            await conn.execute("INSERT INTO workflow_root_context_checkpoint(id,payload,payload_sha256) VALUES(%s,%s::jsonb,%s)", (ident, canonical, digest))
        return f"rctx1:{ident}:{digest}:{mac}", 1

    async def checkpoint_inventory(
        self,
        *,
        include_threads: bool = False,
        thread_cursor: str | None = None,
        thread_limit: int = _CHECKPOINT_REFERENCE_PAGE_MAX,
        include_references: bool = False,
        reference_cursor: str | None = None,
        reference_limit: int = _CHECKPOINT_REFERENCE_PAGE_MAX,
    ) -> dict[str, Any]:
        """Report storage usage without inferring retention or terminal status.

        Workflow has no authoritative Backend run-terminal view here. Age is
        therefore inventory evidence only; every entry remains unclassified.
        """
        if self._pool is None:
            raise RuntimeError("root checkpoint store is not open")
        if not 1 <= thread_limit <= _CHECKPOINT_REFERENCE_PAGE_MAX:
            raise ValueError(
                f"thread_limit must be between 1 and {_CHECKPOINT_REFERENCE_PAGE_MAX}"
            )
        if not 1 <= reference_limit <= _CHECKPOINT_REFERENCE_PAGE_MAX:
            raise ValueError(
                f"reference_limit must be between 1 and {_CHECKPOINT_REFERENCE_PAGE_MAX}"
            )
        try:
            async with self._pool.connection() as conn:
                async with conn.transaction():
                    await conn.execute(
                        "SELECT set_config('statement_timeout', %s, true)",
                        (f"{_CHECKPOINT_INVENTORY_TIMEOUT_MS}ms",),
                    )
                    cursor = await conn.execute(_CHECKPOINT_SUMMARY_SQL)
                    rows = await cursor.fetchall()
                    thread_rows: list[dict[str, Any]] = []
                    if include_threads:
                        cursor = await conn.execute(
                            _CHECKPOINT_THREAD_SQL,
                            (thread_cursor, thread_cursor, thread_limit + 1),
                        )
                        thread_rows = await cursor.fetchall()
                    reference_rows: list[dict[str, Any]] = []
                    if include_references:
                        cursor = await conn.execute(
                            _CHECKPOINT_REFERENCE_SQL,
                            (
                                reference_cursor,
                                reference_cursor,
                                reference_limit + 1,
                            ),
                        )
                        reference_rows = await cursor.fetchall()
        except QueryCanceled as exc:
            raise RuntimeError("checkpoint inventory query timed out") from exc
        thread_has_more = len(thread_rows) > thread_limit
        thread_page = thread_rows[:thread_limit]
        reference_has_more = len(reference_rows) > reference_limit
        reference_page = reference_rows[:reference_limit]
        rows = [*rows, *thread_page, *reference_page]
        return {
            "terminal_authority": "unavailable",
            "measurement": {
                "field": "logical_row_bytes_estimate",
                "reclaimable": False,
                "includes_indexes": False,
                "includes_bloat": False,
                "shared_blobs_may_repeat_across_references": True,
            },
            "threads": {
                "included": include_threads,
                "limit": thread_limit,
                "next_cursor": (
                    thread_page[-1]["name"] if thread_has_more else None
                ),
            },
            "references": {
                "included": include_references,
                "limit": reference_limit,
                "next_cursor": (
                    reference_page[-1]["name"] if reference_has_more else None
                ),
            },
            "items": [
                {
                    "scope": row["scope"],
                    "name": row["name"],
                    "rows": row["rows"],
                    "logical_row_bytes_estimate": row[
                        "logical_row_bytes_estimate"
                    ],
                    "oldest_at": row["oldest_at"],
                    "age_basis": row["age_basis"],
                    "classification": "unclassified",
                }
                for row in rows
            ],
        }

    async def delete_root_contexts(self, root_run_id: str) -> int:
        """Delete Workflow-owned Root rows for one Backend-authorized terminal root."""
        if self._pool is None:
            raise RuntimeError("root checkpoint store is not open")
        canonical_run_id = str(uuid.UUID(root_run_id))
        async with self._pool.connection() as conn:
            cursor = await conn.execute(
                """
                DELETE FROM workflow_root_context_checkpoint
                WHERE payload ->> 'root_run_id' = %s
                RETURNING id
                """,
                (canonical_run_id,),
            )
            return len(await cursor.fetchall())

    async def thread_storage_counts(self, thread_id: str) -> dict[str, int]:
        """Read-only dangling-row evidence after official saver deletion."""
        if self._pool is None:
            raise RuntimeError("root checkpoint store is not open")
        if not _THREAD_RE.fullmatch(thread_id):
            raise ValueError("checkpoint thread identity is invalid")
        async with self._pool.connection() as conn:
            cursor = await conn.execute(
                """
                SELECT
                  (SELECT count(*) FROM checkpoints WHERE thread_id=%s) AS checkpoints,
                  (SELECT count(*) FROM checkpoint_blobs WHERE thread_id=%s) AS blobs,
                  (SELECT count(*) FROM checkpoint_writes WHERE thread_id=%s) AS writes
                """,
                (thread_id, thread_id, thread_id),
            )
            row = await cursor.fetchone()
        return {
            "checkpoints": row["checkpoints"],
            "blobs": row["blobs"],
            "writes": row["writes"],
        }

    async def root_context_count(self, root_run_id: str) -> int:
        if self._pool is None:
            raise RuntimeError("root checkpoint store is not open")
        canonical_run_id = str(uuid.UUID(root_run_id))
        async with self._pool.connection() as conn:
            cursor = await conn.execute(
                """
                SELECT count(*) AS count
                FROM workflow_root_context_checkpoint
                WHERE payload ->> 'root_run_id' = %s
                """,
                (canonical_run_id,),
            )
            row = await cursor.fetchone()
        return row["count"]

    async def get_root_context_checkpoint(self, reference: str) -> dict[str, Any]:
        if self._pool is None:
            raise RuntimeError("root checkpoint store is not open")
        parts = reference.split(":")
        if len(parts) != 4 or parts[0] != "rctx1":
            raise ValueError("invalid Root context checkpoint reference")
        ident, digest, mac = parts[1:]
        expected = hmac.new(settings.checkpoint_hmac_key.encode(), f"{ident}:{digest}".encode(), hashlib.sha256).hexdigest()
        if not hmac.compare_digest(expected, mac):
            raise ValueError("Root context checkpoint signature is invalid")
        async with self._pool.connection() as conn:
            cursor = await conn.execute("SELECT payload::text,payload_sha256 FROM workflow_root_context_checkpoint WHERE id=%s", (ident,))
            row = await cursor.fetchone()
        if row is None or row["payload_sha256"] != digest:
            raise ValueError("Root context checkpoint is missing or corrupt")
        payload = json.loads(row["payload"])
        canonical = json.dumps(payload, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
        if hashlib.sha256(canonical.encode()).hexdigest() != digest:
            raise ValueError("Root context checkpoint is missing or corrupt")
        return payload


def require_runtime_checkpoint_dsn() -> str:
    dsn = settings.checkpoint_database_url
    if not dsn or not dsn.strip():
        raise RuntimeError(
            "AGENT_TEST_RUN_ENABLED requires CHECKPOINT_DATABASE_URL; "
            "in-memory fallback is not permitted"
        )
    return dsn


def ensure_checkpointer(value: BaseCheckpointSaver[str] | None) -> BaseCheckpointSaver[str]:
    if value is None:
        raise RuntimeError("direct-Agent graph requires a durable checkpointer")
    return value
