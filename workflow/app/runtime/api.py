from __future__ import annotations

from functools import partial

from fastapi import APIRouter, HTTPException, Request, status

from app.runtime.backend import BackendRunConflict, BackendRunError
from app.runtime.manager import (
    RuntimeManagerConflict,
    RuntimeManagerError,
    RuntimeRunManager,
)
from app.runtime.models import (
    CancelRunRequest,
    ResumeRunRequest,
    RuntimeRunResult,
    StartRunRequest,
)
from app.security import require_runtime_context
from app.settings import settings

router = APIRouter(prefix="/agent-runs", tags=["agent-runtime"])


def _manager(request: Request) -> RuntimeRunManager:
    value = getattr(request.app.state, "agent_runtime_manager", None)
    if value is None:
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            detail={
                "error": "agent_runtime_unavailable",
                "message": "Agent runtime is not ready.",
            },
        )
    return value


_context = partial(
    require_runtime_context,
    flag_name="agent_test_run_enabled",
    message="Agent runtime requires tenant, user, and role identity.",
)


@router.post(
    "/{run_id}/start",
    response_model=RuntimeRunResult,
    status_code=status.HTTP_202_ACCEPTED,
)
async def start_agent_run(
    run_id: str, body: StartRunRequest, request: Request
) -> RuntimeRunResult:
    ctx = await _context(request)
    return await _call(_manager(request).dispatch_command(run_id, body.command_id, ctx))


@router.post(
    "/{run_id}/resume",
    response_model=RuntimeRunResult,
    status_code=status.HTTP_202_ACCEPTED,
)
async def resume_agent_run(
    run_id: str, body: ResumeRunRequest, request: Request
) -> RuntimeRunResult:
    ctx = await _context(request)
    return await _call(_manager(request).dispatch_command(run_id, body.command_id, ctx))


@router.post(
    "/{run_id}/cancel",
    response_model=RuntimeRunResult,
    status_code=status.HTTP_202_ACCEPTED,
)
async def cancel_agent_run(
    run_id: str, request: Request, body: CancelRunRequest
) -> RuntimeRunResult:
    ctx = await _context(request)
    return await _call(_manager(request).dispatch_command(run_id, body.command_id, ctx))


@router.post(
    "/{run_id}/approvals/{approval_id}/execute",
    response_model=RuntimeRunResult,
    status_code=status.HTTP_202_ACCEPTED,
)
async def execute_approved_write(run_id: str, approval_id: str, request: Request) -> RuntimeRunResult:
    if not settings.agent_write_tools_enabled:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="Not Found")
    ctx = await _context(request)
    return await _call(_manager(request).execute_approved_write(run_id, approval_id, ctx))


async def _call(awaitable) -> RuntimeRunResult:
    try:
        return await awaitable
    except (RuntimeManagerConflict, BackendRunConflict) as exc:
        raise HTTPException(
            status_code=status.HTTP_409_CONFLICT,
            detail={"error": "agent_run_conflict", "message": str(exc)},
        ) from exc
    except (RuntimeManagerError, ValueError) as exc:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail={"error": "agent_run_invalid", "message": str(exc)},
        ) from exc
    except BackendRunError as exc:
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            detail={
                "error": "agent_runtime_dependency_failed",
                "message": str(exc),
            },
        ) from exc
