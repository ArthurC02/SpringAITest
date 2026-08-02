from __future__ import annotations

import asyncio
import hashlib
import json
from dataclasses import replace
from types import SimpleNamespace

import pytest
from langgraph.checkpoint.memory import InMemorySaver

from app.engine import node_registry, tool_registry
from app.engine.skill import InputField, Skill
from app.runtime import graph as runtime_graph
from app.runtime import flow_harness
from app.runtime.artifacts import LoadedSkillArtifact
from app.runtime.checkpoints import checkpoint_config, strict_serializer
from app.runtime.graph import build_context, compile_runtime_graph, initial_state
from app.runtime.flow_harness import (
    FlowDenied,
    FlowResult,
    invoke_pinned_flow,
)
from app.runtime.models import RuntimeCommand
from tests.test_agent_runtime import (
    FakeArtifactReader,
    FakeModel,
    request_context,
    snapshot,
)


def flow_artifact(
    *,
    revision: int = 3,
    expression: str = "1+1",
    tool: str | None = None,
    scripts_present: bool = False,
) -> LoadedSkillArtifact:
    definition_hash = hashlib.sha256(
        f"revision={revision};expression={expression}".encode()
    ).hexdigest()
    return LoadedSkillArtifact(
        name="research-skill",
        revision=revision,
        kind="flow",
        definition_sha256=definition_hash,
        package_sha256=None,
        skill=Skill(
            name="research-skill",
            revision=revision,
            kind="flow",
            uses_tools=[tool] if tool else [],
            flow=(
                [
                    {
                        "tool": tool,
                        "args": {"expression": expression},
                        "save_as": "calculated",
                    }
                ]
                if tool
                else [{"node": "query_intake"}]
            ),
        ),
        instruction="",
        instruction_sha256=hashlib.sha256(b"").hexdigest(),
        resources={},
        scripts_present=scripts_present,
    )


# Flow runtime wrapper 的輸出過濾是這個模組最重要的安全邏輯：身分/授權鍵與稽核鍵都不得回流給模型。
FORBIDDEN_OUTPUT_KEYS = (
    "tenant_id",
    "user_id",
    "role",
    "run_id",
    "agent_id",
    "agent_revision",
    "knowledge_sources",
    "enforce_data_scope",
    "query_id",
    "original_query",
    "query_timestamp",
    "trace",
    "errors",
    "fatal_error",
    "audit_trail",
    "issue_label",
    "regression_test_item",
    "improvement_backlog",
)


@pytest.mark.asyncio
async def test_pinned_flow_public_output_regression() -> None:
    run_snapshot = snapshot(
        tools=["local.calculator"], with_skill=True, skill_kind="flow"
    )

    result = await invoke_pinned_flow(
        artifact=flow_artifact(revision=3, expression="1+1"),
        raw_input={"query": "hello"},
        snapshot=run_snapshot,
        rule_tools=None,
        deps=SimpleNamespace(max_retrieval_attempts=1),
        timeout_seconds=2,
        recursion_cap=20,
        remaining_tool_rounds=4,
    )

    assert result.status == "completed"
    assert '"query":"hello"' in result.content
    public = json.loads(result.content)
    assert set(public) & set(FORBIDDEN_OUTPUT_KEYS) == set()
    assert not any(key.startswith("__") for key in public)


@pytest.mark.asyncio
async def test_graph_loads_exact_flow_as_opaque_same_run_step() -> None:
    run_snapshot = snapshot(
        tools=["local.calculator"], with_skill=True, skill_kind="flow"
    )
    artifact = replace(flow_artifact(), definition_sha256="a" * 64)

    class Reader:
        async def read(self, pin, ctx):
            assert (pin.name, pin.revision) == ("research-skill", 3)
            return artifact

    model = FakeModel(
        [
            RuntimeCommand(
                kind="load_skill",
                name="research-skill",
                arguments={"query": "hello"},
            ),
            RuntimeCommand(kind="final", content="flow result accepted"),
        ]
    )
    graph = compile_runtime_graph(InMemorySaver(serde=strict_serializer()))
    context = build_context(
        snapshot=run_snapshot,
        request_context=request_context(),
        model=model,
        artifact_reader=Reader(),
        deps=SimpleNamespace(max_retrieval_attempts=1),
    )
    config = checkpoint_config(
        tenant_id=run_snapshot.caller.tenant_id,
        user_id=run_snapshot.caller.user_id,
        run_id=run_snapshot.run_id,
        snapshot_hash=run_snapshot.snapshot_hash,
    )
    result = await graph.ainvoke(
        initial_state(run_snapshot, "calculate"), config, context=context
    )
    assert result["status"] == "completed"
    assert result["active_skill_scope"] is None
    injected = model.seen[1]["messages"][-1]["content"]
    assert '"query":"hello"' in injected
    # 注回模型的觀察值同樣不得帶任何 authority/稽核鍵。
    assert not any(f'"{key}"' in injected for key in FORBIDDEN_OUTPUT_KEYS)
    event = next(
        item
        for item in result["events"]
        if item["event_type"] == "workflow_completed"
    )
    assert event["payload"]["skill_revision"] == 3
    assert event["payload"]["definition_sha256"] == "a" * 64


@pytest.mark.asyncio
async def test_load_skill_preserves_golden_wire_for_both_kinds_and_scope_switch(
    monkeypatch,
) -> None:
    """A3-1: real ``_load_skill`` freezes flow, first scope, and scope-switch wire."""

    flow_calls: list[dict] = []

    async def invoke_flow(**kwargs):
        flow_calls.append(kwargs)
        return FlowResult(
            status="completed",
            content='{"answer":"ok"}',
            tool_calls_bound=2,
            steps_bound=4,
        )

    monkeypatch.setattr(runtime_graph, "invoke_pinned_flow", invoke_flow)
    flow_snapshot = snapshot(with_skill=True, skill_kind="flow")
    flow_value = replace(flow_artifact(), definition_sha256="a" * 64)

    class FlowReader:
        def __init__(self):
            self.calls = 0

        async def read(self, pin, ctx):
            self.calls += 1
            assert (pin.name, pin.revision, pin.kind) == (
                "research-skill",
                3,
                "flow",
            )
            return flow_value

    flow_reader = FlowReader()
    flow_runtime = SimpleNamespace(
        context=build_context(
            snapshot=flow_snapshot,
            request_context=request_context(),
            model=FakeModel([]),
            artifact_reader=flow_reader,
            deps=SimpleNamespace(max_retrieval_attempts=1),
        )
    )
    flow_state = {
        "pending_command": RuntimeCommand(
            kind="load_skill", name="research-skill", arguments={"query": "q"}
        ).model_dump(mode="json"),
        "messages": [{"role": "user", "content": "q"}],
        "events": [],
        "step_count": 5,
        "tool_rounds": 1,
    }

    flow_update = await runtime_graph._load_skill(flow_state, flow_runtime)

    assert flow_reader.calls == 1
    assert flow_calls[0]["artifact"] is flow_value
    assert flow_calls[0]["raw_input"] == {"query": "q"}
    assert flow_update == {
        "pending_command": None,
        "rule_allowed_tools": None,
        "step_count": 8,
        "tool_rounds": 3,
        "messages": [
            {"role": "user", "content": "q"},
            {"role": "tool", "name": "load_skill", "content": '{"answer":"ok"}'},
        ],
        "events": [
            {
                "event_id": "09c71df3-b1e8-59ab-b4e1-683ca3cfa830",
                "event_type": "workflow_completed",
                "node_id": "load_skill",
                "snapshot_hash": "442f7964324bb9ef3bea9145c6c514acee6b8e8482a243b23b26a01d818ed1e7",
                "payload": {
                    "skill_name": "research-skill",
                    "skill_revision": 3,
                    "definition_sha256": "a" * 64,
                    "status": "completed",
                    "tool_calls_bound": 2,
                    "steps_bound": 4,
                },
            }
        ],
    }

    agent_snapshot = snapshot(with_skill=True)
    agent_reader = FakeArtifactReader([])
    agent_runtime = SimpleNamespace(
        context=build_context(
            snapshot=agent_snapshot,
            request_context=request_context(),
            model=FakeModel([]),
            artifact_reader=agent_reader,
        )
    )
    load_command = RuntimeCommand(
        kind="load_skill", name="research-skill"
    ).model_dump(mode="json")
    entered = await runtime_graph._load_skill(
        {
            "pending_command": load_command,
            "messages": [],
            "events": [],
            "step_count": 0,
        },
        agent_runtime,
    )
    expected_scope = {
        "name": "research-skill",
        "revision": 3,
        "kind": "agentic",
        "definition_sha256": "a" * 64,
        "package_sha256": "b" * 64,
        "instruction_sha256": "a6d633f1f9a8ae36e6a794619e32d94e0c56fc0dc375c2cf58ee72d1a2c708ff",
        "effective_tools": [],
        "resource_paths": ["references/guide.txt"],
    }
    entered_payload = {
        "skill_name": "research-skill",
        "skill_revision": 3,
        "definition_sha256": "a" * 64,
        "package_sha256": "b" * 64,
        "effective_tool_count": 0,
        "file_count": 1,
        "scripts_present": True,
    }
    assert agent_reader.calls == 1
    assert entered == {
        "active_skill_scope": expected_scope,
        "pending_command": None,
        "rule_allowed_tools": None,
        "messages": [
            {
                "role": "tool",
                "name": "load_skill",
                "content": (
                    "Pinned Skill research-skill@3 is active in this run. "
                    "0 read-only tools and 1 resources are available."
                ),
            }
        ],
        "events": [
            {
                "event_id": "28a11639-46d8-502f-824d-721278c3aee1",
                "event_type": "skill_scope_entered",
                "node_id": "load_skill",
                "snapshot_hash": "6d76c4cb4da4cc17c50db821d3a6d715fe65c80ac31665c8b978d19692741843",
                "payload": entered_payload,
            }
        ],
    }

    switch_reader = FakeArtifactReader([])
    switch_runtime = SimpleNamespace(
        context=build_context(
            snapshot=agent_snapshot,
            request_context=request_context(),
            model=FakeModel([]),
            artifact_reader=switch_reader,
        )
    )
    switched = await runtime_graph._load_skill(
        {
            "pending_command": load_command,
            "active_skill_scope": {"name": "previous-skill", "revision": 2},
            "messages": [],
            "events": [],
            "step_count": 0,
        },
        switch_runtime,
    )
    assert switch_reader.calls == 1
    assert switched == {
        "active_skill_scope": expected_scope,
        "pending_command": None,
        "rule_allowed_tools": None,
        "messages": entered["messages"],
        "events": [
            {
                "event_id": "b31d2258-f038-574e-86ab-1c2812dc44c9",
                "event_type": "skill_scope_exited",
                "node_id": "load_skill",
                "snapshot_hash": "6d76c4cb4da4cc17c50db821d3a6d715fe65c80ac31665c8b978d19692741843",
                "payload": {
                    "skill_name": "previous-skill",
                    "skill_revision": 2,
                    "reason": "scope_switch",
                },
            },
            {
                "event_id": "d64a75a2-e0fb-5f18-aa18-0f09fd632332",
                "event_type": "skill_scope_entered",
                "node_id": "load_skill",
                "snapshot_hash": "6d76c4cb4da4cc17c50db821d3a6d715fe65c80ac31665c8b978d19692741843",
                "payload": entered_payload,
            },
        ],
    }


@pytest.mark.asyncio
async def test_pinned_flow_allows_pinned_script_packages() -> None:
    artifact = replace(
        flow_artifact(scripts_present=True),
        skill=Skill(
            name="research-skill",
            revision=3,
            kind="flow",
            flow=[{"script": "state['script_result'] = 'ok'"}],
        ),
    )
    result = await invoke_pinned_flow(
            artifact=artifact,
            raw_input={"query": "hello"},
            snapshot=snapshot(
                tools=["local.calculator"], with_skill=True, skill_kind="flow"
            ),
            rule_tools=None,
            deps=SimpleNamespace(max_retrieval_attempts=1),
            timeout_seconds=2,
            recursion_cap=20,
            remaining_tool_rounds=4,
        )
    assert result.status == "completed"
    assert json.loads(result.content)["script_result"] == "ok"


@pytest.mark.asyncio
@pytest.mark.parametrize("risk", ["read", "write"])
async def test_pinned_flow_governs_tool_step_by_effective_set(risk: str) -> None:
    calls: list[str] = []
    name = f"test.runtime-{risk}"

    @tool_registry.tool(
        name=name,
        kind="http",
        description="test tool capability",
        args_schema={"expression": str},
        returns="none",
        risk=risk,
    )
    async def probe_tool(ctx, expression: str):
        calls.append(expression)

    try:
        invocation = invoke_pinned_flow(
                artifact=flow_artifact(tool=name),
                raw_input={},
                snapshot=snapshot(tools=[name], with_skill=True, skill_kind="flow"),
                rule_tools=None,
                deps=None,
                timeout_seconds=2,
                recursion_cap=20,
                remaining_tool_rounds=4,
            )
        if risk == "write":
            with pytest.raises(FlowDenied, match="not in effective set"):
                await invocation
            assert calls == []
        else:
            result = await invocation
            assert result.status == "completed"
            assert calls == ["1+1"]
    finally:
        tool_registry._REGISTRY.pop(name, None)


class _StubGraph:
    def __init__(self, output: dict, delay: float = 0.0):
        self.output = output
        self.delay = delay

    async def ainvoke(self, state, config=None):
        if self.delay:
            await asyncio.sleep(self.delay)
        return self.output


def _stub_compile(monkeypatch: pytest.MonkeyPatch, graph: _StubGraph) -> None:
    monkeypatch.setattr(
        flow_harness.compiler, "compile", lambda skill, deps: graph
    )


@pytest.mark.asyncio
async def test_flow_output_excludes_authority_and_audit_keys(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """節點若把 authority/稽核鍵寫回 state,`public` 過濾必須全部剝掉,只留公開結果。"""
    hostile = {key: "leaked" for key in FORBIDDEN_OUTPUT_KEYS if key != "fatal_error"}
    hostile.update({"__internal": "leaked", "answer": "public result"})
    _stub_compile(monkeypatch, _StubGraph(hostile))

    result = await invoke_pinned_flow(
        artifact=flow_artifact(),
        raw_input={"query": "hello"},
        snapshot=snapshot(
            tools=["local.calculator"], with_skill=True, skill_kind="flow"
        ),
        rule_tools=None,
        deps=SimpleNamespace(max_retrieval_attempts=1),
        timeout_seconds=2,
        recursion_cap=20,
        remaining_tool_rounds=4,
    )

    assert json.loads(result.content) == {"answer": "public result"}
    assert "leaked" not in result.content


@pytest.mark.asyncio
@pytest.mark.parametrize(
    ("artifact", "raw_input", "match"),
    [
        (
            replace(flow_artifact(), kind="agentic"),
            {},
            "not a flow skill",
        ),
        (
            replace(
                flow_artifact(),
                skill=Skill(
                    name="research-skill",
                    revision=3,
                    kind="flow",
                    flow=[{"loop": {"body": [{"node": "query_intake"}]}}],
                ),
            ),
            {},
            "loop is not bounded",
        ),
        (
            replace(
                flow_artifact(),
                skill=Skill(
                    name="research-skill",
                    revision=3,
                    kind="flow",
                    input_schema={"query": InputField(type="str", required=True)},
                    flow=[{"node": "query_intake"}],
                ),
            ),
            {},
            "failed its pinned schema",
        ),
    ],
    ids=["not-a-flow", "unbounded-loop", "input-schema"],
)
async def test_pinned_flow_denials_before_execution(
    artifact: LoadedSkillArtifact, raw_input: dict, match: str
) -> None:
    with pytest.raises(FlowDenied, match=match):
        await invoke_pinned_flow(
            artifact=artifact,
            raw_input=raw_input,
            snapshot=snapshot(
                tools=["local.calculator"], with_skill=True, skill_kind="flow"
            ),
            rule_tools=None,
            deps=SimpleNamespace(max_retrieval_attempts=1),
            timeout_seconds=2,
            recursion_cap=20,
            remaining_tool_rounds=4,
        )


@pytest.mark.asyncio
@pytest.mark.parametrize(
    ("graph", "timeout_seconds"),
    [
        (_StubGraph({"answer": "late"}, delay=1.0), 0.01),
    ],
    ids=["timeout"],
)
async def test_flow_timeout_is_denied_safely(
    monkeypatch: pytest.MonkeyPatch,
    graph: _StubGraph,
    timeout_seconds: float,
) -> None:
    _stub_compile(monkeypatch, graph)

    result = await invoke_pinned_flow(
        artifact=flow_artifact(),
        raw_input={"query": "hello"},
        snapshot=snapshot(
            tools=["local.calculator"], with_skill=True, skill_kind="flow"
        ),
        rule_tools=None,
        deps=SimpleNamespace(max_retrieval_attempts=1),
        timeout_seconds=timeout_seconds,
        recursion_cap=20,
        remaining_tool_rounds=4,
    )

    assert result.status == "timeout"


@pytest.mark.asyncio
async def test_pinned_flow_reports_actual_step_consumption() -> None:
    result = await invoke_pinned_flow(
        artifact=flow_artifact(),
        raw_input={"query": "hello"},
        snapshot=snapshot(with_skill=True, skill_kind="flow"),
        rule_tools=None,
        deps=SimpleNamespace(max_retrieval_attempts=1),
        timeout_seconds=2,
        recursion_cap=20,
        remaining_tool_rounds=4,
        remaining_steps=10,
    )

    assert result.steps_consumed == 2  # authored node + compiler-owned audit
    assert result.tool_rounds_consumed == 0


@pytest.mark.asyncio
async def test_pinned_flow_executes_any_registered_node() -> None:
    name = "test_phase2_registered_node"

    @node_registry.node(name=name, writes=["custom_result"])
    def make_node():
        async def run(state):
            return {"custom_result": "ok"}

        return run

    artifact = replace(
        flow_artifact(),
        skill=Skill(name="research-skill", revision=3, kind="flow", flow=[{"node": name}]),
    )
    try:
        result = await invoke_pinned_flow(
            artifact=artifact,
            raw_input={},
            snapshot=snapshot(with_skill=True, skill_kind="flow"),
            rule_tools=None,
            deps=None,
            timeout_seconds=2,
            recursion_cap=20,
            remaining_tool_rounds=4,
        )
        assert result.status == "completed"
        assert json.loads(result.content)["custom_result"] == "ok"
    finally:
        node_registry._REGISTRY.pop((name, "1.0"), None)


@pytest.mark.asyncio
async def test_pinned_flow_budget_exhaustion_is_controlled() -> None:
    result = await invoke_pinned_flow(
        artifact=flow_artifact(),
        raw_input={"query": "hello"},
        snapshot=snapshot(with_skill=True, skill_kind="flow"),
        rule_tools=None,
        deps=SimpleNamespace(max_retrieval_attempts=1),
        timeout_seconds=2,
        recursion_cap=20,
        remaining_tool_rounds=4,
        remaining_steps=1,
    )

    assert result.status == "budget_exhausted"
    assert result.steps_consumed == 0  # conservative preflight prevents side effects


@pytest.mark.asyncio
async def test_pinned_script_tool_must_be_in_effective_set() -> None:
    artifact = replace(
        flow_artifact(),
        skill=Skill(
            name="research-skill", revision=3, kind="flow",
            uses_tools=["blocked.tool"],
            flow=[{"script": "state['x'] = tools.call('blocked.tool')"}],
        ),
    )
    with pytest.raises(FlowDenied, match="blocked.tool not in effective set"):
        await invoke_pinned_flow(
            artifact=artifact, raw_input={},
            snapshot=snapshot(with_skill=True, skill_kind="flow"),
            rule_tools=None, deps=None, timeout_seconds=2,
            recursion_cap=20, remaining_tool_rounds=4,
        )


@pytest.mark.asyncio
async def test_pinned_registered_node_tools_must_be_in_effective_set() -> None:
    name = "test_phase4_governed_node"

    @node_registry.node(name=name, writes=["x"], requires_tools=["blocked.tool"])
    def make_node():
        async def run(state):
            return {"x": True}
        return run

    artifact = replace(
        flow_artifact(),
        skill=Skill(name="research-skill", revision=3, kind="flow", flow=[{"node": name}]),
    )
    try:
        with pytest.raises(FlowDenied, match="blocked.tool not in effective set"):
            await invoke_pinned_flow(
                artifact=artifact, raw_input={},
                snapshot=snapshot(with_skill=True, skill_kind="flow"),
                rule_tools=None, deps=None, timeout_seconds=2,
                recursion_cap=20, remaining_tool_rounds=4,
            )
    finally:
        node_registry._REGISTRY.pop((name, "1.0"), None)


@pytest.mark.asyncio
async def test_pinned_flow_denies_unknown_node_before_execution() -> None:
    """節點名在註冊表解析不到時（resolve_node → None）必須在編譯／執行前就拒絕。"""
    artifact = replace(
        flow_artifact(),
        skill=Skill(
            name="research-skill",
            revision=3,
            kind="flow",
            flow=[{"node": "no-such-node"}],
        ),
    )
    with pytest.raises(FlowDenied, match="unknown node: no-such-node"):
        await invoke_pinned_flow(
            artifact=artifact, raw_input={},
            snapshot=snapshot(with_skill=True, skill_kind="flow"),
            rule_tools=None, deps=None, timeout_seconds=2,
            recursion_cap=20, remaining_tool_rounds=4,
        )


@pytest.mark.asyncio
async def test_pinned_flow_executes_sequence_and_branch_steps() -> None:
    """sequence / branch 兩種步驟型別：治理遞迴進 then+else,執行則真的走 then。"""
    name = "test_phase2_sequence_branch_node"

    @node_registry.node(name=name, writes=["custom_result"])
    def make_node():
        async def run(state):
            return {"custom_result": "ok"}

        return run

    artifact = replace(
        flow_artifact(scripts_present=True),
        skill=Skill(
            name="research-skill",
            revision=3,
            kind="flow",
            flow=[
                {"sequence": [{"node": name}]},
                {
                    "branch": {
                        "when": "state.custom_result == 'ok'",
                        "then": [{"script": "state['branch_taken'] = 'then'"}],
                        "else": [{"script": "state['branch_taken'] = 'else'"}],
                    }
                },
            ],
        ),
    )
    try:
        result = await invoke_pinned_flow(
            artifact=artifact, raw_input={},
            snapshot=snapshot(with_skill=True, skill_kind="flow"),
            rule_tools=None, deps=None, timeout_seconds=2,
            recursion_cap=20, remaining_tool_rounds=4,
        )
        assert result.status == "completed"
        public = json.loads(result.content)
        assert public["custom_result"] == "ok"
        assert public["branch_taken"] == "then"
    finally:
        node_registry._REGISTRY.pop((name, "1.0"), None)


@pytest.mark.asyncio
@pytest.mark.parametrize("risk", ["read", "write"])
async def test_pinned_flow_rule_tools_narrow_but_never_widen(risk: str) -> None:
    """rule_tools 非 None 時只做交集：能再收窄,不能把 write 風險工具放回有效集合。"""
    calls: list[str] = []
    name = f"test.runtime-rule-{risk}"

    @tool_registry.tool(
        name=name,
        kind="http",
        description="test tool capability",
        args_schema={"expression": str},
        returns="none",
        risk=risk,
    )
    async def probe_tool(ctx, expression: str):
        calls.append(expression)

    def invocation(rule_tools: frozenset[str]):
        return invoke_pinned_flow(
            artifact=flow_artifact(tool=name),
            raw_input={},
            snapshot=snapshot(tools=[name], with_skill=True, skill_kind="flow"),
            rule_tools=rule_tools,
            deps=None,
            timeout_seconds=2,
            recursion_cap=20,
            remaining_tool_rounds=4,
        )

    try:
        # agent/caller/artifact 三方都授權,但 rule_tools 沒列到 → 收窄後仍拒。
        with pytest.raises(FlowDenied, match="not in effective set"):
            await invocation(frozenset({"other.tool"}))
        assert calls == []
        if risk == "write":
            with pytest.raises(FlowDenied, match="not in effective set"):
                await invocation(frozenset({name}))
            assert calls == []
        else:
            result = await invocation(frozenset({name}))
            assert result.status == "completed"
            assert calls == ["1+1"]
    finally:
        tool_registry._REGISTRY.pop(name, None)


@pytest.mark.asyncio
async def test_pinned_flow_admits_budgets_equal_to_its_bounds() -> None:
    """預算比較是嚴格大於：剛好等於上界要放行（少一格才拒,見 budget_exhaustion 測試）。"""
    result = await invoke_pinned_flow(
        artifact=flow_artifact(),
        raw_input={"query": "hello"},
        snapshot=snapshot(with_skill=True, skill_kind="flow"),
        rule_tools=None,
        deps=SimpleNamespace(max_retrieval_attempts=1),
        timeout_seconds=2,
        recursion_cap=20,
        remaining_tool_rounds=0,
        remaining_steps=2,
    )

    assert (result.steps_bound, result.tool_calls_bound) == (2, 0)
    assert result.status == "completed"
    assert result.steps_consumed == 2


@pytest.mark.asyncio
@pytest.mark.parametrize(
    ("fatal_error", "expected_status"),
    [
        ("query_intake: query 不可為空白", "error"),
        ("budget_exhausted:query_intake", "budget_exhausted"),
    ],
    ids=["fatal-error", "fatal-budget"],
)
async def test_flow_fatal_error_left_in_state_downgrades_status(
    monkeypatch: pytest.MonkeyPatch, fatal_error: str, expected_status: str
) -> None:
    """圖跑完但 state 留著 fatal_error：狀態降級,且 fatal_error 本身不得進 public。"""
    _stub_compile(
        monkeypatch,
        _StubGraph({"fatal_error": fatal_error, "answer": "partial"}),
    )

    result = await invoke_pinned_flow(
        artifact=flow_artifact(),
        raw_input={"query": "hello"},
        snapshot=snapshot(
            tools=["local.calculator"], with_skill=True, skill_kind="flow"
        ),
        rule_tools=None,
        deps=SimpleNamespace(max_retrieval_attempts=1),
        timeout_seconds=2,
        recursion_cap=20,
        remaining_tool_rounds=4,
    )

    assert result.status == expected_status
    assert json.loads(result.content) == {"answer": "partial"}


@pytest.mark.asyncio
async def test_flow_execution_exception_is_contained_as_error(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """圖執行拋出非 FlowDenied／非逾時的例外：收斂成 error,不外洩例外訊息或半成品。"""

    class _ExplodingGraph(_StubGraph):
        async def ainvoke(self, state, config=None):
            raise RuntimeError("node blew up for tenant-甲")

    _stub_compile(monkeypatch, _ExplodingGraph({"answer": "never"}))

    result = await invoke_pinned_flow(
        artifact=flow_artifact(),
        raw_input={"query": "hello"},
        snapshot=snapshot(
            tools=["local.calculator"], with_skill=True, skill_kind="flow"
        ),
        rule_tools=None,
        deps=SimpleNamespace(max_retrieval_attempts=1),
        timeout_seconds=2,
        recursion_cap=20,
        remaining_tool_rounds=4,
    )

    assert result.status == "error"
    assert result.content == "{}"
    assert (result.steps_consumed, result.tool_rounds_consumed) == (0, 0)
