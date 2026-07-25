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
from psycopg_pool import AsyncConnectionPool

from app.settings import settings

_THREAD_RE = re.compile(r"^[0-9a-f]{64}$")


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


class PostgresCheckpointStore:
    """Own the process-wide pool used by LangGraph's durable saver."""

    def __init__(self, dsn: str):
        if not dsn.strip():
            raise ValueError("checkpoint DSN must not be blank")
        self._dsn = dsn
        self._pool: AsyncConnectionPool[Any] | None = None
        self.saver: AsyncPostgresSaver | None = None

    async def open(self) -> AsyncPostgresSaver:
        if self.saver is not None:
            return self.saver
        self._pool = AsyncConnectionPool(
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
        await self._pool.open()
        await self._pool.wait()
        self.saver = AsyncPostgresSaver(
            conn=self._pool,
            serde=strict_serializer(),
        )
        await self.saver.setup()
        return self.saver

    async def close(self) -> None:
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
            await conn.execute("CREATE TABLE IF NOT EXISTS workflow_root_context_checkpoint (id uuid PRIMARY KEY, payload jsonb NOT NULL, payload_sha256 text NOT NULL, created_at timestamptz NOT NULL DEFAULT now())")
            await conn.execute("INSERT INTO workflow_root_context_checkpoint(id,payload,payload_sha256) VALUES(%s,%s::jsonb,%s)", (ident, canonical, digest))
        return f"rctx1:{ident}:{digest}:{mac}", 1

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
