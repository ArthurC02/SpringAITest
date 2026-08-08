from __future__ import annotations

from fastapi import APIRouter, Depends, HTTPException, Request, status

from app.runtime.backend import BackendRunConflict, BackendRunError
from app.runtime.manager import (
    RuntimeAdmissionRejected,
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
from app.security import RequestContext, require_runtime_context
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


async def agent_runtime_context(request: Request) -> RequestContext:
    """身分閘門掛成依賴（不是 handler 內第一行），讓認證先於 pydantic body 驗證。

    寫在 handler body 內時 FastAPI 會先驗 body，未授權的呼叫者能靠畸形 body 換到
    422 + 欄位細節來反推 schema；依賴形式讓同一批請求收斂成 401。
    """
    return await require_runtime_context(
        request,
        flag_name="agent_test_run_enabled",
        message="Agent runtime requires tenant, user, and role identity.",
    )


async def approved_write_context(request: Request) -> RequestContext:
    """D7 寫入旗標的 404 必須排在身分檢查之前（關閉時能力看起來像沒安裝過）。"""
    if not settings.agent_write_tools_enabled:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="Not Found")
    return await agent_runtime_context(request)


@router.post(
    "/{run_id}/start",
    response_model=RuntimeRunResult,
    status_code=status.HTTP_202_ACCEPTED,
)
async def start_agent_run(
    run_id: str,
    body: StartRunRequest,
    request: Request,
    ctx: RequestContext = Depends(agent_runtime_context),
) -> RuntimeRunResult:
    return await _call(_manager(request).dispatch_command(run_id, body.command_id, ctx))


@router.post(
    "/{run_id}/resume",
    response_model=RuntimeRunResult,
    status_code=status.HTTP_202_ACCEPTED,
)
async def resume_agent_run(
    run_id: str,
    body: ResumeRunRequest,
    request: Request,
    ctx: RequestContext = Depends(agent_runtime_context),
) -> RuntimeRunResult:
    return await _call(_manager(request).dispatch_command(run_id, body.command_id, ctx))


@router.post(
    "/{run_id}/cancel",
    response_model=RuntimeRunResult,
    status_code=status.HTTP_202_ACCEPTED,
)
async def cancel_agent_run(
    run_id: str,
    request: Request,
    body: CancelRunRequest,
    ctx: RequestContext = Depends(agent_runtime_context),
) -> RuntimeRunResult:
    return await _call(_manager(request).dispatch_command(run_id, body.command_id, ctx))


@router.post(
    "/{run_id}/approvals/{approval_id}/execute",
    response_model=RuntimeRunResult,
    status_code=status.HTTP_202_ACCEPTED,
)
async def execute_approved_write(
    run_id: str,
    approval_id: str,
    request: Request,
    ctx: RequestContext = Depends(approved_write_context),
) -> RuntimeRunResult:
    return await _call(_manager(request).execute_approved_write(run_id, approval_id, ctx))


async def _call(awaitable) -> RuntimeRunResult:
    try:
        return await awaitable
    except RuntimeAdmissionRejected as exc:
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            detail={"error": "agent_runtime_busy", "message": str(exc)},
            headers={"Retry-After": "1"},
        ) from exc
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
