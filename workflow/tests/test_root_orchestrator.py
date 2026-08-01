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
    RootRunResult,
    TaskAssignment,
    VerificationItem,
    VerificationReport,
    WorkerPin,
    PublishedGraphProof,
    MAX_CHILD_CITATIONS_BYTES,
    MAX_CHILD_OUTPUT_BYTES,
    MAX_ROOT_RESULT_BYTES,
    MAX_TASK_CONTEXT_BYTES,
    ROOT_RUNTIME_ADAPTER_SHA256,
    ROOT_RUNTIME_ADAPTER_VERSION,
    _json_size,
)
from app.orchestration.validator import validate
from tests.test_orchestration_graph_ir import agent_runtime_graph, orchestrator_graph
from app.runtime.models import canonical_json_bytes, canonical_json_sha256, parse_json_preserving_numbers
from app.workflow_contracts import GRAPH_IR_COMPILER_CONTRACT_VERSION


def _hash(value: str) -> str:
    return hashlib.sha256(value.encode()).hexdigest()


def _snapshot(**limits: Any) -> RootExecutionSnapshot:
    graph = orchestrator_graph()
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
        join_policy=limits.get("join_policy", "repair"),
        limits=OrchestratorLimits(
            max_context_rounds=2,
            max_tasks=limits.get("max_tasks", 4),
            max_child_runs=limits.get("max_child_runs", 5),
            max_concurrency=limits.get("max_concurrency", 2),
            max_repair_rounds=limits.get("max_repair_rounds", 1),
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
    snapshot = _snapshot()
    result = await RootOrchestrator(
        runtime,
        acquire_context=_acquire,
        decompose=_decompose,
        plan_repairs=_repairs,
        aggregate=_aggregate,
    ).execute(snapshot, {"goal": "compare"})

    assert result.status == "completed"
    assert runtime.verifications == 2
    assert result.aggregate == {"tasks": ["a", "b", "a-repair-1"]}
    assert [item.verdict for item in result.verification_report.items] == ["PASS"] * 3
    assert result.limitations == []
    assert all(item.status == "completed" for item in result.accepted_results)
    # Accepted results travel to Backend as lineage only.
    assert all(
        item.output == {} and item.citations == []
        for item in result.accepted_results
    )
    child_events = [
        event for event in result.audit if event["event_type"] == "child_finished"
    ]
    assert child_events[0]["snapshot_hash"] == snapshot.snapshot_hash
    assert child_events[0]["child_snapshot_hash"] == _hash("worker")


@pytest.mark.asyncio
@pytest.mark.parametrize("max_concurrency", [2, 3])
async def test_dispatch_never_exceeds_max_concurrency_with_more_tasks_than_slots(
    max_concurrency,
):
    """Three tasks against two slots: deleting the dispatch semaphore must fail."""
    runtime = FakeRuntime()

    async def three_tasks(snapshot, context):
        return [
            TaskAssignment(task_id=task_id, attempt=1, objective="research")
            for task_id in ("a", "b", "c")
        ]

    result = await RootOrchestrator(
        runtime,
        acquire_context=_acquire,
        decompose=three_tasks,
        plan_repairs=_repairs,
        aggregate=_aggregate,
    ).execute(
        _snapshot(
            max_concurrency=max_concurrency,
            max_child_runs=8,
            token_budget=100_000,
        ),
        {"goal": "compare"},
    )

    assert result.status == "completed"
    assert runtime.max_active == max_concurrency


@pytest.mark.asyncio
@pytest.mark.parametrize("verdict", ["FAIL", "INSUFFICIENT_EVIDENCE"])
async def test_single_failed_verdict_is_excluded_from_pass_only_aggregate(verdict):
    class MixedVerdictRuntime(FakeRuntime):
        async def run_verifier(self, snapshot, results):
            self.verifications += 1
            return VerificationReport(
                items=[
                    VerificationItem(
                        task_id=item.task_id,
                        attempt=item.attempt,
                        verdict="PASS" if item.task_id == "a" else verdict,
                    )
                    for item in results
                ]
            )

    runtime = MixedVerdictRuntime()
    result = await RootOrchestrator(
        runtime,
        acquire_context=_acquire,
        decompose=_decompose,
        plan_repairs=_repairs,
        aggregate=_aggregate,
    ).execute(_snapshot(), {"goal": "compare"})

    assert result.status == "completed"
    # A non-PASS verdict never turns into a repair round and never aggregates.
    assert runtime.verifications == 1
    assert [item.task_id for item in result.accepted_results] == ["a"]
    assert result.aggregate == {"tasks": ["a"]}
    assert result.limitations == ["b"]


@pytest.mark.asyncio
async def test_repair_budget_exhaustion_fails_without_aggregating():
    aggregated = False

    class NeverRepairedRuntime(FakeRuntime):
        async def run_verifier(self, snapshot, results):
            self.verifications += 1
            return VerificationReport(
                items=[
                    VerificationItem(
                        task_id=item.task_id,
                        attempt=item.attempt,
                        verdict="NEEDS_REPAIR",
                        repair_request="more evidence",
                    )
                    for item in results
                ]
            )

    async def never_aggregate(snapshot, results):
        nonlocal aggregated
        aggregated = True
        return {}

    runtime = NeverRepairedRuntime()
    result = await RootOrchestrator(
        runtime,
        acquire_context=_acquire,
        decompose=_decompose,
        plan_repairs=_repairs,
        aggregate=never_aggregate,
    ).execute(
        _snapshot(max_repair_rounds=1, max_child_runs=8, token_budget=100_000),
        {"goal": "compare"},
    )

    assert result.status == "failed"
    assert result.limitations == ["repair budget exhausted"]
    assert runtime.verifications == 2
    assert not aggregated


@pytest.mark.asyncio
async def test_zero_repair_rounds_fails_on_the_very_first_needs_repair_verdict():
    """max_repair_rounds=0 is the documented lower bound: no repair round at all."""
    planned = False

    async def never_plan(snapshot, findings, repair_round):
        nonlocal planned
        planned = True
        return []

    runtime = FakeRuntime()
    result = await RootOrchestrator(
        runtime,
        acquire_context=_acquire,
        decompose=_decompose,
        plan_repairs=never_plan,
        aggregate=_aggregate,
    ).execute(_snapshot(max_repair_rounds=0), {"goal": "compare"})

    assert result.status == "failed"
    assert result.limitations == ["repair budget exhausted"]
    assert runtime.verifications == 1
    assert not planned
    # The PASSing sibling is still reported as accepted lineage on the failure.
    assert [item.task_id for item in result.accepted_results] == ["b"]


@pytest.mark.asyncio
@pytest.mark.parametrize(
    "max_child_runs, status, limitations",
    [(6, "completed", []), (5, "failed", ["child budget exhausted"])],
)
async def test_child_budget_boundary_at_and_beyond_max_child_runs(
    max_child_runs, status, limitations
):
    class RepairBothOnceRuntime(FakeRuntime):
        async def run_verifier(self, snapshot, results):
            self.verifications += 1
            return VerificationReport(
                items=[
                    VerificationItem(
                        task_id=item.task_id,
                        attempt=item.attempt,
                        verdict="NEEDS_REPAIR" if self.verifications == 1 else "PASS",
                        repair_request="more" if self.verifications == 1 else None,
                    )
                    for item in results
                ]
            )

    result = await RootOrchestrator(
        RepairBothOnceRuntime(),
        acquire_context=_acquire,
        decompose=_decompose,
        plan_repairs=_repairs,
        aggregate=_aggregate,
    ).execute(
        # Round 1 books 2 workers + 1 verifier; the repair round needs 2 more
        # workers plus its verifier, so 6 is the exact budget and 5 is one short.
        _snapshot(max_child_runs=max_child_runs, token_budget=100_000),
        {"goal": "compare"},
    )

    assert result.status == status
    assert result.limitations == limitations


@pytest.mark.asyncio
@pytest.mark.parametrize(
    "field, value, message",
    [
        ("task_id", "other", "child result task ID does not match"),
        ("attempt", 2, "child result attempt does not match"),
        ("worker_snapshot_hash", _hash("forged"), "snapshot hash does not match its pin"),
        ("worker_agent_id", "impostor", "worker pin does not match its assignment"),
        ("worker_agent_revision", 99, "worker pin does not match its assignment"),
        ("worker_workflow_revision", 99, "worker pin does not match its assignment"),
    ],
)
async def test_child_result_that_does_not_match_its_pin_is_rejected(
    field, value, message
):
    runtime = FakeRuntime()
    original = runtime.run_worker

    async def forged(snapshot, worker, task):
        result = await original(snapshot, worker, task)
        return result.model_copy(update={field: value})

    async def one_task(snapshot, context):
        return [TaskAssignment(task_id="a", attempt=1, objective="research")]

    runtime.run_worker = forged
    with pytest.raises(ValueError, match=message):
        await RootOrchestrator(
            runtime,
            acquire_context=_acquire,
            decompose=one_task,
            plan_repairs=_repairs,
            aggregate=_aggregate,
        ).execute(_snapshot(), {})

    assert runtime.cancelled


@pytest.mark.asyncio
@pytest.mark.parametrize(
    "join_policy, statuses, status, limitations",
    [
        ("fail-fast", ("completed", "failed"), "failed", ["worker child failed"]),
        ("allow-partial", ("completed", "failed"), "completed", []),
        ("allow-partial", ("failed", "failed"), "failed", ["no worker result completed"]),
    ],
)
async def test_join_policy_matrix_fail_fast_allow_partial_and_no_completion(
    join_policy, statuses, status, limitations
):
    by_task = dict(zip(("a", "b"), statuses, strict=True))

    class PartialRuntime(FakeRuntime):
        async def run_worker(self, snapshot, worker, task):
            result = await super().run_worker(snapshot, worker, task)
            return result.model_copy(update={"status": by_task[task.task_id]})

        async def run_verifier(self, snapshot, results):
            self.verifications += 1
            return VerificationReport(
                items=[
                    VerificationItem(
                        task_id=item.task_id, attempt=item.attempt, verdict="PASS"
                    )
                    for item in results
                ]
            )

    runtime = PartialRuntime()
    result = await RootOrchestrator(
        runtime,
        acquire_context=_acquire,
        decompose=_decompose,
        plan_repairs=_repairs,
        aggregate=_aggregate,
    ).execute(_snapshot(join_policy=join_policy), {"goal": "compare"})

    assert result.status == status
    assert result.limitations == limitations
    assert runtime.cancelled == (join_policy == "fail-fast")
    if status == "completed":
        assert result.aggregate == {"tasks": ["a"]}


@pytest.mark.asyncio
async def test_repair_join_policy_keeps_going_after_a_failed_worker_child():
    """`repair` neither fail-fasts nor reports the failed child as a limitation."""

    class HalfFailingRuntime(FakeRuntime):
        async def run_worker(self, snapshot, worker, task):
            result = await super().run_worker(snapshot, worker, task)
            if task.task_id == "b":
                return result.model_copy(update={"status": "failed"})
            return result

    runtime = HalfFailingRuntime()
    result = await RootOrchestrator(
        runtime,
        acquire_context=_acquire,
        decompose=_decompose,
        plan_repairs=_repairs,
        aggregate=_aggregate,
        # _snapshot() defaults to join_policy="repair".
    ).execute(_snapshot(), {"goal": "compare"})

    assert result.status == "completed"
    assert not runtime.cancelled
    # Only the completed child reaches the verifier, so its NEEDS_REPAIR verdict
    # still buys a repair round; the failed child is silently dropped.
    assert runtime.verifications == 2
    assert result.aggregate == {"tasks": ["a", "a-repair-1"]}
    assert result.limitations == []


@pytest.mark.asyncio
async def test_root_timeout_cancels_children_and_reports_timed_out():
    runtime = FakeRuntime()

    async def slow(snapshot, worker, task):
        await asyncio.sleep(10)
        raise AssertionError("the root deadline must cancel this child")

    runtime.run_worker = slow
    result = await RootOrchestrator(
        runtime,
        acquire_context=_acquire,
        decompose=_decompose,
        plan_repairs=_repairs,
        aggregate=_aggregate,
        # The caller's remaining deadline is shorter than the snapshot timeout.
    ).execute(_snapshot(timeout_seconds=1), {}, remaining_deadline_seconds=0.05)

    assert result.status == "timed_out"
    assert result.accepted_results == []
    assert runtime.cancelled
    assert result.audit[-1]["event_type"] == "root_timed_out"


@pytest.mark.asyncio
async def test_token_budget_exactly_covering_all_reservations_succeeds():
    async def run(token_budget):
        return await RootOrchestrator(
            FakeRuntime(),
            acquire_context=_acquire,
            decompose=_decompose,
            plan_repairs=_repairs,
            aggregate=_aggregate,
        ).execute(_snapshot(token_budget=token_budget), {"goal": "compare"})

    measured = await run(1_000_000)
    assert measured.status == "completed"
    exact = max(
        event["token_units_used"]
        for event in measured.audit
        if "token_units_used" in event
    )

    assert (await run(exact)).status == "completed"
    short = await run(exact - 1)
    assert short.status == "failed"
    assert short.limitations[0].startswith("root token budget exhausted")


@pytest.mark.asyncio
async def test_task_without_eligible_pinned_worker_fails_closed():
    async def unmatched(snapshot, context):
        return [
            TaskAssignment(
                task_id="a",
                attempt=1,
                objective="research",
                required_capabilities=["unknown"],
            )
        ]

    with pytest.raises(ValueError, match="no pinned worker satisfies task a"):
        await RootOrchestrator(
            FakeRuntime(),
            acquire_context=_acquire,
            decompose=unmatched,
            plan_repairs=_repairs,
            aggregate=_aggregate,
        ).execute(_snapshot(), {})


def test_limits_reject_child_budget_without_a_verifier_slot():
    limits = dict(
        max_context_rounds=2,
        max_tasks=4,
        max_concurrency=2,
        max_repair_rounds=1,
        timeout_seconds=2,
    )
    assert OrchestratorLimits(max_child_runs=5, **limits).max_child_runs == 5
    with pytest.raises(ValueError, match="reserve one verifier"):
        OrchestratorLimits(max_child_runs=4, **limits)


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


@pytest.mark.asyncio
@pytest.mark.parametrize(
    "plan, message",
    [
        ([], "task plan is empty or exceeds max_tasks"),
        # _snapshot() fixes max_tasks at 4, so a fifth task is one past it.
        (
            [("a", 1), ("b", 1), ("c", 1), ("d", 1), ("e", 1)],
            "task plan is empty or exceeds max_tasks",
        ),
        ([("a", 1), ("a", 1)], "task IDs must be unique"),
    ],
)
async def test_malformed_task_plan_fails_closed_before_dispatch(plan, message):
    runtime = FakeRuntime()

    async def planned(snapshot, context):
        return [
            TaskAssignment(task_id=task_id, attempt=attempt, objective="research")
            for task_id, attempt in plan
        ]

    with pytest.raises(ValueError, match=message):
        await RootOrchestrator(
            runtime,
            acquire_context=_acquire,
            decompose=planned,
            plan_repairs=_repairs,
            aggregate=_aggregate,
        ).execute(_snapshot(), {})

    # Nothing was dispatched, so there is no durable child to cancel.
    assert not runtime.cancelled


def test_verifier_must_be_independent_of_every_worker():
    snapshot = _snapshot()
    raw = snapshot.model_dump()
    raw["verifier"] = snapshot.workers[0].model_dump()
    with pytest.raises(ValueError, match="verifier must be independent"):
        RootExecutionSnapshot.model_validate(raw)


def test_verifier_must_be_read_only():
    """An independent verifier still fails closed unless it is read-only."""
    snapshot = _snapshot()
    raw = snapshot.model_dump()
    raw["verifier"] = snapshot.verifier.model_dump() | {"read_only": False}
    with pytest.raises(ValueError, match="verifier must be read-only"):
        RootExecutionSnapshot.model_validate(raw)


def test_authority_hashes_and_worker_pin_uniqueness_fail_closed():
    """Rules/policies the caller swapped under a pinned authority hash are rejected."""
    snapshot = _snapshot()

    raw = snapshot.model_dump()
    raw["business_rules"] = {"version": 1, "rules": [{"x": 1}]}
    with pytest.raises(ValueError, match="business rules do not match"):
        RootExecutionSnapshot.model_validate(raw)

    raw = snapshot.model_dump()
    raw["policies"] = {"dispatchMode": "sequential"}
    with pytest.raises(ValueError, match="policies do not match"):
        RootExecutionSnapshot.model_validate(raw)

    raw = snapshot.model_dump()
    raw["workers"] = [snapshot.workers[0].model_dump()] * 2
    with pytest.raises(ValueError, match="worker pins must be unique"):
        RootExecutionSnapshot.model_validate(raw)


def test_graph_adapter_and_root_snapshot_hash_proofs_fail_closed():
    snapshot = _snapshot()
    raw = snapshot.model_dump()
    raw["graph"]["runtime_adapter_sha256"] = _hash("forged")
    with pytest.raises(ValueError, match="adapter hash"):
        RootExecutionSnapshot.model_validate(raw)

    raw = snapshot.model_dump()
    raw["graph"]["runtime_adapter_version"] = "root-runtime-adapter-2"
    with pytest.raises(ValueError, match="unsupported Root runtime adapter version"):
        RootExecutionSnapshot.model_validate(raw)

    raw = snapshot.model_dump()
    raw["graph"]["compiler_contract_version"] = "graph-ir/0"
    with pytest.raises(ValueError, match="unsupported Graph IR compiler contract"):
        RootExecutionSnapshot.model_validate(raw)

    raw = snapshot.model_dump()
    raw["graph"]["definition_sha256"] = _hash("other-graph")
    with pytest.raises(ValueError, match="published orchestrator Graph IR proof"):
        RootExecutionSnapshot.model_validate(raw)

    raw = snapshot.model_dump()
    raw["graph"]["definition"]["governance"]["maxSteps"] = 0
    with pytest.raises(ValueError, match="published orchestrator Graph IR proof"):
        RootExecutionSnapshot.model_validate(raw)

    # A published, compiler-valid worker graph still has no Root adapter.
    worker_graph = agent_runtime_graph()
    raw = snapshot.model_dump()
    raw["graph"]["definition"] = worker_graph
    raw["graph"]["definition_sha256"] = validate(worker_graph).definition_sha256
    with pytest.raises(ValueError, match="node without a runtime adapter"):
        RootExecutionSnapshot.model_validate(raw)

    raw = snapshot.model_dump()
    raw["token_budget"] += 1
    with pytest.raises(ValueError, match="snapshot hash"):
        RootExecutionSnapshot.model_validate(raw)


def test_root_snapshot_accepts_preserved_canonical_integer_tokens():
    """Backend emits its integer `budgets.timeoutSeconds` unchanged in the snapshot.

    `RootCommandClaim.snapshot()` decodes through `from_preserved_json`, so the
    preserved-number path is the one that must keep `90` from becoming `90.0`
    and invalidating the cross-service canonical snapshot hash.
    """
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


@pytest.mark.asyncio
async def test_terminally_insufficient_context_fails_instead_of_waiting_for_input():
    """`terminal=True` means the authority already closed the question."""
    runtime = FakeRuntime()
    decomposed = False

    async def terminally_insufficient(snapshot, context, context_round):
        return ContextAcquisition(
            ready=False,
            context=context,
            missing=["required document", "signed contract"],
            terminal=True,
        )

    async def should_not_run(snapshot, context):
        nonlocal decomposed
        decomposed = True
        return []

    result = await RootOrchestrator(
        runtime,
        acquire_context=terminally_insufficient,
        decompose=should_not_run,
        plan_repairs=_repairs,
        aggregate=_aggregate,
    ).execute(_snapshot(), {})

    assert result.status == "failed"
    assert result.limitations == [
        "context is terminally insufficient: required document, signed contract"
    ]
    # A terminal insufficiency never asks the caller for input it cannot use.
    assert result.clarification == []
    assert not decomposed


@pytest.mark.asyncio
@pytest.mark.parametrize(
    "context_round, status, limitations",
    [
        (0, "failed", ["context round budget exhausted"]),
        # _snapshot() fixes max_context_rounds at 2.
        (2, "completed", []),
        (3, "failed", ["context round budget exhausted"]),
    ],
)
async def test_context_round_boundary_at_and_beyond_max_context_rounds(
    context_round, status, limitations
):
    result = await RootOrchestrator(
        FakeRuntime(),
        acquire_context=_acquire,
        decompose=_decompose,
        plan_repairs=_repairs,
        aggregate=_aggregate,
    ).execute(_snapshot(), {"goal": "compare"}, context_round=context_round)

    assert result.status == status
    assert result.limitations == limitations
    # An out-of-budget round is rejected before any context is acquired.
    assert len(
        [item for item in result.audit if item["event_type"] == "context_assessed"]
    ) == (1 if status == "completed" else 0)


@pytest.mark.asyncio
@pytest.mark.parametrize(
    "overflow, status, limitations",
    [(0, "completed", []), (1, "failed", ["context byte budget exhausted"])],
)
async def test_context_byte_budget_accepts_the_limit_and_rejects_one_byte_over(
    overflow, status, limitations
):
    # The charge against the token ledger happens first, so the byte budget is
    # only reachable with a token budget that comfortably covers the payload.
    snapshot = _snapshot(token_budget=1_000_000)
    size = snapshot.context_byte_budget + overflow
    # {"blob":"…"} costs 11 bytes around the padding value.
    payload = {"blob": "x" * (size - 11)}
    assert _json_size(payload) == size

    async def sized(snap, context, context_round):
        return ContextAcquisition(ready=True, context=payload)

    result = await RootOrchestrator(
        FakeRuntime(),
        acquire_context=sized,
        decompose=_decompose,
        plan_repairs=_repairs,
        aggregate=_aggregate,
    ).execute(snapshot, {"goal": "compare"})

    assert result.status == status
    assert result.limitations == limitations


def _provenance(context_key: str) -> ContextProvenance:
    return ContextProvenance(
        context_key=context_key,
        source_type="caller",
        source_id="request",
        observed_at="2026-01-01T00:00:00Z",
        content_sha256=_hash(context_key),
    )


def test_task_envelope_rejects_conversation_and_delegation():
    with pytest.raises(ValueError, match="conversation"):
        TaskAssignment(
            task_id="a", attempt=1, objective="work", context={"messages": ["secret"]}
        )
    # delegation_depth is Literal[0]: Worker delegation is closed at the type level.
    with pytest.raises(ValueError, match="Input should be 0"):
        TaskAssignment(task_id="a", attempt=1, objective="work", delegation_depth=1)


def test_task_context_requires_exactly_one_provenance_per_field():
    with pytest.raises(ValueError, match="exactly one provenance"):
        TaskAssignment(
            task_id="a", attempt=1, objective="work", context={"document_id": "d-1"}
        )
    with pytest.raises(ValueError, match="exactly one provenance"):
        TaskAssignment(
            task_id="a",
            attempt=1,
            objective="work",
            context={"document_id": "d-1"},
            context_provenance=[_provenance("document_id"), _provenance("other")],
        )
    assert TaskAssignment(
        task_id="a",
        attempt=1,
        objective="work",
        context={"document_id": "d-1"},
        context_provenance=[_provenance("document_id")],
    ).delegation_depth == 0


def test_task_context_byte_limit_accepts_the_limit_and_rejects_one_byte_over():
    def envelope(size: int) -> TaskAssignment:
        # {"k":"…"} costs 8 bytes around the padding value.
        return TaskAssignment(
            task_id="a",
            attempt=1,
            objective="work",
            context={"k": "x" * (size - 8)},
            context_provenance=[_provenance("k")],
        )

    assert _json_size(envelope(MAX_TASK_CONTEXT_BYTES).context) == MAX_TASK_CONTEXT_BYTES
    with pytest.raises(ValueError, match="task context exceeds its byte limit"):
        envelope(MAX_TASK_CONTEXT_BYTES + 1)


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
    assert _json_size(result.model_dump(mode="json")) <= MAX_ROOT_RESULT_BYTES


def test_root_result_exceeding_the_backend_transition_limit_fails_closed():
    with pytest.raises(ValueError, match="Root result exceeds Backend transition"):
        RootRunResult(
            status="completed",
            accepted_results=[],
            aggregate={"answer": "x" * (MAX_ROOT_RESULT_BYTES + 1)},
            audit=[],
        )


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
    # {"value":"…"} costs 12 bytes around the padding, [ … ] two more.
    at_limit_output = {"value": "x" * (MAX_CHILD_OUTPUT_BYTES - 12)}
    at_limit_citations = [{"value": "x" * (MAX_CHILD_CITATIONS_BYTES - 14)}]
    assert _json_size(at_limit_output) == MAX_CHILD_OUTPUT_BYTES
    assert _json_size(at_limit_citations) == MAX_CHILD_CITATIONS_BYTES
    assert ChildResult(**common, output=at_limit_output).output == at_limit_output
    assert (
        ChildResult(**common, citations=at_limit_citations).citations
        == at_limit_citations
    )

    with pytest.raises(ValueError, match="child output"):
        ChildResult(**common, output={"value": "x" * (MAX_CHILD_OUTPUT_BYTES - 11)})
    with pytest.raises(ValueError, match="child citations"):
        ChildResult(
            **common, citations=[{"value": "x" * (MAX_CHILD_CITATIONS_BYTES - 13)}]
        )
