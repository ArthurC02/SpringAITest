from __future__ import annotations

import asyncio
import hashlib
import json
from dataclasses import replace
from types import SimpleNamespace

import pytest
from langgraph.checkpoint.memory import InMemorySaver

from app.engine import tool_registry
from app.engine.skill import InputField, Skill
from app.runtime import legacy_flow
from app.runtime.artifacts import LoadedSkillArtifact
from app.runtime.checkpoints import checkpoint_config, strict_serializer
from app.runtime.graph import build_context, compile_runtime_graph, initial_state
from app.runtime.legacy_flow import LegacyFlowDenied, invoke_pinned_legacy_flow
from app.runtime.models import RuntimeCommand
from tests.test_agent_runtime import FakeModel, request_context, snapshot


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


# legacy flow 的輸出過濾是這個模組最重要的安全邏輯：身分/授權鍵與稽核鍵都不得回流給模型。
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
async def test_pinned_flow_executes_and_returns_only_public_state() -> None:
    run_snapshot = snapshot(
        tools=["local.calculator"], with_skill=True, skill_kind="flow"
    )

    result = await invoke_pinned_legacy_flow(
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
        if item["event_type"] == "legacy_flow_completed"
    )
    assert event["payload"]["skill_revision"] == 3
    assert event["payload"]["definition_sha256"] == "a" * 64


@pytest.mark.asyncio
async def test_pinned_flow_rejects_scripts_before_execution() -> None:
    with pytest.raises(LegacyFlowDenied, match="external code"):
        await invoke_pinned_legacy_flow(
            artifact=flow_artifact(scripts_present=True),
            raw_input={},
            snapshot=snapshot(
                tools=["local.calculator"], with_skill=True, skill_kind="flow"
            ),
            rule_tools=None,
            deps=None,
            timeout_seconds=2,
            recursion_cap=20,
            remaining_tool_rounds=4,
        )


@pytest.mark.asyncio
@pytest.mark.parametrize("risk", ["read", "write"])
async def test_pinned_flow_rejects_any_tool_step_regardless_of_risk(risk: str) -> None:
    """`legacy_flow.py` 無條件拒絕所有 tool step——放寬成「只擋 write」必須是明示決定。"""
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
        with pytest.raises(LegacyFlowDenied, match="tool actions are not safe"):
            await invoke_pinned_legacy_flow(
                artifact=flow_artifact(tool=name),
                raw_input={},
                snapshot=snapshot(tools=[name], with_skill=True, skill_kind="flow"),
                rule_tools=None,
                deps=None,
                timeout_seconds=2,
                recursion_cap=20,
                remaining_tool_rounds=4,
            )
        assert calls == []
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
        legacy_flow.compiler, "compile", lambda skill, deps: graph
    )


@pytest.mark.asyncio
async def test_legacy_flow_output_excludes_authority_and_audit_keys(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """節點若把 authority/稽核鍵寫回 state,`public` 過濾必須全部剝掉,只留公開結果。"""
    hostile = {key: "leaked" for key in FORBIDDEN_OUTPUT_KEYS if key != "fatal_error"}
    hostile.update({"__internal": "leaked", "answer": "public result"})
    _stub_compile(monkeypatch, _StubGraph(hostile))

    result = await invoke_pinned_legacy_flow(
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
            "not a legacy flow",
        ),
        (
            replace(
                flow_artifact(),
                skill=Skill(
                    name="research-skill",
                    revision=3,
                    kind="flow",
                    flow=[{"node": "retrieve"}],
                ),
            ),
            {},
            "not deterministically safe",
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
    ids=["not-a-flow", "unsafe-node", "unbounded-loop", "input-schema"],
)
async def test_pinned_flow_denials_before_execution(
    artifact: LoadedSkillArtifact, raw_input: dict, match: str
) -> None:
    with pytest.raises(LegacyFlowDenied, match=match):
        await invoke_pinned_legacy_flow(
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
    ("graph", "timeout_seconds", "match"),
    [
        (_StubGraph({"fatal_error": {"code": "boom"}}), 2, "controlled failure"),
        (_StubGraph({"answer": "late"}, delay=1.0), 0.01, "bounded timeout"),
    ],
    ids=["fatal-error", "timeout"],
)
async def test_pinned_flow_denials_during_execution(
    monkeypatch: pytest.MonkeyPatch,
    graph: _StubGraph,
    timeout_seconds: float,
    match: str,
) -> None:
    _stub_compile(monkeypatch, graph)

    with pytest.raises(LegacyFlowDenied, match=match):
        await invoke_pinned_legacy_flow(
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
