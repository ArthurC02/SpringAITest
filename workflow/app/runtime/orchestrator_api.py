"""Fail-closed internal API for Backend-issued Root execution snapshots."""

from __future__ import annotations

from typing import Literal

from fastapi import APIRouter, Depends, HTTPException, Request, status
from pydantic import BaseModel, ConfigDict, Field

from app.runtime.orchestrator_supervisor import RootRuntimeSupervisor
from app.security import RequestContext, require_runtime_context

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


async def root_runtime_context(request: Request) -> RequestContext:
    """身分閘門掛成依賴，讓認證先於 pydantic body 驗證（見 runtime/api.py 同名說明）。"""
    return await require_runtime_context(
        request,
        flag_name="multi_agent_dispatch_enabled",
        message="Root runtime requires tenant, user, and role identity.",
    )


@router.post(
    "/{run_id}/dispatch",
    response_model=RootDispatchAccepted,
    status_code=status.HTTP_202_ACCEPTED,
)
async def dispatch_root_run(
    run_id: str,
    body: RootDispatchRequest,
    request: Request,
    ctx: RequestContext = Depends(root_runtime_context),
) -> RootDispatchAccepted:
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
