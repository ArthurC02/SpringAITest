"""Trusted Harness seam for task-local ContextRequest/ContextDelta mutation."""

from app.engine.node_registry import node
from app.nodes.context_enrichment.ports import TaskContextPort
from app.runtime.orchestrator_backend import ContextDelta, VersionedContextRequest
from app.security import RequestContext


@node(
    name="context_task_local_update", version="1.0",
    reads=["task_context_job", "tenant_id", "user_id", "role"],
    writes=["context_request", "context_delta_result"],
    deps=["context_task_backend"],
    description="Create or update task-local context through a trusted child Harness seam.",
)
def make_task_local_context_node(context_task_backend: TaskContextPort):
    async def task_local_context(state: dict) -> dict:
        job = state.get("task_context_job")
        if not isinstance(job, dict) or set(job) not in (
            {"operation", "root_run_id", "child_id"},
            {"operation", "root_run_id", "child_id", "request", "delta"},
        ):
            raise ValueError("task-local context job is invalid")
        ctx = RequestContext(
            tenant_id=state["tenant_id"], user_id=state["user_id"], role=state["role"]
        )
        if job["operation"] == "create" and set(job) == {
            "operation", "root_run_id", "child_id",
        }:
            result = await context_task_backend.create_context_request(
                job["root_run_id"], job["child_id"], ctx
            )
            return {"context_request": result.model_dump(mode="json")}
        if job["operation"] == "apply" and set(job) == {
            "operation", "root_run_id", "child_id", "request", "delta",
        }:
            request = VersionedContextRequest.model_validate(job["request"])
            delta = ContextDelta.model_validate(job["delta"])
            result = await context_task_backend.apply_context_delta(
                job["root_run_id"], job["child_id"], request, delta, ctx
            )
            return {"context_delta_result": result.model_dump(mode="json")}
        raise ValueError("task-local context operation is invalid")

    return task_local_context
