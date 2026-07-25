"""Backend persistence port for D5 root and child runs."""

from __future__ import annotations

import base64
import socket
import uuid
from typing import Literal

import httpx
from pydantic import BaseModel, ConfigDict, Field

from app.backend_http import get_client
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


class OrchestratorBackendClient:
    def __init__(self, owner: str | None = None):
        self.owner = owner or f"workflow-root:{socket.gethostname()}:{uuid.uuid4().hex}"

    @staticmethod
    def _headers(ctx: RequestContext) -> dict[str, str]:
        return {
            "X-Internal-Token": settings.internal_api_token,
            "X-Tenant-Id": ctx.tenant_id,
            "X-User-Id": ctx.user_id,
            "X-User-Role": ctx.role,
        }

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
                "task_envelope": {
                    "objective": task.objective,
                    "required_capabilities": task.required_capabilities,
                    "context": task.context,
                    "context_provenance": [
                        item.model_dump(mode="json")
                        for item in task.context_provenance
                    ],
                    "write_intent": task.write_intent,
                    "delegation_depth": task.delegation_depth,
                    "repair_of": task.repair_of,
                },
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
                headers=self._headers(ctx) | (headers or {}),
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
