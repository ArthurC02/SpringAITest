"""Production entry point for the internal task-local context Skill.

E3 seam: no worker-side trigger is wired yet because the worker-tool
integration that would raise a ContextRequest is not defined by any contract
document.  Until it is, this runtime is exercised only by the contract tests —
inventing a trigger here would hard-code a boundary Backend has not specified.
"""

from __future__ import annotations

from typing import Any

from app import skills
from app.runtime.orchestrator_backend import OrchestratorBackendPermanentError
from app.security import RequestContext
from app.settings import settings


class TaskLocalContextRuntime:
    async def execute(
        self, job: dict[str, Any], ctx: RequestContext
    ) -> dict[str, Any]:
        if not (
            settings.multi_agent_dispatch_enabled
            and settings.context_enrichment_enabled
        ):
            raise OrchestratorBackendPermanentError("Task-local context is disabled")
        loaded = skills.get("context-task-local")
        if loaded is None:
            raise OrchestratorBackendPermanentError(
                "Task-local context Skill is unavailable"
            )
        output = await loaded.graph.ainvoke(
            {
                "task_context_job": job,
                "tenant_id": ctx.tenant_id,
                "user_id": ctx.user_id,
                "role": ctx.role,
            },
            config={"recursion_limit": loaded.recursion_limit},
        )
        fatal = output.get("fatal_error")
        if fatal:
            raise OrchestratorBackendPermanentError(
                f"Task-local context Skill failed: {fatal}"
            )
        return {
            key: output[key]
            for key in ("context_request", "context_delta_result")
            if key in output
        }
