from __future__ import annotations

import asyncio
from datetime import UTC, datetime, timedelta
from typing import Any, Protocol

from pydantic import BaseModel, ConfigDict

from app import backend_http
from app.runtime.checkpoints import PostgresCheckpointStore, checkpoint_config
from app.settings import settings


class RetentionCandidate(BaseModel):
    model_config = ConfigDict(extra="forbid")

    candidate_id: str
    kind: str
    source: str
    tenant_id: str
    user_id: str
    run_id: str
    snapshot_sha256: str
    max_generation: int
    checkpoint_ref: str | None = None
    completed_at: datetime


class RetentionEvidenceHook(Protocol):
    async def before_delete(self, candidate: RetentionCandidate) -> str: ...

    async def after_delete(
        self,
        candidate: RetentionCandidate,
        evidence_ref: str,
        deleted_threads: int,
        deleted_root_contexts: int,
    ) -> None: ...


class NoopRetentionEvidenceHook:
    """Report/off placeholder; delete must fail closed without real evidence."""

    async def before_delete(self, candidate: RetentionCandidate) -> str:
        raise RuntimeError(
            "checkpoint retention delete requires a backup/restore evidence provider"
        )

    async def after_delete(
        self,
        candidate: RetentionCandidate,
        evidence_ref: str,
        deleted_threads: int,
        deleted_root_contexts: int,
    ) -> None:
        return None


class CheckpointRetentionService:
    def __init__(
        self,
        store: PostgresCheckpointStore,
        evidence: RetentionEvidenceHook | None = None,
    ) -> None:
        self.store = store
        self.evidence = evidence or NoopRetentionEvidenceHook()
        self._run_lock = asyncio.Lock()

    async def run_once(self) -> dict[str, Any]:
        async with self._run_lock:
            return await self._run_once()

    async def _run_once(self) -> dict[str, Any]:
        mode = settings.checkpoint_retention_mode
        if mode == "off":
            return {"mode": "off", "candidate_count": 0, "deleted_count": 0}
        candidates = await self._candidates()
        inventory = await self.store.checkpoint_inventory()
        if mode == "report":
            return {
                "mode": "report",
                "candidate_count": len(candidates),
                "candidate_ids": [item.candidate_id for item in candidates],
                "deleted_count": 0,
                "inventory": inventory,
            }

        deleted_threads = 0
        deleted_roots = 0
        for candidate in candidates:
            evidence_ref = await self.evidence.before_delete(candidate)
            threads, roots = await self._delete(candidate)
            await self._ack(candidate, threads, roots, evidence_ref)
            await self.evidence.after_delete(
                candidate, evidence_ref, threads, roots
            )
            deleted_threads += threads
            deleted_roots += roots
        return {
            "mode": "delete",
            "candidate_count": len(candidates),
            "candidate_ids": [item.candidate_id for item in candidates],
            "deleted_count": len(candidates),
            "deleted_threads": deleted_threads,
            "deleted_root_contexts": deleted_roots,
            "inventory": inventory,
        }

    async def _candidates(self) -> list[RetentionCandidate]:
        now = datetime.now(UTC)
        response = await backend_http.get_client().get(
            "/api/internal/checkpoint-retention/candidates",
            params={
                "retentionBefore": (
                    now - timedelta(days=settings.checkpoint_retention_ttl_days)
                ).isoformat(),
                "recoveryBefore": (
                    now - timedelta(days=settings.checkpoint_retention_grace_days)
                ).isoformat(),
                "limit": 100,
            },
            headers={"X-Internal-Token": settings.internal_api_token},
        )
        response.raise_for_status()
        return [
            RetentionCandidate.model_validate(item)
            for item in response.json()["items"]
        ]

    async def _delete(self, candidate: RetentionCandidate) -> tuple[int, int]:
        if candidate.kind == "root_context":
            deleted = await self.store.delete_root_contexts(candidate.run_id)
            if await self.store.root_context_count(candidate.run_id) != 0:
                raise RuntimeError("Root context checkpoint deletion left dangling rows")
            return 0, deleted
        if candidate.kind != "agent_thread":
            raise ValueError("unknown checkpoint retention candidate kind")
        deleted_threads = 0
        for generation in range(1, candidate.max_generation + 1):
            config = checkpoint_config(
                tenant_id=candidate.tenant_id,
                user_id=candidate.user_id,
                run_id=candidate.run_id,
                snapshot_hash=candidate.snapshot_sha256,
                lease_generation=generation,
            )
            thread_id = config["configurable"]["thread_id"]
            await self._saver().adelete_thread(thread_id)
            if any((await self.store.thread_storage_counts(thread_id)).values()):
                raise RuntimeError("Checkpoint deletion left dangling rows")
            deleted_threads += 1
        return deleted_threads, 0

    async def _ack(
        self,
        candidate: RetentionCandidate,
        deleted_threads: int,
        deleted_root_contexts: int,
        evidence_ref: str,
    ) -> None:
        response = await backend_http.get_client().post(
            "/api/internal/checkpoint-retention/ack",
            json={
                "candidate_id": candidate.candidate_id,
                "deleted_threads": deleted_threads,
                "deleted_root_contexts": deleted_root_contexts,
                "evidence_ref": evidence_ref,
            },
            headers={"X-Internal-Token": settings.internal_api_token},
        )
        response.raise_for_status()

    def _saver(self):
        if self.store.saver is None:
            raise RuntimeError("checkpoint retention store is not open")
        return self.store.saver
