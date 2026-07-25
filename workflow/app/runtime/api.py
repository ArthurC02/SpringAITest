from __future__ import annotations

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
from app.security import RequestContext, require_internal
from app.settings import settings

router = APIRouter(prefix="/agent-runs", tags=["agent-runtime"])


class AgentRuntimeFeatureGateMiddleware:
    """Return 404 before routing/body parsing when the D3 flag is disabled."""

    def __init__(self, app):
        self.app = app

    async def __call__(self, scope, receive, send):
        if (
            scope.get("type") == "http"
            and str(scope.get("path") or "").startswith("/agent-runs/")
            and not settings.agent_test_run_enabled
        ):
            response = HTTPException(status_code=404, detail="Not Found")
            from starlette.responses import JSONResponse

            await JSONResponse(
                status_code=response.status_code,
                content={"detail": response.detail},
            )(scope, receive, send)
            return
        await self.app(scope, receive, send)


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


async def _context(request: Request) -> RequestContext:
    # Feature-off is deliberately resolved before authentication: the route
    # remains indistinguishable from an uninstalled capability.
    if not settings.agent_test_run_enabled:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="Not Found")
    await require_internal(request.headers.get("X-Internal-Token"))
    tenant = (request.headers.get("X-Tenant-Id") or "").strip()
    user = (request.headers.get("X-User-Id") or "").strip()
    role = (request.headers.get("X-User-Role") or "").strip()
    if not tenant or not user or not role:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail={
                "error": "missing_context",
                "message": "Agent runtime requires tenant, user, and role identity.",
            },
        )
    return RequestContext(tenant_id=tenant, user_id=user, role=role)


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
