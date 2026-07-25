from __future__ import annotations

import pytest
import json
from langchain_core.runnables import RunnableLambda

from app.runtime.models import canonical_json_sha256
from app.runtime.orchestrator import (
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


@pytest.mark.asyncio
@pytest.mark.parametrize(
    "user_input",
    ["Taiwan region", "I do not know", "Region:"],
)
async def test_unstructured_or_incomplete_resume_stays_fail_closed(monkeypatch, user_input):
    snapshot = _snapshot_with_input()
    raw = snapshot.model_dump(mode="python")
    raw["authority"]["context_tools"] = []
    raw.pop("snapshot_hash")
    planner = ProductionRootPlanner(
        FakeBackend(), RequestContext(tenant_id="tenant", user_id="user", role="USER")
    )
    from app.runtime.orchestrator_production import _ContextSufficiency
    async def insufficient(_snapshot, _inputs):
        return _ContextSufficiency(ready=False, missing=["region is required"])
    monkeypatch.setattr(planner, "_assess_context", insufficient)
    acquired = await planner.acquire(
        RootExecutionSnapshot(snapshot_hash=canonical_json_sha256(raw), **raw),
        {"user_input": user_input},
        1,
    )
    assert not acquired.ready
    assert acquired.missing == ["region is required"]


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
    "payload",
    [
        {"ready": True, "facts": {"region": "Taiwan"}, "evidence": [{"fact_key":"region","quote":"Region: Taiwan","input_index":0}], "missing": ["country"]},
        {"ready": True, "facts": {}, "evidence": [{"fact_key":"region","quote":"Taiwan","input_index":0}], "missing": []},
        {"ready": True, "facts": {"region": "Taiwan"}, "evidence": [], "missing": []},
        {"ready": False, "facts": {"region": "Taiwan"}, "evidence": [], "missing": ["country"]},
        {"ready": True, "facts": {"region": "Taiwan"}, "evidence": [{"fact_key":"region","quote":"Taiwan","input_index":0}, {"fact_key":"region","quote":"Taiwan","input_index":0}], "missing": []},
    ],
)
def test_context_sufficiency_rejects_contradictory_authority(payload):
    with pytest.raises(ValueError):
        _ContextSufficiency(**payload)


@pytest.mark.asyncio
async def test_context_sufficiency_rejects_forged_evidence(monkeypatch):
    snapshot = _snapshot_with_input()
    planner = ProductionRootPlanner(
        FakeBackend(), RequestContext(tenant_id="tenant", user_id="user", role="USER")
    )

    class Model:
        def bind(self, **_kwargs):
            return self
        def with_structured_output(self, _schema):
            return RunnableLambda(
                lambda _value: {
                    "ready": True,
                    "facts": {"region": "Taiwan"},
                    "evidence": [{"fact_key": "region", "quote": "invented authority", "input_index": 0}],
                    "missing": [],
                }
            )

    monkeypatch.setattr("app.runtime.orchestrator_production.get_direct_agent_runtime_llm", lambda: Model())
    assert await planner._assess_context(snapshot, ["I am in Taiwan"]) is None


@pytest.mark.asyncio
async def test_context_sufficiency_rejects_unrelated_privilege_claim(monkeypatch):
    snapshot = _snapshot_with_input()
    planner = ProductionRootPlanner(
        FakeBackend(), RequestContext(tenant_id="tenant", user_id="user", role="USER")
    )

    class Model:
        def bind(self, **_kwargs):
            return self

        def with_structured_output(self, _schema):
            return RunnableLambda(
                lambda _value: {
                    "ready": True,
                    "facts": {"is_admin": "true"},
                    "evidence": [
                        {"fact_key": "is_admin", "quote": "Region: Taiwan", "input_index": 0}
                    ],
                    "missing": [],
                }
            )

    monkeypatch.setattr("app.runtime.orchestrator_production.get_direct_agent_runtime_llm", lambda: Model())
    assert await planner._assess_context(snapshot, ["Region: Taiwan"]) is None


@pytest.mark.asyncio
async def test_context_sufficiency_accepts_natural_grounded_fact(monkeypatch):
    snapshot = _snapshot_with_input()
    planner = ProductionRootPlanner(
        FakeBackend(), RequestContext(tenant_id="tenant", user_id="user", role="USER")
    )

    class Model:
        def bind(self, **_kwargs):
            return self

        def with_structured_output(self, _schema):
            return RunnableLambda(
                lambda _value: {
                    "ready": True,
                    "facts": {"region": "Taiwan"},
                    "evidence": [
                        {"fact_key": "region", "quote": "I am in Taiwan", "input_index": 0}
                    ],
                    "missing": [],
                }
            )

    monkeypatch.setattr("app.runtime.orchestrator_production.get_direct_agent_runtime_llm", lambda: Model())
    assessed = await planner._assess_context(snapshot, ["I am in Taiwan"])
    assert assessed is not None
    assert assessed.facts == {"region": "Taiwan"}


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

    report = await runtime.run_verifier(
        snapshot,
        [
            ChildResult(
                task_id="worker-task",
                attempt=1,
                child_run_id="child",
                worker_snapshot_hash=snapshot.workers[0].snapshot_hash,
                worker_agent_id=snapshot.workers[0].agent_id,
                worker_agent_revision=snapshot.workers[0].agent_revision,
                worker_workflow_revision=snapshot.workers[0].workflow_revision,
                status="completed",
            )
        ],
    )
    assert report.items[0].verdict == "PASS"
