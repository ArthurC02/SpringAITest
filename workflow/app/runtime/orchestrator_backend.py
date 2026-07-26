"""Backend persistence port for D5 root and child runs."""

from __future__ import annotations

import base64
import socket
import uuid
from typing import Any, Literal

import httpx
from pydantic import BaseModel, ConfigDict, Field

from app.backend_http import get_client, internal_headers
from app.runtime.orchestrator import (
    ContextAcquisition,
    RootExecutionSnapshot,
    TaskAssignment,
    WorkerPin,
)
from app.runtime.models import canonical_json_bytes, parse_json_preserving_numbers
from app.security import RequestContext
from app.settings import settings


class OrchestratorBackendError(RuntimeError):
    pass


class OrchestratorBackendConflict(OrchestratorBackendError):
    pass


class OrchestratorBackendPermanentError(OrchestratorBackendError):
    """A durable contract rejection that must not be retried by recovery."""


class _Wire(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)


_GUID_PATTERN = r"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[1-5][0-9a-fA-F]{3}-[89abAB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}$"


class RootCommandClaim(_Wire):
    command_id: str
    run_id: str
    command_type: Literal["start", "resume"]
    claim_token: str
    claim_expires_at: str
    lease_generation: int = Field(ge=1)
    snapshot_hash: str = Field(pattern=r"^[0-9a-f]{64}$")
    snapshot_canonical_base64: str
    resume_input: str | None = None
    checkpoint_ref: str | None = None
    checkpoint_version: int | None = None
    deadline_at: str | None = None

    def snapshot(self) -> RootExecutionSnapshot:
        try:
            raw = base64.b64decode(
                self.snapshot_canonical_base64, validate=True
            )
            value = parse_json_preserving_numbers(raw)
            if canonical_json_bytes(value) != raw:
                raise ValueError("Root snapshot bytes are not canonical JSON")
            snapshot = RootExecutionSnapshot.from_preserved_json(value)
            if snapshot.snapshot_hash != self.snapshot_hash:
                raise ValueError("snapshot envelope hash mismatch")
            return snapshot
        except Exception as exc:
            raise OrchestratorBackendPermanentError(
                "Backend returned an invalid Root snapshot"
            ) from exc


class ChildRecord(_Wire):
    id: str
    orchestrator_root_run_id: str
    task_id: str
    attempt: int = Field(ge=1)
    run_kind: Literal["worker", "verifier"]
    agent_id: str
    agent_revision: int = Field(ge=1)
    workflow_id: str
    workflow_revision: int = Field(ge=1)
    agent_snapshot_hash: str = Field(pattern=r"^[0-9a-f]{64}$")
    status: Literal["queued", "running", "completed", "failed", "cancelled"]
    agent_run_id: str
    command_id: str


class ChildStatus(ChildRecord):
    command_id: str | None = None
    output: dict = Field(default_factory=dict)
    citations: list[dict] = Field(default_factory=list)
    error_code: str | None = None
    error_message: str | None = None


class RootRecord(_Wire):
    model_config = ConfigDict(extra="ignore", strict=True)
    id: str
    status: str
    state_version: int = Field(ge=1)


class RootRecoveryItem(_Wire):
    tenant_id: str
    user_id: str
    role: str
    claim: RootCommandClaim


class RootRecoveryResponse(_Wire):
    items: list[RootRecoveryItem]
    has_more: bool


class ContextRequestRef(_Wire):
    context_id: str = Field(pattern=_GUID_PATTERN)
    revision: int = Field(ge=1)
    view_id: str = Field(pattern=_GUID_PATTERN)


class ContextRequest(_Wire):
    id: str = Field(pattern=_GUID_PATTERN)
    root_run_id: str = Field(pattern=_GUID_PATTERN)
    child_id: str = Field(pattern=_GUID_PATTERN)
    task_id: str
    role: Literal["worker", "verifier"]
    context_id: str = Field(pattern=_GUID_PATTERN)
    base_context_ref: ContextRequestRef | None = None
    current_context_ref: ContextRequestRef | None = None
    version: int = Field(ge=1)
    created_at: str
    updated_at: str


class ContextDeltaEvidence(_Wire):
    evidence_type: str
    source_id: str
    snapshot_id: str
    content_ref: str
    content_hash: str = Field(pattern=r"^[0-9a-f]{64}$")
    scope: dict[str, Any] = Field(default_factory=dict)
    observations: dict[str, Any]
    acl_decision_id: str | None = None
    observed_at: str
    lineage: dict[str, Any]


class ContextDeltaView(_Wire):
    view_type: Literal["planner", "worker", "verifier", "synthesizer"]
    definition: dict[str, Any]


class ContextDeltaSourceFailure(_Wire):
    source_id: str = Field(min_length=1, max_length=256)
    failure_code: str = Field(min_length=1, max_length=128)


class ContextDeltaMeasurements(_Wire):
    context_round: int = Field(ge=1)
    max_context_rounds: int = Field(ge=1)
    critical_ambiguity: bool = False
    deadline_exhausted: bool = False
    assumptions_count: int = Field(default=0, ge=0)
    policy_violations: list[str] = Field(default_factory=list)
    retrieval_gaps: list[ContextDeltaSourceFailure] = Field(default_factory=list)


class ContextDelta(_Wire):
    definition: dict[str, Any]
    evidence: list[ContextDeltaEvidence] = Field(default_factory=list)
    views: list[ContextDeltaView] = Field(default_factory=list)
    measurements: ContextDeltaMeasurements
    as_of: str | None = None
    expires_at: str | None = None


class ContextDeltaResponse(_Wire):
    context_id: str = Field(pattern=_GUID_PATTERN)
    revision: int = Field(ge=1)
    root_run_id: str | None = Field(default=None, pattern=_GUID_PATTERN)
    status: Literal[
        "NEED_MORE_CONTEXT", "NEEDS_CLARIFICATION", "BLOCKED_BY_POLICY",
        "INSUFFICIENT_DATA", "READY", "READY_WITH_ASSUMPTIONS",
    ]
    readiness: float
    unmet_requirements: list[str]
    policy_id: str = Field(pattern=_GUID_PATTERN)
    definition: dict[str, Any]
    as_of: str
    created_at: str
    expires_at: str | None = None
    context_ref: ContextRequestRef | None = None
    selected_source_id: str | None = None
    adapter_id: str | None = None


class VersionedContextRequest(BaseModel):
    model_config = ConfigDict(frozen=True, arbitrary_types_allowed=False)
    value: ContextRequest
    etag: str


class AppliedContextDelta(BaseModel):
    model_config = ConfigDict(frozen=True, arbitrary_types_allowed=False)
    revision: ContextDeltaResponse
    request_etag: str


class OrchestratorBackendClient:
    def __init__(self, owner: str | None = None):
        self.owner = owner or f"workflow-root:{socket.gethostname()}:{uuid.uuid4().hex}"

    async def claim(
        self, run_id: str, command_id: str, ctx: RequestContext
    ) -> RootCommandClaim | None:
        response = await self._request(
            "POST",
            f"/api/orchestrator-runs/{_guid(run_id)}/commands/{_guid(command_id)}/claim",
            ctx,
            json={
                "worker_id": self.owner,
                "lease_seconds": settings.multi_agent_root_lease_seconds,
            },
        )
        if response.status_code == 204:
            return None
        try:
            return RootCommandClaim.model_validate(response.json())
        except ValueError as exc:
            raise OrchestratorBackendError("Backend returned an invalid Root claim") from exc

    async def claim_recovery(self) -> RootRecoveryResponse:
        try:
            response = await get_client().post(
                "/api/orchestrator-runs/recovery/claim",
                headers={"X-Internal-Token": settings.internal_api_token},
                json={
                    "worker_id": self.owner,
                    "limit": settings.runtime_recovery_batch_size,
                    "lease_seconds": settings.multi_agent_root_lease_seconds,
                },
                timeout=httpx.Timeout(10.0),
            )
            if response.status_code == 409:
                raise OrchestratorBackendConflict(
                    "Root recovery claim changed concurrently"
                )
            response.raise_for_status()
            return RootRecoveryResponse.model_validate(response.json())
        except OrchestratorBackendError:
            raise
        except (httpx.HTTPError, ValueError) as exc:
            raise OrchestratorBackendError("Backend Root recovery claim failed") from exc

    async def complete_dispatch(
        self, claim: RootCommandClaim, ctx: RequestContext
    ) -> None:
        await self._request(
            "POST",
            f"/api/orchestrator-runs/{_guid(claim.run_id)}/commands/{_guid(claim.command_id)}/dispatch/complete",
            ctx,
            json={"claim_token": claim.claim_token},
        )

    async def renew_claim(
        self, claim: RootCommandClaim, ctx: RequestContext
    ) -> None:
        await self._request(
            "POST",
            f"/api/orchestrator-runs/{_guid(claim.run_id)}/commands/{_guid(claim.command_id)}/lease/renew",
            ctx,
            json={
                "claim_token": claim.claim_token,
                "lease_generation": claim.lease_generation,
                "lease_seconds": settings.multi_agent_root_lease_seconds,
            },
        )

    async def create_child(
        self,
        snapshot: RootExecutionSnapshot,
        task: TaskAssignment,
        worker: WorkerPin,
        kind: Literal["worker", "verifier"],
        ctx: RequestContext,
    ) -> ChildRecord:
        task_envelope = {
            "objective": task.objective,
            "required_capabilities": task.required_capabilities,
            "context": task.context,
            "context_provenance": [
                item.model_dump(mode="json") for item in task.context_provenance
            ],
            "write_intent": task.write_intent,
            "delegation_depth": task.delegation_depth,
            "repair_of": task.repair_of,
        }
        if task.context_ref is not None:
            task_envelope["context_ref"] = task.context_ref.model_dump(mode="json")
        response = await self._request(
            "POST",
            f"/api/orchestrator-runs/{_guid(snapshot.root_run_id)}/children",
            ctx,
            json={
                "task_id": task.task_id,
                "attempt": task.attempt,
                "run_kind": kind,
                "agent_id": worker.agent_id,
                "agent_revision": worker.agent_revision,
                "token_cap": worker.token_cap,
                "write_intent": task.write_intent,
                "task_envelope": task_envelope,
            },
        )
        try:
            child = ChildRecord.model_validate(response.json())
        except ValueError as exc:
            raise OrchestratorBackendError("Backend returned an invalid child") from exc
        if (
            child.task_id != task.task_id
            or child.attempt != task.attempt
            or child.agent_id != worker.agent_id
            or child.agent_revision != worker.agent_revision
            or child.workflow_id != worker.workflow_id
            or child.workflow_revision != worker.workflow_revision
            or child.agent_snapshot_hash != worker.snapshot_hash
        ):
            raise OrchestratorBackendError("Backend child does not match its immutable pin")
        return child

    async def get_child(
        self, root_run_id: str, child_id: str, ctx: RequestContext
    ) -> ChildStatus:
        response = await self._request(
            "GET",
            f"/api/orchestrator-runs/{_guid(root_run_id)}/children/{_guid(child_id)}",
            ctx,
        )
        try:
            return ChildStatus.model_validate(response.json())
        except ValueError as exc:
            raise OrchestratorBackendError("Backend returned an invalid child status") from exc

    async def get_root(self, root_run_id: str, ctx: RequestContext) -> RootRecord:
        response = await self._request(
            "GET", f"/api/orchestrator-runs/{_guid(root_run_id)}", ctx
        )
        try:
            return RootRecord.model_validate(response.json())
        except ValueError as exc:
            raise OrchestratorBackendError("Backend returned an invalid Root run") from exc

    async def create_context_request(
        self, root_run_id: str, child_id: str, ctx: RequestContext
    ) -> VersionedContextRequest:
        self._require_task_context_enabled()
        response = await self._request(
            "POST",
            f"/api/orchestrator-runs/{_guid(root_run_id)}/children/{_guid(child_id)}/context-requests",
            ctx,
        )
        return self._context_request(response, root_run_id, child_id)

    async def get_context_request(
        self, root_run_id: str, child_id: str, request_id: str,
        ctx: RequestContext,
    ) -> VersionedContextRequest:
        self._require_task_context_enabled()
        response = await self._request(
            "GET",
            f"/api/orchestrator-runs/{_guid(root_run_id)}/children/{_guid(child_id)}"
            f"/context-requests/{_guid(request_id)}",
            ctx,
        )
        return self._context_request(response, root_run_id, child_id)

    async def apply_context_delta(
        self,
        root_run_id: str,
        child_id: str,
        request: VersionedContextRequest,
        delta: ContextDelta,
        ctx: RequestContext,
    ) -> AppliedContextDelta:
        """Apply exactly once; an ETag conflict is never silently rebased."""
        self._require_task_context_enabled()
        if (
            request.value.root_run_id != root_run_id
            or request.value.child_id != child_id
            or request.etag != f'"{request.value.version}"'
        ):
            raise OrchestratorBackendPermanentError(
                "Context request does not belong to the supplied immutable child"
            )
        if (
            len(delta.views) != 1
            or delta.views[0].view_type != request.value.role
        ):
            raise OrchestratorBackendPermanentError(
                "Context delta view must match the Backend-issued task role"
            )
        try:
            response = await self._request(
                "POST",
                f"/api/orchestrator-runs/{_guid(root_run_id)}/children/{_guid(child_id)}"
                f"/context-requests/{_guid(request.value.id)}/deltas",
                ctx,
                headers={"If-Match": request.etag},
                json=delta.model_dump(mode="json"),
            )
        except OrchestratorBackendConflict as exc:
            latest = await self.get_context_request(
                root_run_id, child_id, request.value.id, ctx
            )
            if (
                latest.value.id != request.value.id
                or latest.value.task_id != request.value.task_id
                or latest.value.role != request.value.role
                or latest.value.context_id != request.value.context_id
                or latest.value.base_context_ref != request.value.base_context_ref
            ):
                raise OrchestratorBackendError(
                    "Backend changed immutable ContextRequest identity"
                ) from exc
            if latest.value.version == request.value.version:
                raise OrchestratorBackendPermanentError(
                    "Backend rejected ContextDelta governance"
                ) from exc
            raise OrchestratorBackendConflict(
                "Context delta ETag is stale "
                f"(expected version {request.value.version}, current version {latest.value.version})"
            ) from exc
        try:
            revision = ContextDeltaResponse.model_validate(response.json())
            etag = _required_etag(response)
        except ValueError as exc:
            raise OrchestratorBackendError(
                "Backend returned an invalid ContextDelta response"
            ) from exc
        return AppliedContextDelta(revision=revision, request_etag=etag)

    @staticmethod
    def _require_task_context_enabled() -> None:
        if not (
            settings.multi_agent_dispatch_enabled
            and settings.context_enrichment_enabled
        ):
            raise OrchestratorBackendPermanentError(
                "Task-local context is disabled"
            )

    @staticmethod
    def _context_request(
        response: httpx.Response, root_run_id: str, child_id: str
    ) -> VersionedContextRequest:
        try:
            value = ContextRequest.model_validate(response.json())
            etag = _required_etag(response)
        except ValueError as exc:
            raise OrchestratorBackendError(
                "Backend returned an invalid ContextRequest"
            ) from exc
        if value.root_run_id != root_run_id or value.child_id != child_id:
            raise OrchestratorBackendError(
                "Backend ContextRequest does not match its immutable child"
            )
        if etag != f'"{value.version}"':
            raise OrchestratorBackendError(
                "Backend ContextRequest ETag does not match its version"
            )
        return VersionedContextRequest(value=value, etag=etag)

    async def acquire_context(
        self,
        snapshot: RootExecutionSnapshot,
        current: dict,
        context_round: int,
        ctx: RequestContext,
    ) -> ContextAcquisition:
        response = await self._request(
            "POST",
            f"/api/orchestrator-runs/{_guid(snapshot.root_run_id)}/context/acquire",
            ctx,
            json={
                "context_round": context_round,
                "current_context": current,
                "allowed_tools": snapshot.authority.context_tools,
                "allowed_knowledge_sources": snapshot.authority.knowledge_sources,
            },
        )
        try:
            acquisition = ContextAcquisition.model_validate(response.json())
        except ValueError as exc:
            raise OrchestratorBackendError(
                "Backend returned invalid Root context"
            ) from exc
        context_keys = set(acquisition.context)
        provenance_keys = {item.context_key for item in acquisition.provenance}
        if context_keys != provenance_keys or len(provenance_keys) != len(acquisition.provenance):
            raise OrchestratorBackendError(
                "Root context lacks exact unique provenance"
            )
        for item in acquisition.provenance:
            if (
                item.source_type == "context-tool"
                and item.source_id not in snapshot.authority.context_tools
            ) or (
                item.source_type == "knowledge-source"
                and item.source_id not in snapshot.authority.knowledge_sources
            ):
                raise OrchestratorBackendError(
                    "Root context provenance exceeds snapshot authority"
                )
        return acquisition

    async def transition_root(
        self,
        claim: RootCommandClaim,
        ctx: RequestContext,
        *,
        to_status: Literal["completed", "failed", "cancelled", "timed_out"],
        result: dict | None,
        error_code: str | None,
        error_message: str | None,
        events: list[dict],
        checkpoint_ref: str | None = None,
        checkpoint_version: int | None = None,
    ) -> None:
        if any(
            isinstance(item, dict)
            and isinstance(item.get("event_type"), str)
            and item["event_type"].startswith("context.")
            for item in events
        ):
            raise OrchestratorBackendPermanentError(
                "Workflow cannot append Backend-owned context events"
            )
        root = await self.get_root(claim.run_id, ctx)
        await self._request(
            "POST",
            f"/api/orchestrator-runs/{_guid(claim.run_id)}/transitions",
            ctx,
            json={
                "expected_state_version": root.state_version,
                "claim_token": claim.claim_token,
                "lease_generation": claim.lease_generation,
                "to_status": to_status,
                "result": result,
                "error_code": error_code,
                "error_message": error_message,
                "events": events,
                "checkpoint_ref": checkpoint_ref,
                "checkpoint_version": checkpoint_version,
            },
        )

    async def cancel_root(
        self, root_run_id: str, ctx: RequestContext, reason: str
    ) -> None:
        await self._request(
            "POST",
            f"/api/orchestrator-runs/{_guid(root_run_id)}/cancel",
            ctx,
            headers={"Idempotency-Key": f"workflow-cancel-{_guid(root_run_id)}"},
            json={"reason": reason[:500]},
        )

    async def _request(
        self,
        method: str,
        path: str,
        ctx: RequestContext,
        *,
        json: dict | None = None,
        headers: dict[str, str] | None = None,
    ) -> httpx.Response:
        try:
            response = await get_client().request(
                method,
                path,
                headers=internal_headers(ctx) | (headers or {}),
                json=json,
                timeout=httpx.Timeout(10.0),
            )
            if response.status_code == 409:
                raise OrchestratorBackendConflict("Root run changed concurrently")
            if response.status_code in {400, 404, 413, 422, 428}:
                raise OrchestratorBackendPermanentError(
                    f"Backend permanently rejected Root request: HTTP {response.status_code}"
                )
            response.raise_for_status()
            return response
        except OrchestratorBackendError:
            raise
        except httpx.HTTPError as exc:
            raise OrchestratorBackendError("Backend Root run request failed") from exc


def _guid(value: str) -> str:
    try:
        return str(uuid.UUID(value))
    except (ValueError, AttributeError) as exc:
        raise OrchestratorBackendError("run or command id is not a UUID") from exc


def _required_etag(response: httpx.Response) -> str:
    etag = response.headers.get("ETag")
    if (
        not isinstance(etag, str) or len(etag) < 3
        or etag[0] != '"' or etag[-1] != '"'
        or not etag[1:-1].isdigit() or int(etag[1:-1]) < 1
    ):
        raise ValueError("Backend response is missing a valid ETag")
    return etag
