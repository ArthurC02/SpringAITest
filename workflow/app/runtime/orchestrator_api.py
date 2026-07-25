"""Fail-closed internal API for Backend-issued Root execution snapshots."""

from __future__ import annotations

from typing import Literal

from fastapi import APIRouter, HTTPException, Request, status
from pydantic import BaseModel, ConfigDict, Field
from starlette.responses import JSONResponse

from app.runtime.orchestrator_supervisor import RootRuntimeSupervisor
from app.security import RequestContext, require_internal
from app.settings import settings

router = APIRouter(prefix="/orchestrator-runs", tags=["root-orchestrator-runtime"])


class RootDispatchRequest(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)

    command_id: str = Field(min_length=1, max_length=128)
    # Accepted for Platform wire compatibility but never used as authority.
    context: dict = Field(default_factory=dict)


class RootDispatchAccepted(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)
    run_id: str
    status: Literal["accepted"]
    scheduled: bool


class MultiAgentDispatchFeatureGateMiddleware:
    """Hide the entire D5 surface before auth and request-body parsing."""

    def __init__(self, app):
        self.app = app

    async def __call__(self, scope, receive, send):
        if (
            scope.get("type") == "http"
            and str(scope.get("path") or "").startswith("/orchestrator-runs")
            and not settings.multi_agent_dispatch_enabled
        ):
            await JSONResponse(status_code=404, content={"detail": "Not Found"})(
                scope, receive, send
            )
            return
        await self.app(scope, receive, send)


async def _context(request: Request) -> RequestContext:
    if not settings.multi_agent_dispatch_enabled:
        raise HTTPException(status_code=404, detail="Not Found")
    await require_internal(request.headers.get("X-Internal-Token"))
    tenant = (request.headers.get("X-Tenant-Id") or "").strip()
    user = (request.headers.get("X-User-Id") or "").strip()
    role = (request.headers.get("X-User-Role") or "").strip()
    if not tenant or not user or not role:
        raise HTTPException(
            status_code=400,
            detail={
                "error": "missing_context",
                "message": "Root runtime requires tenant, user, and role identity.",
            },
        )
    return RequestContext(tenant_id=tenant, user_id=user, role=role)


@router.post(
    "/{run_id}/dispatch",
    response_model=RootDispatchAccepted,
    status_code=status.HTTP_202_ACCEPTED,
)
async def dispatch_root_run(
    run_id: str, body: RootDispatchRequest, request: Request
) -> RootDispatchAccepted:
    ctx = await _context(request)
    supervisor: RootRuntimeSupervisor | None = getattr(
        request.app.state, "root_runtime_supervisor", None
    )
    if supervisor is None:
        raise HTTPException(
            status_code=503,
            detail={
                "error": "multi_agent_runtime_unavailable",
                "message": "Root Orchestrator runtime is not ready.",
            },
        )
    scheduled = supervisor.schedule(run_id, body.command_id, ctx)
    return RootDispatchAccepted(
        run_id=run_id, status="accepted", scheduled=scheduled
    )
