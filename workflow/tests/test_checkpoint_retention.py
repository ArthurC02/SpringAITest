from __future__ import annotations

import asyncio
from typing import Any

import pytest
from fastapi.testclient import TestClient

from app import backend_http
from app.main import app
from app.runtime.retention import CheckpointRetentionService, RetentionCandidate
from app.settings import settings


class _Response:
    def __init__(self, payload: dict[str, Any] | None = None, error: bool = False) -> None:
        self._payload = payload or {}
        self._error = error

    def json(self) -> dict[str, Any]:
        return self._payload

    def raise_for_status(self) -> None:
        if self._error:
            raise RuntimeError("backend acknowledgement failed")


class _Backend:
    def __init__(self, candidates: list[dict[str, Any]]) -> None:
        self.candidates = candidates
        self.get_calls = 0
        self.acks: list[dict[str, Any]] = []
        self.fail_acks = 0

    async def get(self, path: str, **kwargs: Any) -> _Response:
        self.get_calls += 1
        assert path == "/api/internal/checkpoint-retention/candidates"
        assert kwargs["params"]["limit"] == 100
        assert set(kwargs["headers"]) == {"X-Internal-Token"}
        return _Response({"items": self.candidates, "has_more": False})

    async def post(self, path: str, **kwargs: Any) -> _Response:
        assert path == "/api/internal/checkpoint-retention/ack"
        if self.fail_acks:
            self.fail_acks -= 1
            return _Response(error=True)
        self.acks.append(kwargs["json"])
        return _Response()


class _Saver:
    def __init__(self) -> None:
        self.calls: list[str] = []

    async def adelete_thread(self, thread_id: str) -> None:
        self.calls.append(thread_id)
        await asyncio.sleep(0)


class _Store:
    def __init__(self) -> None:
        self.saver = _Saver()
        self.inventory_calls = 0
        self.root_rows: dict[str, int] = {}
        self.dangling = False

    async def checkpoint_inventory(self) -> dict[str, Any]:
        self.inventory_calls += 1
        return {"terminal_authority": "unavailable", "items": []}

    async def delete_root_contexts(self, run_id: str) -> int:
        deleted = self.root_rows.get(run_id, 0)
        self.root_rows[run_id] = 0
        return deleted

    async def root_context_count(self, run_id: str) -> int:
        return self.root_rows.get(run_id, 0)

    async def thread_storage_counts(self, thread_id: str) -> dict[str, int]:
        return {
            "checkpoints": 1 if self.dangling else 0,
            "blobs": 0,
            "writes": 0,
        }


class _Evidence:
    def __init__(self) -> None:
        self.before: list[str] = []
        self.after: list[str] = []

    async def before_delete(self, candidate: RetentionCandidate) -> str:
        self.before.append(candidate.candidate_id)
        return "backup-restore:test-evidence"

    async def after_delete(
        self,
        candidate: RetentionCandidate,
        evidence_ref: str,
        deleted_threads: int,
        deleted_root_contexts: int,
    ) -> None:
        self.after.append(candidate.candidate_id)


def _candidate(
    candidate_id: str,
    *,
    kind: str = "agent_thread",
    run_id: str = "00000000-0000-0000-0000-000000000001",
    generations: int = 2,
) -> dict[str, Any]:
    return {
        "candidate_id": candidate_id,
        "kind": kind,
        "source": "d3" if kind == "agent_thread" else "d5-root",
        "tenant_id": "tenant-a",
        "user_id": "user-a",
        "run_id": run_id,
        "snapshot_sha256": "a" * 64,
        "max_generation": generations,
        "checkpoint_ref": None,
        "completed_at": "2025-01-01T00:00:00Z",
    }


@pytest.mark.asyncio
async def test_off_mode_does_not_inventory_or_call_backend(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setattr(settings, "checkpoint_retention_mode", "off")
    monkeypatch.setattr(
        backend_http, "get_client", lambda: pytest.fail("backend must not be called")
    )
    store = _Store()

    result = await CheckpointRetentionService(store).run_once()  # type: ignore[arg-type]

    assert result == {"mode": "off", "candidate_count": 0, "deleted_count": 0}
    assert store.inventory_calls == 0


@pytest.mark.asyncio
async def test_report_and_delete_use_same_authority_selection(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    candidates = [
        _candidate("agent-candidate"),
        _candidate(
            "root-candidate",
            kind="root_context",
            run_id="00000000-0000-0000-0000-000000000002",
            generations=0,
        ),
    ]
    backend = _Backend(candidates)
    monkeypatch.setattr(backend_http, "get_client", lambda: backend)
    store = _Store()
    store.root_rows["00000000-0000-0000-0000-000000000002"] = 2

    monkeypatch.setattr(settings, "checkpoint_retention_mode", "report")
    report = await CheckpointRetentionService(store).run_once()  # type: ignore[arg-type]
    assert store.saver.calls == []
    assert backend.acks == []

    evidence = _Evidence()
    monkeypatch.setattr(settings, "checkpoint_retention_mode", "delete")
    deleted = await CheckpointRetentionService(  # type: ignore[arg-type]
        store, evidence
    ).run_once()

    assert report["candidate_ids"] == deleted["candidate_ids"]
    assert deleted["deleted_threads"] == 2
    assert deleted["deleted_root_contexts"] == 2
    assert len(backend.acks) == 2
    assert evidence.before == evidence.after == report["candidate_ids"]


@pytest.mark.asyncio
async def test_crash_before_ack_retries_idempotent_delete(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    backend = _Backend([_candidate("candidate", generations=1)])
    backend.fail_acks = 1
    monkeypatch.setattr(backend_http, "get_client", lambda: backend)
    monkeypatch.setattr(settings, "checkpoint_retention_mode", "delete")
    store = _Store()
    service = CheckpointRetentionService(store, _Evidence())  # type: ignore[arg-type]

    with pytest.raises(RuntimeError, match="acknowledgement failed"):
        await service.run_once()
    result = await service.run_once()

    assert result["deleted_count"] == 1
    assert len(store.saver.calls) == 2
    assert len(backend.acks) == 1


@pytest.mark.asyncio
async def test_two_instances_repeat_only_idempotent_official_delete(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    backend = _Backend([_candidate("candidate", generations=1)])
    monkeypatch.setattr(backend_http, "get_client", lambda: backend)
    monkeypatch.setattr(settings, "checkpoint_retention_mode", "delete")
    store = _Store()

    await asyncio.gather(
        CheckpointRetentionService(store, _Evidence()).run_once(),  # type: ignore[arg-type]
        CheckpointRetentionService(store, _Evidence()).run_once(),  # type: ignore[arg-type]
    )

    assert len(store.saver.calls) == 2
    assert len(backend.acks) == 2


@pytest.mark.asyncio
async def test_dangling_rows_fail_before_ack(monkeypatch: pytest.MonkeyPatch) -> None:
    backend = _Backend([_candidate("candidate", generations=1)])
    monkeypatch.setattr(backend_http, "get_client", lambda: backend)
    monkeypatch.setattr(settings, "checkpoint_retention_mode", "delete")
    store = _Store()
    store.dangling = True

    with pytest.raises(RuntimeError, match="dangling"):
        await CheckpointRetentionService(store, _Evidence()).run_once()  # type: ignore[arg-type]

    assert backend.acks == []


@pytest.mark.asyncio
async def test_delete_with_noop_evidence_fails_before_delete_or_ack(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    backend = _Backend([_candidate("candidate", generations=1)])
    monkeypatch.setattr(backend_http, "get_client", lambda: backend)
    monkeypatch.setattr(settings, "checkpoint_retention_mode", "delete")
    store = _Store()

    with pytest.raises(RuntimeError, match="backup/restore evidence provider"):
        await CheckpointRetentionService(store).run_once()  # type: ignore[arg-type]

    assert store.saver.calls == []
    assert backend.acks == []


def test_delete_with_noop_evidence_fails_during_app_startup(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setattr(settings, "checkpoint_retention_mode", "delete")

    with pytest.raises(RuntimeError, match="backup/restore evidence provider"):
        with TestClient(app):
            pytest.fail("application must not start in delete mode without evidence")
