from __future__ import annotations

import socket
import uuid
from typing import Any, Literal

import httpx
from pydantic import AliasChoices, BaseModel, ConfigDict, Field

from app.backend_http import get_client
from app.runtime.events import RuntimeEvent
from app.runtime.models import (
    DirectAgentExecutionSnapshot,
    parse_json_preserving_numbers,
)
from app.security import RequestContext
from app.settings import settings


class BackendRunError(RuntimeError):
    pass


class BackendRunConflict(BackendRunError):
    pass


class RunRecord(BaseModel):
    model_config = ConfigDict(extra="ignore", strict=True)

    id: str
    snapshot_hash: str
    status: str
    state_version: int = Field(ge=0)
    lease_generation: int = Field(default=0, ge=0)
    checkpoint_generation: int = Field(default=0, ge=0)
    checkpoint_ref: str | None = None
    checkpoint_version: int = Field(ge=0)
    event_ack_cursor: int = Field(default=0, ge=0)
    deadline_at: str = "2099-01-01T00:00:00Z"
    cancel_requested: bool = False


class LeaseRecord(BaseModel):
    model_config = ConfigDict(extra="ignore", strict=True)

    lease_token: str
    lease_generation: int = Field(default=0, ge=0)
    lease_expires_at: str
    checkpoint_generation: int = Field(default=0, ge=0)
    checkpoint_ref: str | None = None
    checkpoint_version: int = Field(default=0, ge=0)
    event_ack_cursor: int = Field(default=0, ge=0)
    run: RunRecord


class RecoveryCommand(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)

    command_id: str
    run_id: str
    command_type: Literal["start", "resume", "cancel", "deadline_cleanup"]
    input: dict[str, Any] = Field(default_factory=dict)
    tenant_id: str = ""
    user_id: str = ""
    role: str = Field(
        default="",
        validation_alias=AliasChoices("caller_role", "role"),
    )
    snapshot_hash: str
    run_status: str
    target_terminal: Literal["failed", "cancelled"] | None = None
    state_version: int = Field(ge=0)
    lease_generation: int = Field(default=0, ge=0)
    checkpoint_generation: int = Field(default=0, ge=0)
    checkpoint_ref: str | None = None
    checkpoint_version: int = Field(ge=0)
    event_ack_cursor: int = Field(default=0, ge=0)
    deadline_at: str = "2099-01-01T00:00:00Z"
    lease_token: str = ""
    lease_expires_at: str = "2099-01-01T00:00:00Z"
    snapshot: dict[str, Any] | None = None
    claim_token: str
    claim_expires_at: str
    dispatch_attempt: int = Field(ge=1)


class RecoveryClaimResponse(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)

    items: list[RecoveryCommand]
    has_more: bool


class BackendRunClient:
    """Workflow's narrow CAS/lease/event port to Backend."""

    def __init__(self, owner: str | None = None):
        self.owner = owner or f"workflow:{socket.gethostname()}:{uuid.uuid4().hex}"

    @staticmethod
    def _headers(ctx: RequestContext) -> dict[str, str]:
        return {
            "X-Internal-Token": settings.internal_api_token,
            "X-Tenant-Id": ctx.tenant_id,
            "X-User-Id": ctx.user_id,
            "X-User-Role": ctx.role,
        }

    async def execution_snapshot(
        self, run_id: str, ctx: RequestContext
    ) -> DirectAgentExecutionSnapshot:
        body = await self._request_json(
            "GET",
            f"/api/agent-runs/{_guid(run_id)}/execution-artifact",
            ctx,
            preserve_numbers=True,
        )
        try:
            snapshot = DirectAgentExecutionSnapshot.from_canonical_base64(
                body["snapshot_canonical_base64"],
                body["snapshot_hash"],
            )
            snapshot.assert_hash()
        except Exception as exc:
            raise BackendRunError("Backend returned an invalid execution snapshot") from exc
        return snapshot

    async def get_run(self, run_id: str, ctx: RequestContext) -> RunRecord:
        body = await self._request_json(
            "GET",
            f"/api/runs/{_guid(run_id)}",
            ctx,
        )
        try:
            return RunRecord.model_validate(body)
        except Exception as exc:
            raise BackendRunError("Backend returned an invalid Agent run") from exc

    async def claim_lease(
        self, run_id: str, ctx: RequestContext, expected_version: int
    ) -> LeaseRecord:
        body = await self._request_json(
            "POST",
            f"/api/agent-runs/{_guid(run_id)}/lease",
            ctx,
            json={
                "expected_version": expected_version,
                "owner": self.owner,
                "duration_seconds": settings.runtime_lease_seconds,
            },
        )
        try:
            return LeaseRecord.model_validate(body)
        except Exception as exc:
            raise BackendRunError("Backend returned an invalid run lease") from exc

    async def claim_command(
        self, run_id: str, command_id: str, ctx: RequestContext
    ) -> RecoveryCommand | None:
        try:
            response = await get_client().post(
                (
                    f"/api/agent-runs/{_guid(run_id)}/commands/"
                    f"{quote_path(command_id)}/claim"
                ),
                headers=self._headers(ctx),
                json={
                    "worker_id": self.owner,
                    "lease_seconds": settings.runtime_lease_seconds,
                },
                timeout=httpx.Timeout(10.0),
            )
            if response.status_code == 204:
                return None
            if response.status_code == 409:
                raise BackendRunConflict("Agent run command claim conflicted")
            response.raise_for_status()
            return RecoveryCommand.model_validate(response.json())
        except BackendRunError:
            raise
        except (httpx.HTTPError, ValueError) as exc:
            raise BackendRunError("Backend command claim failed") from exc

    async def claim_recovery_commands(
        self, *, limit: int, lease_seconds: int
    ) -> RecoveryClaimResponse:
        try:
            response = await get_client().post(
                "/api/agent-runs/recovery/claim",
                headers={"X-Internal-Token": settings.internal_api_token},
                json={
                    "worker_id": self.owner,
                    "limit": limit,
                    "lease_seconds": lease_seconds,
                },
                timeout=httpx.Timeout(10.0),
            )
            if response.status_code == 409:
                raise BackendRunConflict("Recovery claim changed concurrently")
            response.raise_for_status()
            return RecoveryClaimResponse.model_validate(response.json())
        except BackendRunError:
            raise
        except (httpx.HTTPError, ValueError) as exc:
            raise BackendRunError("Backend recovery claim failed") from exc

    async def complete_recovery_dispatch(
        self,
        command: RecoveryCommand,
        ctx: RequestContext,
    ) -> None:
        await self._request_no_content(
            "POST",
            (
                f"/api/agent-runs/{_guid(command.run_id)}/commands/"
                f"{quote_path(command.command_id)}/dispatch/complete"
            ),
            ctx,
            json={"claim_token": command.claim_token},
        )

    async def transition(
        self,
        run_id: str,
        ctx: RequestContext,
        *,
        expected_version: int,
        lease_token: str,
        lease_generation: int = 0,
        expected_event_ack_cursor: int | None = None,
        to_status: str,
        checkpoint_ref: str | None = None,
        checkpoint_version: int | None = None,
        pending_input: dict[str, Any] | None = None,
        result: dict[str, Any] | None = None,
        error_code: str | None = None,
        error_message: str | None = None,
    ) -> RunRecord:
        body = await self._request_json(
            "POST",
            f"/api/agent-runs/{_guid(run_id)}/transitions",
            ctx,
            json={
                "expected_version": expected_version,
                "to_status": to_status,
                "lease_token": lease_token,
                "lease_generation": lease_generation,
                "expected_event_ack_cursor": expected_event_ack_cursor,
                "checkpoint_ref": checkpoint_ref,
                "checkpoint_version": checkpoint_version,
                "pending_input": pending_input,
                "result": result,
                "error_code": error_code,
                "error_message": error_message,
            },
        )
        try:
            return RunRecord.model_validate(body)
        except Exception as exc:
            raise BackendRunError("Backend returned an invalid run transition") from exc

    async def append_events(
        self,
        run_id: str,
        ctx: RequestContext,
        events: list[RuntimeEvent],
        *,
        expected_version: int,
        lease_token: str,
        lease_generation: int = 0,
        event_cursor_start: int = 0,
    ) -> RunRecord:
        body = await self._request_json(
            "POST",
            f"/api/agent-runs/{_guid(run_id)}/events",
            ctx,
            json={
                "expected_version": expected_version,
                "lease_token": lease_token,
                "lease_generation": lease_generation,
                "event_cursor_start": event_cursor_start,
                "events": [event.as_backend_dict() for event in events],
            },
        )
        try:
            return RunRecord.model_validate(body)
        except Exception as exc:
            raise BackendRunError("Backend returned an invalid event response") from exc

    async def _request_json(
        self,
        method: str,
        path: str,
        ctx: RequestContext,
        *,
        json: dict[str, Any] | None = None,
        preserve_numbers: bool = False,
    ) -> Any:
        try:
            response = await get_client().request(
                method,
                path,
                headers=self._headers(ctx),
                json=json,
                timeout=httpx.Timeout(10.0),
            )
            if response.status_code == 409:
                raise BackendRunConflict("Agent run state changed concurrently")
            if response.status_code == 404:
                raise BackendRunError("Agent run is unavailable")
            response.raise_for_status()
            return (
                parse_json_preserving_numbers(response.content)
                if preserve_numbers
                else response.json()
            )
        except BackendRunError:
            raise
        except (httpx.HTTPError, ValueError) as exc:
            raise BackendRunError("Backend Agent run request failed") from exc

    async def _request_no_content(
        self,
        method: str,
        path: str,
        ctx: RequestContext,
        *,
        json: dict[str, Any],
    ) -> None:
        try:
            response = await get_client().request(
                method,
                path,
                headers=self._headers(ctx),
                json=json,
                timeout=httpx.Timeout(10.0),
            )
            if response.status_code == 409:
                raise BackendRunConflict("Recovery dispatch acknowledgement conflicted")
            if response.status_code == 404:
                raise BackendRunError("Recovery command is unavailable")
            response.raise_for_status()
        except BackendRunError:
            raise
        except httpx.HTTPError as exc:
            raise BackendRunError("Backend recovery acknowledgement failed") from exc


def _guid(value: str) -> str:
    try:
        return str(uuid.UUID(value))
    except (ValueError, AttributeError) as exc:
        raise BackendRunError("run_id is not a valid UUID") from exc


def quote_path(value: str) -> str:
    from urllib.parse import quote

    if not value or len(value) > 256:
        raise BackendRunError("command_id is invalid")
    return quote(value, safe="")
