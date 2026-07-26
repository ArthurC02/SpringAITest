from __future__ import annotations

import pytest
import json
from langchain_core.runnables import RunnableLambda

from app.runtime.models import canonical_json_sha256
from app.runtime.orchestrator import (
    MAX_CHILD_OUTPUT_BYTES,
    RootExecutionSnapshot,
    RootInput,
    TaskAssignment,
)
from app.runtime.orchestrator_backend import ChildRecord, ChildStatus
from app.runtime.orchestrator_production import (
    ProductionChildRuntime,
    ProductionRootPlanner,
    _ContextSufficiency,
)
from app.security import RequestContext
from tests.test_root_orchestrator import _snapshot


class FakeBackend:
    def __init__(self):
        self.created = []
        self.reads = 0
        self.cancelled = False

    async def create_child(self, snapshot, task, worker, kind, ctx):
        self.created.append((task, worker, kind, ctx))
        return ChildRecord(
            id="aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
            orchestrator_root_run_id=snapshot.root_run_id,
            task_id=task.task_id,
            attempt=task.attempt,
            run_kind=kind,
            agent_id=worker.agent_id,
            agent_revision=worker.agent_revision,
            workflow_id=worker.workflow_id,
            workflow_revision=worker.workflow_revision,
            agent_snapshot_hash=worker.snapshot_hash,
            status="queued",
            agent_run_id="bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
            command_id="cccccccc-cccc-4ccc-8ccc-cccccccccccc",
        )

    async def get_child(self, root_run_id, child_id, ctx):
        self.reads += 1
        child = self.created[-1]
        task, worker, kind, _ = child
        return ChildStatus(
            id=child_id,
            orchestrator_root_run_id=root_run_id,
            task_id=task.task_id,
            attempt=task.attempt,
            run_kind=kind,
            agent_id=worker.agent_id,
            agent_revision=worker.agent_revision,
            workflow_id=worker.workflow_id,
            workflow_revision=worker.workflow_revision,
            agent_snapshot_hash=worker.snapshot_hash,
            status="completed",
            agent_run_id="bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
            command_id="cccccccc-cccc-4ccc-8ccc-cccccccccccc",
            output={"answer": "real durable result"},
            citations=[{"source": "doc"}],
        )

    async def cancel_root(self, root_run_id, ctx, reason):
        self.cancelled = True


class FakeManager:
    def __init__(self):
        self.commands = []

    async def dispatch_command(self, run_id, command_id, ctx):
        self.commands.append((run_id, command_id, ctx))


@pytest.mark.asyncio
async def test_worker_uses_durable_child_command_and_backend_terminal_result():
    backend = FakeBackend()
    manager = FakeManager()
    ctx = RequestContext(tenant_id="tenant", user_id="user", role="USER")
    runtime = ProductionChildRuntime(backend, manager, ctx)
    snapshot = _snapshot()
    task = TaskAssignment(task_id="task", attempt=1, objective="read")

    result = await runtime.run_worker(snapshot, snapshot.workers[0], task)

    assert manager.commands[0][:2] == (
        "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
        "cccccccc-cccc-4ccc-8ccc-cccccccccccc",
    )
    assert result.output == {"answer": "real durable result"}
    assert result.citations == [{"source": "doc"}]
    assert result.worker_snapshot_hash == snapshot.workers[0].snapshot_hash


def _snapshot_with_input() -> RootExecutionSnapshot:
    raw = _snapshot().model_dump(mode="python")
    raw["root_input"] = RootInput(
        message="Find the contract evidence",
        observed_at="2026-01-01T00:00:00Z",
    ).model_dump()
    raw["authority"]["context_tools"] = ["backend.retrieval_search"]
    raw.pop("snapshot_hash")
    return RootExecutionSnapshot(
        snapshot_hash=canonical_json_sha256(raw), **raw
    )


@pytest.mark.asyncio
async def test_context_acquisition_uses_only_scoped_server_retrieval(monkeypatch):
    calls = []

    async def search(query, top_k, tenant_id, knowledge_sources):
        calls.append((query, top_k, tenant_id, knowledge_sources))
        return [{"id": "chunk-1", "text": "evidence"}]

    monkeypatch.setattr(
        "app.runtime.orchestrator_production.search_chunks_scoped", search
    )
    backend = FakeBackend()
    ctx = RequestContext(tenant_id="tenant", user_id="user", role="USER")
    planner = ProductionRootPlanner(backend, ctx)
    snapshot = _snapshot_with_input()

    acquired = await planner.acquire(snapshot, {}, 1)

    assert acquired.ready
    assert calls == [
        (
            "Find the contract evidence",
            4,
            "tenant",
            ["docs"],
        )
    ]
    assert acquired.context["retrieval_chunks"][0]["id"] == "chunk-1"
    assert {
        (item.context_key, item.source_type, item.source_id)
        for item in acquired.provenance
    } == {
        ("goal", "caller", "root-input"),
        (
            "retrieval_chunks",
            "context-tool",
            "backend.retrieval_search",
        ),
    }


@pytest.mark.asyncio
async def test_verified_resume_input_is_consumed_and_satisfies_context_gate(monkeypatch):
    snapshot = _snapshot_with_input()
    raw = snapshot.model_dump(mode="python")
    raw["authority"]["context_tools"] = []
    raw.pop("snapshot_hash")
    without_retrieval = RootExecutionSnapshot(
        snapshot_hash=canonical_json_sha256(raw), **raw
    )
    planner = ProductionRootPlanner(
        FakeBackend(), RequestContext(tenant_id="tenant", user_id="user", role="USER")
    )
    from app.runtime.orchestrator_production import _ContextSufficiency
    async def sufficient(_snapshot, inputs):
        assert inputs == ["Region: Taiwan"]
        return _ContextSufficiency(ready=True, facts={"region": "Taiwan"}, evidence=[{"fact_key":"region","quote":"Region: Taiwan","input_index":0}])
    monkeypatch.setattr(planner, "_assess_context", sufficient)

    acquired = await planner.acquire(
        without_retrieval,
        {"user_input": "Region: Taiwan"},
        1,
    )

    assert acquired.ready
    assert acquired.missing == []
    assert acquired.context["user_context.region"] == "Taiwan"
    assert ("user_context.region", "caller", "resume-input:0") in {
        (item.context_key, item.source_type, item.source_id)
        for item in acquired.provenance
    }
    assert next(
        item.content_sha256
        for item in acquired.provenance
        if item.context_key == "user_context.region"
    ) == canonical_json_sha256("Region: Taiwan")


def _install_structured_model(monkeypatch, payload: dict) -> list:
    """Replace only the provider; the grounding checks stay real."""
    calls: list = []

    class Model:
        def bind(self, **_kwargs):
            return self

        def with_structured_output(self, _schema):
            def respond(value):
                calls.append(value)
                return payload

            return RunnableLambda(respond)

    monkeypatch.setattr(
        "app.runtime.orchestrator_production.get_direct_agent_runtime_llm",
        lambda: Model(),
    )
    return calls


def _snapshot_without_retrieval() -> RootExecutionSnapshot:
    raw = _snapshot_with_input().model_dump(mode="python")
    raw["authority"]["context_tools"] = []
    raw.pop("snapshot_hash")
    return RootExecutionSnapshot(snapshot_hash=canonical_json_sha256(raw), **raw)


@pytest.mark.asyncio
@pytest.mark.parametrize(
    "user_input, payload",
    [
        # Unstructured input: the model invents a structured quote absent from it.
        (
            "Taiwan region",
            {"ready": True, "facts": {"region": "Taiwan"}, "evidence": [{"fact_key": "region", "quote": "Region: Taiwan", "input_index": 0}], "missing": []},
        ),
        # Quoted verbatim, but the quote does not contain the claimed value.
        (
            "I do not know",
            {"ready": True, "facts": {"region": "Taiwan"}, "evidence": [{"fact_key": "region", "quote": "I do not know", "input_index": 0}], "missing": []},
        ),
        # Incomplete input: ready together with a still-missing field is contradictory.
        (
            "Region:",
            {"ready": True, "facts": {"region": "Taiwan"}, "evidence": [{"fact_key": "region", "quote": "Region:", "input_index": 0}], "missing": ["region"]},
        ),
    ],
)
async def test_unstructured_or_incomplete_resume_stays_fail_closed(
    monkeypatch, user_input, payload
):
    calls = _install_structured_model(monkeypatch, payload)
    planner = ProductionRootPlanner(
        FakeBackend(), RequestContext(tenant_id="tenant", user_id="user", role="USER")
    )

    acquired = await planner.acquire(
        _snapshot_without_retrieval(), {"user_input": user_input}, 1
    )

    assert calls, "the real grounding path must run against the model output"
    assert not acquired.ready
    assert acquired.missing == ["context sufficiency assessment unavailable"]
    assert acquired.context == {"goal": "Find the contract evidence"}
    assert [item.context_key for item in acquired.provenance] == ["goal"]


@pytest.mark.asyncio
async def test_context_acquisition_rejects_unknown_tool_without_calling_network(
    monkeypatch,
):
    snapshot = _snapshot_with_input()
    raw = snapshot.model_dump(mode="python")
    raw["authority"]["context_tools"].append("dangerous.write")
    raw.pop("snapshot_hash")
    forged = RootExecutionSnapshot(
        snapshot_hash=canonical_json_sha256(raw), **raw
    )
    planner = ProductionRootPlanner(
        FakeBackend(),
        RequestContext(tenant_id="tenant", user_id="user", role="USER"),
    )
    with pytest.raises(Exception, match="unsupported context tool"):
        await planner.acquire(forged, {}, 1)


@pytest.mark.parametrize(
    "payload, message",
    [
        ({"ready": True, "facts": {"region": "Taiwan"}, "evidence": [{"fact_key":"region","quote":"Region: Taiwan","input_index":0}], "missing": ["country"]}, "ready context requires facts, evidence, and no missing fields"),
        ({"ready": True, "facts": {}, "evidence": [{"fact_key":"region","quote":"Taiwan","input_index":0}], "missing": []}, "ready context requires facts, evidence, and no missing fields"),
        ({"ready": True, "facts": {"region": "Taiwan"}, "evidence": [], "missing": []}, "ready context requires facts, evidence, and no missing fields"),
        ({"ready": False, "facts": {"region": "Taiwan"}, "evidence": [], "missing": ["country"]}, "insufficient context must expose only missing fields"),
        ({"ready": False, "facts": {}, "evidence": [], "missing": []}, "insufficient context must expose only missing fields"),
        ({"ready": True, "facts": {"region": "Taiwan"}, "evidence": [{"fact_key":"region","quote":"Taiwan","input_index":0}, {"fact_key":"region","quote":"Taiwan","input_index":0}], "missing": []}, "exactly one grounded evidence item per fact"),
        ({"ready": True, "facts": {"region": "Taiwan"}, "evidence": [{"fact_key":"country","quote":"Taiwan","input_index":0}], "missing": []}, "exactly one grounded evidence item per fact"),
        ({"ready": False, "missing": [f"field-{index}" for index in range(21)]}, "at most 20 items"),
        ({"ready": True, "facts": {f"f{index}": "v" for index in range(33)}, "evidence": [{"fact_key": f"f{index}", "quote": "v", "input_index": 0} for index in range(33)], "missing": []}, "at most 32 items"),
        ({"ready": True, "facts": {"region": "Taiwan"}, "evidence": [{"fact_key":"region","quote":"Taiwan","input_index":20}], "missing": []}, "less than or equal to 19"),
    ],
)
def test_context_sufficiency_rejects_contradictory_authority(payload, message):
    with pytest.raises(ValueError, match=message):
        _ContextSufficiency(**payload)


@pytest.mark.parametrize(
    "payload",
    [
        {"ready": False, "missing": [f"field-{index}" for index in range(20)]},
        {
            "ready": True,
            "facts": {f"f{index}": "v" for index in range(32)},
            "evidence": [{"fact_key": f"f{index}", "quote": "v", "input_index": 0} for index in range(32)],
            "missing": [],
        },
        {"ready": True, "facts": {"region": "Taiwan"}, "evidence": [{"fact_key": "region", "quote": "Taiwan", "input_index": 19}], "missing": []},
    ],
)
def test_context_sufficiency_accepts_its_declared_bounds(payload):
    assert _ContextSufficiency(**payload).ready == payload["ready"]


@pytest.mark.asyncio
@pytest.mark.parametrize(
    "inputs, payload, expected_facts",
    [
        # Forged evidence: the quote appears nowhere in the trusted input.
        (
            ["I am in Taiwan"],
            {"ready": True, "facts": {"region": "Taiwan"}, "evidence": [{"fact_key": "region", "quote": "invented authority", "input_index": 0}], "missing": []},
            None,
        ),
        # Real quote, unrelated privilege claim: the value is not inside the quote.
        (
            ["Region: Taiwan"],
            {"ready": True, "facts": {"is_admin": "true"}, "evidence": [{"fact_key": "is_admin", "quote": "Region: Taiwan", "input_index": 0}], "missing": []},
            None,
        ),
        # Evidence index outside the trusted inputs cannot be grounded.
        (
            ["Region: Taiwan"],
            {"ready": True, "facts": {"region": "Taiwan"}, "evidence": [{"fact_key": "region", "quote": "Region: Taiwan", "input_index": 1}], "missing": []},
            None,
        ),
        # Naturally phrased but genuinely grounded: the gate must not over-reject.
        (
            ["I am in Taiwan"],
            {"ready": True, "facts": {"region": "Taiwan"}, "evidence": [{"fact_key": "region", "quote": "I am in Taiwan", "input_index": 0}], "missing": []},
            {"region": "Taiwan"},
        ),
    ],
)
async def test_context_sufficiency_grounds_every_fact_in_its_trusted_quote(
    monkeypatch, inputs, payload, expected_facts
):
    _install_structured_model(monkeypatch, payload)
    planner = ProductionRootPlanner(
        FakeBackend(), RequestContext(tenant_id="tenant", user_id="user", role="USER")
    )

    assessed = await planner._assess_context(_snapshot_with_input(), inputs)

    if expected_facts is None:
        assert assessed is None
    else:
        assert assessed is not None and assessed.facts == expected_facts


@pytest.mark.asyncio
async def test_verifier_parses_bounded_json_string_before_strict_validation():
    class VerifierBackend(FakeBackend):
        async def get_child(self, root_run_id, child_id, ctx):
            status = await super().get_child(root_run_id, child_id, ctx)
            task = self.created[-1][0]
            status.output = {
                "output": json.dumps(
                    {
                        "items": [
                            {
                                "task_id": "worker-task",
                                "attempt": 1,
                                "verdict": "PASS",
                                "evidence": [],
                                "repair_request": None,
                            }
                        ]
                    }
                )
            }
            return status

    backend = VerifierBackend()
    manager = FakeManager()
    runtime = ProductionChildRuntime(
        backend,
        manager,
        RequestContext(tenant_id="tenant", user_id="user", role="USER"),
    )
    snapshot = _snapshot()
    from app.runtime.orchestrator import ChildResult

    report = await runtime.run_verifier(snapshot, [_worker_result(snapshot)])
    assert report.items[0].verdict == "PASS"


def _worker_result(snapshot):
    from app.runtime.orchestrator import ChildResult

    return ChildResult(
        task_id="worker-task",
        attempt=1,
        child_run_id="child",
        worker_snapshot_hash=snapshot.workers[0].snapshot_hash,
        worker_agent_id=snapshot.workers[0].agent_id,
        worker_agent_revision=snapshot.workers[0].agent_revision,
        worker_workflow_revision=snapshot.workers[0].workflow_revision,
        status="completed",
    )


@pytest.mark.asyncio
@pytest.mark.parametrize(
    "verifier_output, message",
    [
        ({"output": "not json at all"}, "Verifier output is not strict JSON"),
        ({"output": {"items": "not a list"}}, "validation error"),
        # A verifier payload past the child wire bound never reaches the parser.
        ({"output": "x" * (MAX_CHILD_OUTPUT_BYTES + 1)}, "Verifier child did not complete"),
    ],
)
async def test_verifier_output_that_is_not_a_bounded_report_fails_closed(
    verifier_output, message
):
    class VerifierBackend(FakeBackend):
        async def get_child(self, root_run_id, child_id, ctx):
            status = await super().get_child(root_run_id, child_id, ctx)
            status.output = verifier_output
            return status

    runtime = ProductionChildRuntime(
        VerifierBackend(),
        FakeManager(),
        RequestContext(tenant_id="tenant", user_id="user", role="USER"),
    )
    snapshot = _snapshot()

    with pytest.raises(Exception, match=message):
        await runtime.run_verifier(snapshot, [_worker_result(snapshot)])
