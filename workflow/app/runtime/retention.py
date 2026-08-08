from __future__ import annotations

import asyncio
from datetime import UTC, datetime, timedelta
from typing import Any, Protocol

from pydantic import BaseModel, ConfigDict, StrictBool, ValidationError

from app import backend_http
from app.runtime.checkpoints import PostgresCheckpointStore, checkpoint_config
from app.settings import settings


_CANDIDATE_PAGE_SIZE = 100
# Backend caps each page at 100. Keep one bounded retention sweep to 100,000
# candidates so a malformed or unexpectedly large authority response cannot run
# forever or hold the retention lock indefinitely.
_MAX_CANDIDATE_PAGES = 1_000
_MAX_CANDIDATES_PER_SWEEP = _CANDIDATE_PAGE_SIZE * _MAX_CANDIDATE_PAGES


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


class RetentionCandidatePage(BaseModel):
    model_config = ConfigDict(extra="forbid")

    items: list[RetentionCandidate]
    next_cursor: str | None = None
    has_more: StrictBool


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
        params = {
            "retentionBefore": (
                now - timedelta(days=settings.checkpoint_retention_ttl_days)
            ).isoformat(),
            "recoveryBefore": (
                now - timedelta(days=settings.checkpoint_retention_grace_days)
            ).isoformat(),
            "limit": _CANDIDATE_PAGE_SIZE,
        }
        candidates: list[RetentionCandidate] = []
        candidate_ids: set[str] = set()
        requested_cursors: set[str] = set()
        cursor: str | None = None

        for _ in range(_MAX_CANDIDATE_PAGES):
            page_params = params.copy()
            if cursor is not None:
                if cursor in requested_cursors:
                    raise RuntimeError(
                        "checkpoint retention candidate pagination repeated a cursor"
                    )
                requested_cursors.add(cursor)
                page_params["cursor"] = cursor

            response = await backend_http.get_client().get(
                "/api/internal/checkpoint-retention/candidates",
                params=page_params,
                headers=backend_http.internal_token_headers(),
            )
            response.raise_for_status()
            try:
                page = RetentionCandidatePage.model_validate(response.json())
            except ValidationError as exc:
                raise RuntimeError(
                    "checkpoint retention candidate pagination response is invalid"
                ) from exc
            if len(page.items) > _CANDIDATE_PAGE_SIZE:
                raise RuntimeError(
                    "checkpoint retention candidate page exceeded its contract limit"
                )
            page_candidates = page.items
            page_candidate_ids = {item.candidate_id for item in page_candidates}
            if len(page_candidate_ids) != len(page_candidates) or (
                page_candidate_ids & candidate_ids
            ):
                raise RuntimeError(
                    "checkpoint retention candidate pagination repeated a candidate"
                )
            if len(candidates) + len(page_candidates) > _MAX_CANDIDATES_PER_SWEEP:
                raise RuntimeError(
                    "checkpoint retention candidate pagination exceeded its safety limit"
                )
            candidate_ids.update(page_candidate_ids)
            candidates.extend(page_candidates)

            if not page.has_more:
                return candidates
            next_cursor = page.next_cursor
            if not isinstance(next_cursor, str) or not next_cursor.strip():
                raise RuntimeError(
                    "checkpoint retention candidate pagination returned no next cursor"
                )
            if not page_candidates:
                raise RuntimeError(
                    "checkpoint retention candidate pagination made no progress"
                )
            if next_cursor in requested_cursors:
                raise RuntimeError(
                    "checkpoint retention candidate pagination repeated a cursor"
                )
            cursor = next_cursor

        raise RuntimeError("checkpoint retention candidate pagination exceeded its page limit")

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
            headers=backend_http.internal_token_headers(),
        )
        response.raise_for_status()

    def _saver(self):
        if self.store.saver is None:
            raise RuntimeError("checkpoint retention store is not open")
        return self.store.saver
