from __future__ import annotations

import asyncio
import hashlib
from typing import Any

import pytest

from app.runtime.orchestrator import (
    ChildResult,
    ContextProvenance,
    ContextAcquisition,
    CallerAuthority,
    EffectiveRootAuthority,
    OrchestratorLimits,
    RootExecutionSnapshot,
    RootOrchestrator,
    TaskAssignment,
    VerificationItem,
    VerificationReport,
    WorkerPin,
    PublishedGraphProof,
    ROOT_RUNTIME_ADAPTER_SHA256,
    ROOT_RUNTIME_ADAPTER_VERSION,
)
from app.orchestration.validator import validate
from app.runtime.models import canonical_json_bytes, canonical_json_sha256, parse_json_preserving_numbers
from app.workflow_contracts import GRAPH_IR_COMPILER_CONTRACT_VERSION


def _hash(value: str) -> str:
    return hashlib.sha256(value.encode()).hexdigest()


def _snapshot(**limits: Any) -> RootExecutionSnapshot:
    graph = _orchestrator_graph()
    graph_result = validate(graph)
    rules: dict[str, Any] = {"version": 1, "rules": []}
    policies = {"dispatchMode": "bounded-parallel"}
    payload = dict(
        root_run_id="root-1",
        orchestrator_id="orchestrator-1",
        orchestrator_revision=2,
        workflow_id="root-workflow",
        workflow_revision=5,
        graph=PublishedGraphProof(
            definition=graph,
            definition_sha256=graph_result.definition_sha256,
            compiler_contract_version=GRAPH_IR_COMPILER_CONTRACT_VERSION,
            runtime_adapter_version=ROOT_RUNTIME_ADAPTER_VERSION,
            runtime_adapter_sha256=ROOT_RUNTIME_ADAPTER_SHA256,
        ),
        caller=CallerAuthority(
            tenant_id="tenant", user_id="user", role="USER",
            tool_grants=["retrieve"], knowledge_grants=["docs"],
        ),
        authority=EffectiveRootAuthority(
            context_tools=["retrieve"],
            knowledge_sources=["docs"],
            rule_set_sha256=canonical_json_sha256(rules),
            policy_sha256=canonical_json_sha256(policies),
        ),
        workers=[
            WorkerPin(
                agent_id="researcher",
                agent_revision=3,
                workflow_id="worker-flow",
                workflow_revision=4,
                snapshot_hash=_hash("worker"),
                token_cap=1_000,
                capabilities=["research"],
            )
        ],
        verifier=WorkerPin(
            agent_id="verifier",
            agent_revision=7,
            workflow_id="verifier-flow",
            workflow_revision=2,
            snapshot_hash=_hash("verifier"),
            token_cap=1_000,
            capabilities=["verification"],
        ),
        join_policy="repair",
        limits=OrchestratorLimits(
            max_context_rounds=2,
            max_tasks=4,
            max_child_runs=5,
            max_concurrency=2,
            max_repair_rounds=1,
            timeout_seconds=limits.get("timeout_seconds", 2),
        ),
        token_budget=limits.get("token_budget", 10_000),
        context_byte_budget=100_000,
        business_rules=rules,
        policies=policies,
    )
    canonical_payload = RootExecutionSnapshot.model_construct(
        snapshot_hash="0" * 64, **payload
    ).model_dump(mode="python", exclude={"snapshot_hash"})
    return RootExecutionSnapshot(
        snapshot_hash=canonical_json_sha256(canonical_payload), **payload
    )


def _orchestrator_graph() -> dict[str, Any]:
    stages = [
        ("start", "start", {}),
        ("context", "acquire_context_and_analyze_problem", {}),
        ("sufficiency", "sufficiency_gate", {}),
        ("decompose", "decompose_work", {}),
        ("dispatch", "dispatch_agents", {}),
        ("join", "join_worker_results", {}),
        ("verify", "invoke_verifier", {}),
        ("repair", "bounded_repair", {"maxIterations": 2}),
        ("aggregate", "aggregate_results", {}),
        ("respond", "respond", {}),
        ("audit", "audit", {}),
        ("end", "end", {}),
    ]
    return {
        "schemaVersion": 1,
        "kind": "orchestrator",
        "nodes": [
            {"id": node_id, "type": node_type, "typeVersion": "1.0", "config": config}
            for node_id, node_type, config in stages
        ],
        "edges": [
            {"id": f"e{i}", "source": {"nodeId": stages[i][0], "port": "out"}, "target": {"nodeId": stages[i + 1][0], "port": "in"}}
            for i in range(len(stages) - 1)
        ] + [
            {"id": "tasks", "source": {"nodeId": "decompose", "port": "tasks"}, "target": {"nodeId": "dispatch", "port": "tasks"}},
            {"id": "results", "source": {"nodeId": "dispatch", "port": "results"}, "target": {"nodeId": "join", "port": "results"}},
        ],
        "governance": {"maxSteps": 40, "maxConcurrency": 4},
    }


class FakeRuntime:
    def __init__(self) -> None:
        self.active = 0
        self.max_active = 0
        self.cancelled = False
        self.verifications = 0

    async def run_worker(self, snapshot, worker, task):
        self.active += 1
        self.max_active = max(self.max_active, self.active)
        await asyncio.sleep(0.01)
        self.active -= 1
        return ChildResult(
            task_id=task.task_id,
            attempt=task.attempt,
            child_run_id=f"child-{task.task_id}",
            worker_snapshot_hash=worker.snapshot_hash,
            worker_agent_id=worker.agent_id,
            worker_agent_revision=worker.agent_revision,
            worker_workflow_revision=worker.workflow_revision,
            status="completed",
            output={"task": task.task_id},
        )

    async def run_verifier(self, snapshot, results):
        self.verifications += 1
        return VerificationReport(
            items=[
                VerificationItem(
                    task_id=item.task_id,
                    attempt=item.attempt,
                    verdict=(
                        "NEEDS_REPAIR"
                        if self.verifications == 1 and item.task_id == "a"
                        else "PASS"
                    ),
                    repair_request="more evidence"
                    if self.verifications == 1 and item.task_id == "a"
                    else None,
                )
                for item in results
            ]
        )

    async def cancel_children(self, root_run_id):
        self.cancelled = True


async def _decompose(snapshot, context):
    return [
        TaskAssignment(
            task_id=task_id,
            attempt=1,
            objective=f"research {task_id}",
            required_capabilities=["research"],
            context={"document_id": task_id},
            context_provenance=[
                ContextProvenance(
                    context_key="document_id",
                    source_type="caller",
                    source_id="request",
                    observed_at="2026-01-01T00:00:00Z",
                    content_sha256=_hash(task_id),
                )
            ],
        )
        for task_id in ("a", "b")
    ]


async def _repairs(snapshot, findings, repair_round):
    return [
        TaskAssignment(
            task_id=f"{item.task_id}-repair-{repair_round}",
            attempt=repair_round + 1,
            objective=item.repair_request or "repair",
            required_capabilities=["research"],
            repair_of=item.task_id,
        )
        for item in findings
    ]


async def _aggregate(snapshot, results):
    return {"tasks": [item.task_id for item in results]}


async def _acquire(snapshot, context, context_round):
    return ContextAcquisition(ready=True, context=context)


@pytest.mark.asyncio
async def test_bounded_parallel_dispatch_verify_repair_and_pass_only_aggregate():
    runtime = FakeRuntime()
    result = await RootOrchestrator(
        runtime,
        acquire_context=_acquire,
        decompose=_decompose,
        plan_repairs=_repairs,
        aggregate=_aggregate,
    ).execute(_snapshot(), {"goal": "compare"})

    assert result.status == "completed"
    assert runtime.max_active == 2
    assert result.aggregate == {"tasks": ["a", "b", "a-repair-1"]}
    assert all(item.status == "completed" for item in result.accepted_results)
    child_events = [
        event for event in result.audit if event["event_type"] == "child_finished"
    ]
    assert child_events[0]["snapshot_hash"] == _snapshot().snapshot_hash
    assert child_events[0]["child_snapshot_hash"] == _hash("worker")


@pytest.mark.asyncio
async def test_parent_cancellation_propagates_to_children():
    runtime = FakeRuntime()

    async def slow_decompose(snapshot, context):
        await asyncio.sleep(10)
        return []

    execution = asyncio.create_task(
        RootOrchestrator(
            runtime,
            acquire_context=_acquire,
            decompose=slow_decompose,
            plan_repairs=_repairs,
            aggregate=_aggregate,
        ).execute(_snapshot(), {})
    )
    await asyncio.sleep(0)
    execution.cancel()
    result = await execution

    assert result.status == "cancelled"
    assert runtime.cancelled


@pytest.mark.asyncio
async def test_parallel_write_tasks_fail_closed_before_dispatch():
    runtime = FakeRuntime()

    async def writes(snapshot, context):
        return [
            TaskAssignment(task_id="a", attempt=1, objective="write", write_intent=True),
            TaskAssignment(task_id="b", attempt=1, objective="write", write_intent=False),
        ]

    with pytest.raises(ValueError, match="parallel write"):
        await RootOrchestrator(
            runtime,
            acquire_context=_acquire,
            decompose=writes,
            plan_repairs=_repairs,
            aggregate=_aggregate,
        ).execute(_snapshot(), {})


def test_verifier_must_be_independent_and_read_only():
    snapshot = _snapshot()
    verifier = snapshot.workers[0].model_copy(update={"read_only": False})
    with pytest.raises(ValueError, match="independent"):
        snapshot.model_copy(update={"verifier": verifier}).model_validate(
            snapshot.model_dump() | {"verifier": verifier.model_dump()}
        )


def test_graph_adapter_and_root_snapshot_hash_proofs_fail_closed():
    snapshot = _snapshot()
    raw = snapshot.model_dump()
    raw["graph"]["runtime_adapter_sha256"] = _hash("forged")
    with pytest.raises(ValueError, match="adapter hash"):
        RootExecutionSnapshot.model_validate(raw)

    raw = snapshot.model_dump()
    raw["token_budget"] += 1
    with pytest.raises(ValueError, match="snapshot hash"):
        RootExecutionSnapshot.model_validate(raw)


def test_backend_style_integer_timeout_preserves_root_snapshot_hash():
    """Backend emits its integer `budgets.timeoutSeconds` unchanged in the snapshot."""
    raw = _snapshot().model_dump(mode="json")
    raw["limits"]["timeout_seconds"] = 90
    unsigned = {key: value for key, value in raw.items() if key != "snapshot_hash"}
    raw["snapshot_hash"] = canonical_json_sha256(unsigned)

    restored = RootExecutionSnapshot.model_validate(raw)

    assert restored.limits.timeout_seconds == 90
    assert restored.snapshot_hash == raw["snapshot_hash"]


def test_root_snapshot_accepts_preserved_canonical_integer_tokens():
    raw = _snapshot().model_dump(mode="json")
    raw["limits"]["timeout_seconds"] = 90
    unsigned = {key: value for key, value in raw.items() if key != "snapshot_hash"}
    raw["snapshot_hash"] = canonical_json_sha256(unsigned)

    preserved = parse_json_preserving_numbers(canonical_json_bytes(raw))
    restored = RootExecutionSnapshot.from_preserved_json(preserved)

    assert restored.limits.timeout_seconds == 90
    assert restored.snapshot_hash == raw["snapshot_hash"]


def test_verifier_requires_exact_unique_task_attempt_coverage():
    snapshot = _snapshot()
    result = ChildResult(
        task_id="a",
        attempt=2,
        child_run_id="child-a",
        worker_snapshot_hash=snapshot.workers[0].snapshot_hash,
        worker_agent_id=snapshot.workers[0].agent_id,
        worker_agent_revision=snapshot.workers[0].agent_revision,
        worker_workflow_revision=snapshot.workers[0].workflow_revision,
        status="completed",
    )
    duplicate = VerificationReport(
        items=[
            VerificationItem(task_id="a", attempt=2, verdict="PASS"),
            VerificationItem(task_id="a", attempt=2, verdict="PASS"),
        ]
    )
    with pytest.raises(ValueError, match="exactly once"):
        RootOrchestrator._validate_report([result], duplicate)


@pytest.mark.asyncio
async def test_context_acquisition_is_bounded_before_decomposition():
    runtime = FakeRuntime()
    decomposed = False

    async def insufficient(snapshot, context, context_round):
        return ContextAcquisition(
            ready=False, context=context, missing=["required document"]
        )

    async def should_not_run(snapshot, context):
        nonlocal decomposed
        decomposed = True
        return []

    result = await RootOrchestrator(
        runtime,
        acquire_context=insufficient,
        decompose=should_not_run,
        plan_repairs=_repairs,
        aggregate=_aggregate,
    ).execute(_snapshot(), {})

    assert result.status == "waiting_input"
    assert result.limitations == ["context remained insufficient"]
    assert not decomposed
    assert len(
        [item for item in result.audit if item["event_type"] == "context_assessed"]
    ) == 1


def test_task_envelope_rejects_conversation_and_delegation():
    with pytest.raises(ValueError, match="conversation"):
        TaskAssignment(
            task_id="a", attempt=1, objective="work", context={"messages": ["secret"]}
        )
    with pytest.raises(ValueError):
        TaskAssignment(task_id="a", attempt=1, objective="work", delegation_depth=1)


@pytest.mark.asyncio
async def test_child_failure_cancels_durable_siblings():
    runtime = FakeRuntime()

    async def fail(snapshot, worker, task):
        raise RuntimeError("child dispatch failed")

    runtime.run_worker = fail
    with pytest.raises(RuntimeError, match="child dispatch"):
        await RootOrchestrator(
            runtime,
            acquire_context=_acquire,
            decompose=_decompose,
            plan_repairs=_repairs,
            aggregate=_aggregate,
        ).execute(_snapshot(), {})

    assert runtime.cancelled


@pytest.mark.asyncio
async def test_root_token_ledger_exhaustion_is_controlled_and_audited():
    result = await RootOrchestrator(
        FakeRuntime(),
        acquire_context=_acquire,
        decompose=_decompose,
        plan_repairs=_repairs,
        aggregate=_aggregate,
    ).execute(_snapshot(token_budget=10), {"goal": "compare"})

    assert result.status == "failed"
    assert "root token budget exhausted" in result.limitations[0]
    exhausted = [
        item for item in result.audit
        if item["event_type"] == "token_budget_exhausted"
    ]
    assert exhausted
    assert exhausted[0]["token_budget"] == 10


@pytest.mark.asyncio
async def test_multi_child_provider_reservations_exhaust_before_dispatch():
    runtime = FakeRuntime()
    calls = 0
    original = runtime.run_worker

    async def counted(snapshot, worker, task):
        nonlocal calls
        calls += 1
        return await original(snapshot, worker, task)

    runtime.run_worker = counted
    result = await RootOrchestrator(
        runtime,
        acquire_context=_acquire,
        decompose=_decompose,
        plan_repairs=_repairs,
        aggregate=_aggregate,
    ).execute(_snapshot(token_budget=2_500), {})

    assert result.status == "failed"
    assert result.limitations == ["root token budget exhausted at workers"]
    assert calls == 0
    reservation = [
        item for item in result.audit
        if item["event_type"] == "token_budget_exhausted"
    ][0]
    assert reservation["accounting"] == "provider-reservation"
    assert reservation["token_units_requested"] == 2_000


@pytest.mark.asyncio
async def test_aggregate_oversize_fails_without_emitting_oversize_root_result():
    async def oversized(snapshot, results):
        return {"answer": "x" * 600_000}

    result = await RootOrchestrator(
        FakeRuntime(),
        acquire_context=_acquire,
        decompose=_decompose,
        plan_repairs=_repairs,
        aggregate=oversized,
    ).execute(_snapshot(), {"goal": "compare"})

    assert result.status == "failed"
    assert result.limitations == ["aggregate output exceeds its byte limit"]
    assert result.aggregate == {}
    assert len(str(result.model_dump()).encode("utf-8")) < 1_048_576


def test_child_output_and_citations_are_bounded():
    snapshot = _snapshot()
    common = dict(
        task_id="a",
        attempt=1,
        child_run_id="child-a",
        worker_snapshot_hash=snapshot.workers[0].snapshot_hash,
        worker_agent_id=snapshot.workers[0].agent_id,
        worker_agent_revision=snapshot.workers[0].agent_revision,
        worker_workflow_revision=snapshot.workers[0].workflow_revision,
        status="completed",
    )
    with pytest.raises(ValueError, match="child output"):
        ChildResult(**common, output={"value": "x" * 140_000})
    with pytest.raises(ValueError, match="child citations"):
        ChildResult(**common, citations=[{"value": "x" * 70_000}])
