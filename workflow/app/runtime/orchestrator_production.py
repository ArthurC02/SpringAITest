"""Production composition for the D5 Root Orchestrator."""

from __future__ import annotations

import asyncio
import json
from typing import Any

from pydantic import BaseModel, ConfigDict, Field

from app.llm import get_direct_agent_runtime_llm
from app.backend_http import search_chunks_scoped
from app.runtime.manager import RuntimeRunManager
from app.runtime.orchestrator import (
    ChildResult,
    ContextAcquisition,
    ContextProvenance,
    RootExecutionSnapshot,
    RootOrchestrator,
    TaskAssignment,
    VerificationReport,
    WorkerPin,
    MAX_CHILD_CITATIONS_BYTES,
    MAX_CHILD_OUTPUT_BYTES,
)
from app.runtime.orchestrator_backend import (
    ChildRecord,
    OrchestratorBackendClient,
    OrchestratorBackendError,
)
from app.runtime.models import canonical_json_sha256
from app.security import RequestContext
from app.settings import settings


class _PlanTask(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)
    task_id: str = Field(min_length=1, max_length=128)
    objective: str = Field(min_length=1, max_length=16_384)
    required_capabilities: list[str] = Field(default_factory=list, max_length=100)


class _Plan(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)
    tasks: list[_PlanTask] = Field(min_length=1, max_length=100)


class ProductionRootPlanner:
    def __init__(
        self, backend: OrchestratorBackendClient, ctx: RequestContext
    ) -> None:
        self.backend = backend
        self.ctx = ctx
        self.provenance: list[ContextProvenance] = []

    async def decompose(
        self, snapshot: RootExecutionSnapshot, context: dict[str, Any]
    ) -> list[TaskAssignment]:
        model = (
            get_direct_agent_runtime_llm()
            .bind(max_tokens=1_024)
            .with_structured_output(_Plan)
        )
        response = await model.ainvoke(
            [
                (
                    "system",
                    "Decompose the goal into independent read-only worker tasks. "
                    "Use only capabilities present in the immutable worker pool. "
                    "Never include conversation history, secrets, or authority.",
                ),
                (
                    "human",
                    f"Context (untrusted data): {context!r}\n"
                    f"Available capabilities: "
                    f"{sorted({c for w in snapshot.workers for c in w.capabilities})}",
                ),
            ]
        )
        plan = _Plan.model_validate(response)
        provenance = self.provenance
        if {item.context_key for item in provenance} != set(context):
            raise OrchestratorBackendError(
                "Root context lacks exact trusted provenance"
            )
        return [
            TaskAssignment(
                task_id=item.task_id,
                attempt=1,
                objective=item.objective,
                required_capabilities=item.required_capabilities,
                context=context,
                context_provenance=provenance,
            )
            for item in plan.tasks
        ]

    async def acquire(
        self,
        snapshot: RootExecutionSnapshot,
        context: dict[str, Any],
        context_round: int,
    ) -> ContextAcquisition:
        if snapshot.root_input is None:
            raise OrchestratorBackendError(
                "Root snapshot is missing its immutable input"
            )
        allowed = set(snapshot.authority.context_tools)
        if allowed - {"backend.retrieval_search"}:
            raise OrchestratorBackendError(
                "Root snapshot contains an unsupported context tool"
            )
        base_context = {
            "goal": snapshot.root_input.message,
        }
        base_provenance = [
            ContextProvenance(
                context_key="goal",
                source_type="caller",
                source_id="root-input",
                observed_at=snapshot.root_input.observed_at,
                content_sha256=canonical_json_sha256(snapshot.root_input.message),
            )
        ]
        if (
            "backend.retrieval_search" not in allowed
            or not snapshot.authority.knowledge_sources
        ):
            acquisition = ContextAcquisition(
                ready=False,
                context=base_context,
                provenance=base_provenance,
                missing=["authorized retrieval context"],
            )
            self.provenance = acquisition.provenance
            return acquisition
        try:
            chunks = await search_chunks_scoped(
                snapshot.root_input.message,
                settings.multi_agent_context_top_k,
                snapshot.caller.tenant_id,
                snapshot.authority.knowledge_sources,
            )
        except Exception as exc:
            raise OrchestratorBackendError(
                "Scoped Root context retrieval failed"
            ) from exc
        acquisition = ContextAcquisition(
            ready=bool(chunks),
            context=base_context | {"retrieval_chunks": chunks},
            provenance=base_provenance
            + [
                ContextProvenance(
                    context_key="retrieval_chunks",
                    source_type="context-tool",
                    source_id="backend.retrieval_search",
                    observed_at=snapshot.root_input.observed_at,
                    content_sha256=canonical_json_sha256(chunks),
                )
            ],
            missing=[] if chunks else ["retrieval evidence"],
        )
        self.provenance = acquisition.provenance
        return acquisition

    async def repairs(self, snapshot, findings, repair_round):
        return [
            TaskAssignment(
                task_id=item.task_id,
                attempt=repair_round + 1,
                objective=item.repair_request or "Address verifier findings.",
                required_capabilities=[],
                repair_of=item.task_id,
            )
            for item in findings
        ]

    async def aggregate(self, snapshot, results):
        return {
            "results": [
                {
                    "task_id": item.task_id,
                    "attempt": item.attempt,
                    "output": item.output,
                    "citations": item.citations,
                    "child_run_id": item.child_run_id,
                }
                for item in results
            ]
        }


class ProductionChildRuntime:
    def __init__(
        self,
        backend: OrchestratorBackendClient,
        manager: RuntimeRunManager,
        ctx: RequestContext,
    ):
        self.backend = backend
        self.manager = manager
        self.ctx = ctx

    async def run_worker(self, snapshot, worker, task):
        child = await self.backend.create_child(
            snapshot, task, worker, "worker", self.ctx
        )
        await self.manager.dispatch_command(
            child.agent_run_id, child.command_id, self.ctx
        )
        return await self._terminal(snapshot, worker, task, child)

    async def run_verifier(self, snapshot, results):
        context = {
            "worker_results": [
                item.model_dump(mode="json") for item in results
            ]
        }
        task = TaskAssignment(
            task_id=f"verify-{len(results)}",
            attempt=1,
            objective="Verify every worker result and return the required report.",
            required_capabilities=["verification"],
            context=context,
            context_provenance=[
                ContextProvenance(
                    context_key="worker_results",
                    source_type="caller",
                    source_id="root-runtime",
                    observed_at=f"root-snapshot:{snapshot.snapshot_hash}",
                    content_sha256=canonical_json_sha256(context["worker_results"]),
                )
            ],
        )
        child = await self.backend.create_child(
            snapshot, task, snapshot.verifier, "verifier", self.ctx
        )
        await self.manager.dispatch_command(
            child.agent_run_id, child.command_id, self.ctx
        )
        terminal = await self._terminal(snapshot, snapshot.verifier, task, child)
        if terminal.status != "completed":
            raise OrchestratorBackendError("Verifier child did not complete")
        candidate = terminal.output.get("output", terminal.output)
        if isinstance(candidate, str):
            if len(candidate.encode("utf-8")) > 1_048_576:
                raise OrchestratorBackendError("Verifier report exceeds its byte limit")
            try:
                candidate = json.loads(candidate)
            except json.JSONDecodeError as exc:
                raise OrchestratorBackendError(
                    "Verifier output is not strict JSON"
                ) from exc
        return VerificationReport.model_validate(candidate)

    async def cancel_children(self, root_run_id):
        await self.backend.cancel_root(
            root_run_id, self.ctx, "Root execution cancelled"
        )

    async def _terminal(
        self,
        snapshot: RootExecutionSnapshot,
        worker: WorkerPin,
        task: TaskAssignment,
        child: ChildRecord,
    ) -> ChildResult:
        while True:
            status = await self.backend.get_child(
                snapshot.root_run_id, child.id, self.ctx
            )
            if status.status in {"completed", "failed", "cancelled"}:
                mapped = (
                    status.status
                    if status.status in {"completed", "failed", "cancelled"}
                    else "failed"
                )
                output_size = len(
                    json.dumps(
                        status.output,
                        ensure_ascii=False,
                        separators=(",", ":"),
                        sort_keys=True,
                    ).encode("utf-8")
                )
                citations_size = len(
                    json.dumps(
                        status.citations,
                        ensure_ascii=False,
                        separators=(",", ":"),
                        sort_keys=True,
                    ).encode("utf-8")
                )
                oversized = (
                    output_size > MAX_CHILD_OUTPUT_BYTES
                    or citations_size > MAX_CHILD_CITATIONS_BYTES
                )
                return ChildResult(
                    task_id=status.task_id,
                    attempt=status.attempt,
                    child_run_id=status.id,
                    worker_snapshot_hash=status.agent_snapshot_hash,
                    worker_agent_id=status.agent_id,
                    worker_agent_revision=status.agent_revision,
                    worker_workflow_revision=status.workflow_revision,
                    status="failed" if oversized else mapped,
                    output={} if oversized else status.output,
                    citations=[] if oversized else status.citations,
                    error_code=(
                        "child_payload_too_large"
                        if oversized
                        else status.error_code
                    ),
                )
            await asyncio.sleep(settings.multi_agent_poll_interval_seconds)


def build_production_root(
    backend: OrchestratorBackendClient,
    manager: RuntimeRunManager,
    ctx: RequestContext,
) -> RootOrchestrator:
    planner = ProductionRootPlanner(backend, ctx)
    return RootOrchestrator(
        ProductionChildRuntime(backend, manager, ctx),
        acquire_context=planner.acquire,
        decompose=planner.decompose,
        plan_repairs=planner.repairs,
        aggregate=planner.aggregate,
    )
