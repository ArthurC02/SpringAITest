"""Bounded Root Orchestrator runtime.

Backend remains the authority for immutable snapshots and durable root/child
records.  This module executes only the already-pinned plan through injected
adapters; it never discovers ``latest`` revisions or grants authority itself.
"""

from __future__ import annotations

import asyncio
import json
from collections.abc import Awaitable, Callable
from typing import Any, Literal, Protocol

from pydantic import BaseModel, ConfigDict, Field, field_validator, model_validator

from app.orchestration.validator import validate as validate_graph
from app.runtime.models import canonical_json_sha256
from app.workflow_contracts import GRAPH_IR_COMPILER_CONTRACT_VERSION

ROOT_RUNTIME_ADAPTER_VERSION = "root-runtime-adapter-1"
MAX_TASK_CONTEXT_BYTES = 65_536
MAX_CHILD_OUTPUT_BYTES = 131_072
MAX_CHILD_CITATIONS_BYTES = 65_536
MAX_AGGREGATE_BYTES = 524_288
MAX_ROOT_RESULT_BYTES = 983_040
PLANNER_PROVIDER_TOKEN_CAP = 1_024
REPAIR_PROVIDER_TOKEN_CAP = 1_024
AGGREGATE_PROVIDER_TOKEN_CAP = 1_024
_ROOT_ADAPTERS = {
    "start": "root.start.v1",
    "acquire_context_and_analyze_problem": "root.context.v1",
    "sufficiency_gate": "root.sufficiency.v1",
    "decompose_work": "root.decompose.v1",
    "dispatch_agents": "root.dispatch.v1",
    "join_worker_results": "root.join.v1",
    "invoke_verifier": "root.verify.v1",
    "bounded_repair": "root.repair.v1",
    "aggregate_results": "root.aggregate.v1",
    "respond": "root.respond.v1",
    "audit": "root.audit.v1",
    "end": "root.end.v1",
}
ROOT_RUNTIME_ADAPTER_SHA256 = canonical_json_sha256(_ROOT_ADAPTERS)


class _StrictModel(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)


class OrchestratorLimits(_StrictModel):
    max_context_rounds: int = Field(ge=1, le=20)
    max_tasks: int = Field(ge=1, le=100)
    max_child_runs: int = Field(ge=1, le=100)
    max_concurrency: int = Field(ge=1, le=32)
    max_repair_rounds: int = Field(ge=0, le=10)
    timeout_seconds: float = Field(gt=0, le=86_400)

    @model_validator(mode="after")
    def child_budget_covers_tasks(self) -> "OrchestratorLimits":
        if self.max_tasks + 1 > self.max_child_runs:
            raise ValueError(
                "max_child_runs must reserve one verifier beyond max_tasks"
            )
        return self


class WorkerPin(_StrictModel):
    agent_id: str = Field(min_length=1)
    agent_revision: int = Field(ge=1)
    workflow_id: str = Field(min_length=1)
    workflow_revision: int = Field(ge=1)
    snapshot_hash: str = Field(pattern=r"^[0-9a-f]{64}$")
    token_cap: int = Field(ge=1, le=1_000_000)
    capabilities: list[str] = Field(default_factory=list)
    read_only: bool = True
    skill_revisions: dict[str, int] = Field(default_factory=dict)


class CallerAuthority(_StrictModel):
    tenant_id: str = Field(min_length=1)
    user_id: str = Field(min_length=1)
    role: str = Field(min_length=1)
    groups: list[str] = Field(default_factory=list)
    tool_grants: list[str] = Field(default_factory=list)
    knowledge_grants: list[str] = Field(default_factory=list)


class EffectiveRootAuthority(_StrictModel):
    context_tools: list[str] = Field(default_factory=list)
    knowledge_sources: list[str] = Field(default_factory=list)
    rule_set_sha256: str = Field(pattern=r"^[0-9a-f]{64}$")
    policy_sha256: str = Field(pattern=r"^[0-9a-f]{64}$")


class RootInput(_StrictModel):
    message: str = Field(min_length=1, max_length=16_384)
    observed_at: str = Field(min_length=1, max_length=128)


class PublishedGraphProof(_StrictModel):
    definition: dict[str, Any]
    definition_sha256: str = Field(pattern=r"^[0-9a-f]{64}$")
    compiler_contract_version: str
    runtime_adapter_version: str
    runtime_adapter_sha256: str = Field(pattern=r"^[0-9a-f]{64}$")

    @model_validator(mode="after")
    def graph_and_adapter_are_server_proven(self) -> "PublishedGraphProof":
        if self.compiler_contract_version != GRAPH_IR_COMPILER_CONTRACT_VERSION:
            raise ValueError("unsupported Graph IR compiler contract")
        if self.runtime_adapter_version != ROOT_RUNTIME_ADAPTER_VERSION:
            raise ValueError("unsupported Root runtime adapter version")
        if self.runtime_adapter_sha256 != ROOT_RUNTIME_ADAPTER_SHA256:
            raise ValueError("Root runtime adapter hash does not match server mapping")
        outcome = validate_graph(self.definition)
        if (
            not outcome.valid
            or outcome.canonical_definition is None
            or outcome.definition_sha256 != self.definition_sha256
        ):
            raise ValueError("published orchestrator Graph IR proof is invalid")
        node_types = {
            str(node.get("type"))
            for node in outcome.canonical_definition.get("nodes", [])
        }
        if not node_types <= set(_ROOT_ADAPTERS):
            raise ValueError("Graph IR contains a node without a runtime adapter")
        return self


class RootExecutionSnapshot(_StrictModel):
    root_run_id: str = Field(min_length=1)
    snapshot_hash: str = Field(pattern=r"^[0-9a-f]{64}$")
    orchestrator_id: str = Field(min_length=1)
    orchestrator_revision: int = Field(ge=1)
    workflow_id: str = Field(min_length=1)
    workflow_revision: int = Field(ge=1)
    graph: PublishedGraphProof
    caller: CallerAuthority
    authority: EffectiveRootAuthority
    root_input: RootInput | None = Field(
        default=None, exclude_if=lambda value: value is None
    )
    workers: list[WorkerPin] = Field(min_length=1)
    verifier: WorkerPin
    join_policy: Literal["fail-fast", "allow-partial", "repair"]
    limits: OrchestratorLimits
    token_budget: int = Field(ge=1, le=10_000_000)
    context_byte_budget: int = Field(ge=1, le=10_000_000)
    business_rules: dict[str, Any]
    policies: dict[str, Any]

    @model_validator(mode="after")
    def verifier_is_independent_and_read_only(self) -> "RootExecutionSnapshot":
        worker_keys = {(item.agent_id, item.agent_revision) for item in self.workers}
        if (self.verifier.agent_id, self.verifier.agent_revision) in worker_keys:
            raise ValueError("verifier must be independent from every worker")
        if not self.verifier.read_only:
            raise ValueError("verifier must be read-only")
        if len(worker_keys) != len(self.workers):
            raise ValueError("worker pins must be unique")
        if canonical_json_sha256(self.business_rules) != self.authority.rule_set_sha256:
            raise ValueError("business rules do not match effective authority hash")
        if canonical_json_sha256(self.policies) != self.authority.policy_sha256:
            raise ValueError("policies do not match effective authority hash")
        canonical_payload = self.model_dump(mode="python", exclude={"snapshot_hash"})
        if canonical_json_sha256(canonical_payload) != self.snapshot_hash:
            raise ValueError("root snapshot hash does not match canonical snapshot")
        return self


class TaskAssignment(_StrictModel):
    task_id: str = Field(min_length=1)
    attempt: int = Field(ge=1)
    objective: str = Field(min_length=1, max_length=20_000)
    required_capabilities: list[str] = Field(default_factory=list)
    context: dict[str, Any] = Field(default_factory=dict)
    context_provenance: list["ContextProvenance"] = Field(default_factory=list)
    write_intent: bool = False
    delegation_depth: Literal[0] = 0
    repair_of: str | None = None

    @field_validator("context")
    @classmethod
    def context_is_a_minimal_envelope(cls, value: dict[str, Any]) -> dict[str, Any]:
        forbidden = {"messages", "conversation", "conversation_history", "memory"}
        if forbidden & {key.lower() for key in value}:
            raise ValueError("task context must not contain conversation or Agent memory")
        size = len(
            json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode()
        )
        if size > MAX_TASK_CONTEXT_BYTES:
            raise ValueError("task context exceeds its byte limit")
        return value

    @model_validator(mode="after")
    def context_has_typed_provenance(self) -> "TaskAssignment":
        context_keys = set(self.context)
        provenance_keys = {item.context_key for item in self.context_provenance}
        if context_keys != provenance_keys:
            raise ValueError("every task context field needs exactly one provenance")
        if len(provenance_keys) != len(self.context_provenance):
            raise ValueError("task context provenance keys must be unique")
        return self


class ContextProvenance(_StrictModel):
    context_key: str = Field(min_length=1)
    source_type: Literal["caller", "context-tool", "knowledge-source"]
    source_id: str = Field(min_length=1)
    observed_at: str = Field(min_length=1)
    content_sha256: str = Field(pattern=r"^[0-9a-f]{64}$")


class ChildResult(_StrictModel):
    task_id: str
    attempt: int = Field(ge=1)
    child_run_id: str
    worker_snapshot_hash: str
    worker_agent_id: str
    worker_agent_revision: int = Field(ge=1)
    worker_workflow_revision: int = Field(ge=1)
    status: Literal["completed", "failed", "cancelled", "timed_out"]
    output: dict[str, Any] = Field(default_factory=dict)
    citations: list[dict[str, Any]] = Field(default_factory=list)
    error_code: str | None = None

    @model_validator(mode="after")
    def bounded_wire_payload(self) -> "ChildResult":
        if _json_size(self.output) > MAX_CHILD_OUTPUT_BYTES:
            raise ValueError("child output exceeds its byte limit")
        if _json_size(self.citations) > MAX_CHILD_CITATIONS_BYTES:
            raise ValueError("child citations exceed their byte limit")
        return self


class VerificationItem(_StrictModel):
    task_id: str
    attempt: int = Field(ge=1)
    verdict: Literal["PASS", "FAIL", "NEEDS_REPAIR", "INSUFFICIENT_EVIDENCE"]
    evidence: list[dict[str, Any]] = Field(default_factory=list)
    repair_request: str | None = None


class VerificationReport(_StrictModel):
    items: list[VerificationItem]


class RootRunResult(_StrictModel):
    status: Literal["completed", "failed", "cancelled", "timed_out"]
    accepted_results: list[ChildResult]
    verification_report: VerificationReport | None = None
    aggregate: dict[str, Any] = Field(default_factory=dict)
    limitations: list[str] = Field(default_factory=list)
    audit: list[dict[str, Any]]

    @model_validator(mode="after")
    def bounded_backend_transition(self) -> "RootRunResult":
        if _json_size(self.model_dump(mode="json")) > MAX_ROOT_RESULT_BYTES:
            raise ValueError("Root result exceeds Backend transition byte limit")
        return self


class RootBudgetExceeded(RuntimeError):
    pass


class RootTokenLedger:
    """Conservative root-wide accounting.

    UTF-8 bytes are charged as token units.  This is deliberately conservative:
    a model-visible token cannot occupy less than one byte on the wire, while
    deterministic stages are charged by their complete canonical input/output.
    """

    def __init__(self, limit: int, audit: list[dict[str, Any]]) -> None:
        self.limit = limit
        self.used = 0
        self.audit = audit

    def charge(self, snapshot: RootExecutionSnapshot, stage: str, value: Any) -> None:
        amount = max(1, _json_size(value))
        if self.used + amount > self.limit:
            self.audit.append(
                RootOrchestrator._event(
                    snapshot,
                    "token_budget_exhausted",
                    stage=stage,
                    token_units_used=self.used,
                    token_units_requested=amount,
                    token_budget=self.limit,
                )
            )
            raise RootBudgetExceeded(stage)
        self.used += amount
        self.audit.append(
            RootOrchestrator._event(
                snapshot,
                "token_budget_charged",
                stage=stage,
                token_units=amount,
                token_units_used=self.used,
                token_budget=self.limit,
            )
        )

    def reserve(
        self, snapshot: RootExecutionSnapshot, stage: str, token_allowance: int
    ) -> None:
        """Atomically and permanently reserve a provider's maximum allowance."""
        if token_allowance < 1:
            raise ValueError("provider token reservation must be positive")
        if self.used + token_allowance > self.limit:
            self.audit.append(
                RootOrchestrator._event(
                    snapshot,
                    "token_budget_exhausted",
                    stage=stage,
                    token_units_used=self.used,
                    token_units_requested=token_allowance,
                    token_budget=self.limit,
                    accounting="provider-reservation",
                )
            )
            raise RootBudgetExceeded(stage)
        self.used += token_allowance
        self.audit.append(
            RootOrchestrator._event(
                snapshot,
                "token_budget_reserved",
                stage=stage,
                token_units=token_allowance,
                token_units_used=self.used,
                token_budget=self.limit,
                accounting="provider-reservation",
            )
        )


class ContextAcquisition(_StrictModel):
    ready: bool
    context: dict[str, Any] = Field(default_factory=dict)
    provenance: list[ContextProvenance] = Field(default_factory=list)
    missing: list[str] = Field(default_factory=list)


class ChildRuntime(Protocol):
    async def run_worker(
        self, snapshot: RootExecutionSnapshot, worker: WorkerPin, task: TaskAssignment
    ) -> ChildResult: ...

    async def run_verifier(
        self,
        snapshot: RootExecutionSnapshot,
        results: list[ChildResult],
    ) -> VerificationReport: ...

    async def cancel_children(self, root_run_id: str) -> None: ...


Decomposer = Callable[
    [RootExecutionSnapshot, dict[str, Any]], Awaitable[list[TaskAssignment]]
]
ContextAcquirer = Callable[
    [RootExecutionSnapshot, dict[str, Any], int], Awaitable[ContextAcquisition]
]
RepairPlanner = Callable[
    [RootExecutionSnapshot, list[VerificationItem], int],
    Awaitable[list[TaskAssignment]],
]
Aggregator = Callable[
    [RootExecutionSnapshot, list[ChildResult]], Awaitable[dict[str, Any]]
]


class RootOrchestrator:
    def __init__(
        self,
        runtime: ChildRuntime,
        *,
        acquire_context: ContextAcquirer,
        decompose: Decomposer,
        plan_repairs: RepairPlanner,
        aggregate: Aggregator,
    ) -> None:
        self._runtime = runtime
        self._acquire_context = acquire_context
        self._decompose = decompose
        self._plan_repairs = plan_repairs
        self._aggregate = aggregate

    async def execute(
        self,
        snapshot: RootExecutionSnapshot,
        context: dict[str, Any],
        *,
        cancel_on_task_cancel: bool = True,
    ) -> RootRunResult:
        audit = [self._event(snapshot, "root_started")]
        ledger = RootTokenLedger(snapshot.token_budget, audit)
        try:
            async with asyncio.timeout(snapshot.limits.timeout_seconds):
                return await self._execute(
                    snapshot,
                    context,
                    audit,
                    ledger,
                    cancel_on_task_cancel=cancel_on_task_cancel,
                )
        except RootBudgetExceeded as exc:
            return self._failed(
                audit, [], None, f"root token budget exhausted at {exc}"
            )
        except asyncio.CancelledError:
            if not cancel_on_task_cancel:
                raise
            await asyncio.shield(self._runtime.cancel_children(snapshot.root_run_id))
            audit.append(self._event(snapshot, "root_cancelled"))
            return RootRunResult(
                status="cancelled", accepted_results=[], audit=audit
            )
        except TimeoutError:
            await asyncio.shield(self._runtime.cancel_children(snapshot.root_run_id))
            audit.append(self._event(snapshot, "root_timed_out"))
            return RootRunResult(
                status="timed_out", accepted_results=[], audit=audit
            )

    async def _execute(
        self,
        snapshot: RootExecutionSnapshot,
        context: dict[str, Any],
        audit: list[dict[str, Any]],
        ledger: RootTokenLedger,
        *,
        cancel_on_task_cancel: bool,
    ) -> RootRunResult:
        acquired = context
        for context_round in range(1, snapshot.limits.max_context_rounds + 1):
            acquisition = await self._acquire_context(
                snapshot, acquired, context_round
            )
            audit.append(
                self._event(
                    snapshot,
                    "context_assessed",
                    context_round=context_round,
                    ready=acquisition.ready,
                    missing_count=len(acquisition.missing),
                )
            )
            acquired = acquisition.context
            ledger.charge(snapshot, "context", acquisition.model_dump(mode="json"))
            acquired_size = len(
                json.dumps(
                    acquired, ensure_ascii=False, separators=(",", ":")
                ).encode()
            )
            if acquired_size > snapshot.context_byte_budget:
                return self._failed(
                    audit, [], None, "context byte budget exhausted"
                )
            if acquisition.ready:
                break
        else:
            return self._failed(
                audit, [], None, "context remained insufficient"
            )

        ledger.reserve(snapshot, "planner", PLANNER_PROVIDER_TOKEN_CAP)
        tasks = await self._decompose(snapshot, acquired)
        self._validate_tasks(snapshot, tasks)
        child_count = 0
        results: list[ChildResult] = []
        report: VerificationReport | None = None

        for repair_round in range(snapshot.limits.max_repair_rounds + 1):
            if child_count + len(tasks) + 1 > snapshot.limits.max_child_runs:
                return self._failed(audit, results, report, "child budget exhausted")
            selected_workers = [
                self._select_worker(snapshot, task) for task in tasks
            ]
            ledger.reserve(
                snapshot,
                "workers",
                sum(worker.token_cap for worker in selected_workers),
            )
            child_count += len(tasks)
            dispatched = await self._dispatch(
                snapshot,
                tasks,
                audit,
                cancel_on_task_cancel=cancel_on_task_cancel,
            )
            results.extend(dispatched)
            failed = [item for item in dispatched if item.status != "completed"]
            if failed and snapshot.join_policy == "fail-fast":
                await self._runtime.cancel_children(snapshot.root_run_id)
                return self._failed(audit, results, report, "worker child failed")

            completed = [item for item in results if item.status == "completed"]
            if not completed:
                return self._failed(audit, results, report, "no worker result completed")
            child_count += 1
            ledger.reserve(snapshot, "verifier", snapshot.verifier.token_cap)
            report = await self._runtime.run_verifier(snapshot, completed)
            self._validate_report(completed, report)
            audit.append(
                self._event(
                    snapshot,
                    "verification_completed",
                    verifier_snapshot_hash=snapshot.verifier.snapshot_hash,
                    verdicts=[item.verdict for item in report.items],
                )
            )
            by_task = {(item.task_id, item.attempt): item for item in report.items}
            accepted = [
                result
                for result in completed
                if by_task.get((result.task_id, result.attempt))
                and by_task[(result.task_id, result.attempt)].verdict == "PASS"
            ]
            repair_items = [
                item for item in report.items if item.verdict == "NEEDS_REPAIR"
            ]
            if not repair_items:
                ledger.reserve(
                    snapshot, "aggregate", AGGREGATE_PROVIDER_TOKEN_CAP
                )
                aggregate = await self._aggregate(snapshot, accepted)
                if _json_size(aggregate) > MAX_AGGREGATE_BYTES:
                    return self._failed(
                        audit, [], report, "aggregate output exceeds its byte limit"
                    )
                limitations = [
                    item.task_id
                    for item in report.items
                    if item.verdict != "PASS"
                ]
                audit.append(self._event(snapshot, "root_completed"))
                return RootRunResult(
                    status="completed",
                    accepted_results=[self._lineage_only(item) for item in accepted],
                    verification_report=report,
                    aggregate=aggregate,
                    limitations=limitations,
                    audit=audit,
                )
            if repair_round >= snapshot.limits.max_repair_rounds:
                return self._failed(
                    audit, accepted, report, "repair budget exhausted"
                )
            ledger.reserve(
                snapshot, "repair_planner", REPAIR_PROVIDER_TOKEN_CAP
            )
            tasks = await self._plan_repairs(snapshot, repair_items, repair_round + 1)
            self._validate_tasks(snapshot, tasks)

        raise AssertionError("bounded repair loop escaped")

    async def _dispatch(
        self,
        snapshot: RootExecutionSnapshot,
        tasks: list[TaskAssignment],
        audit: list[dict[str, Any]],
        *,
        cancel_on_task_cancel: bool,
    ) -> list[ChildResult]:
        semaphore = asyncio.Semaphore(snapshot.limits.max_concurrency)

        async def run(task: TaskAssignment) -> ChildResult:
            worker = self._select_worker(snapshot, task)
            async with semaphore:
                result = await self._runtime.run_worker(snapshot, worker, task)
            if result.task_id != task.task_id:
                raise ValueError("child result task ID does not match its assignment")
            if result.attempt != task.attempt:
                raise ValueError("child result attempt does not match its assignment")
            if result.worker_snapshot_hash != worker.snapshot_hash:
                raise ValueError("child result snapshot hash does not match its pin")
            if (
                result.worker_agent_id != worker.agent_id
                or result.worker_agent_revision != worker.agent_revision
                or result.worker_workflow_revision != worker.workflow_revision
            ):
                raise ValueError("child result worker pin does not match its assignment")
            audit.append(
                self._event(
                    snapshot,
                    "child_finished",
                    task_id=task.task_id,
                    child_run_id=result.child_run_id,
                    agent_id=worker.agent_id,
                    agent_revision=worker.agent_revision,
                    workflow_revision=worker.workflow_revision,
                    child_snapshot_hash=result.worker_snapshot_hash,
                    status=result.status,
                )
            )
            return result

        try:
            return list(await asyncio.gather(*(run(task) for task in tasks)))
        except asyncio.CancelledError:
            raise
        except BaseException:
            # Coroutines cancelled by gather are not sufficient when the
            # adapter has already created durable Backend child runs.
            await asyncio.shield(
                self._runtime.cancel_children(snapshot.root_run_id)
            )
            raise

    @staticmethod
    def _select_worker(
        snapshot: RootExecutionSnapshot, task: TaskAssignment
    ) -> WorkerPin:
        required = set(task.required_capabilities)
        eligible = [
            worker
            for worker in snapshot.workers
            if required <= set(worker.capabilities)
        ]
        if not eligible:
            raise ValueError(f"no pinned worker satisfies task {task.task_id}")
        return eligible[0]

    @staticmethod
    def _validate_tasks(
        snapshot: RootExecutionSnapshot, tasks: list[TaskAssignment]
    ) -> None:
        if not tasks or len(tasks) > snapshot.limits.max_tasks:
            raise ValueError("task plan is empty or exceeds max_tasks")
        ids = [(item.task_id, item.attempt) for item in tasks]
        if len(ids) != len(set(ids)):
            raise ValueError("task IDs must be unique")
        if len(tasks) > 1 and any(item.write_intent for item in tasks):
            raise ValueError("parallel write tasks are forbidden in v1")

    @staticmethod
    def _validate_report(
        results: list[ChildResult], report: VerificationReport
    ) -> None:
        expected = [(item.task_id, item.attempt) for item in results]
        actual = [(item.task_id, item.attempt) for item in report.items]
        if len(actual) != len(set(actual)) or set(actual) != set(expected):
            raise ValueError(
                "verification report must cover each completed child exactly once"
            )

    @staticmethod
    def _lineage_only(result: ChildResult) -> ChildResult:
        return result.model_copy(update={"output": {}, "citations": []})

    @staticmethod
    def _event(
        snapshot: RootExecutionSnapshot, event_type: str, **fields: Any
    ) -> dict[str, Any]:
        return {
            "event_type": event_type,
            "root_run_id": snapshot.root_run_id,
            "snapshot_hash": snapshot.snapshot_hash,
            "orchestrator_revision": snapshot.orchestrator_revision,
            "workflow_revision": snapshot.workflow_revision,
            **fields,
        }

    @staticmethod
    def _failed(
        audit: list[dict[str, Any]],
        results: list[ChildResult],
        report: VerificationReport | None,
        limitation: str,
    ) -> RootRunResult:
        return RootRunResult(
            status="failed",
            accepted_results=results,
            verification_report=report,
            limitations=[limitation],
            audit=audit,
        )


def _json_size(value: Any) -> int:
    return len(
        json.dumps(
            value, ensure_ascii=False, separators=(",", ":"), sort_keys=True
        ).encode("utf-8")
    )
