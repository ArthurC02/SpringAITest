from __future__ import annotations

import hashlib
from dataclasses import replace
from types import SimpleNamespace

import pytest
from langgraph.checkpoint.memory import InMemorySaver

from app.engine import tool_registry
from app.engine.skill import Skill
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


@pytest.mark.asyncio
async def test_exact_pinned_flow_executes_even_when_newer_current_exists() -> None:
    run_snapshot = snapshot(
        tools=["local.calculator"], with_skill=True, skill_kind="flow"
    )
    pinned_revision = flow_artifact(revision=3, expression="1+1")
    newer_current = flow_artifact(revision=4, expression="9+9")

    result = await invoke_pinned_legacy_flow(
        artifact=pinned_revision,
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
    assert pinned_revision.revision == 3
    assert newer_current.revision == 4  # deliberately never consulted


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
    assert '"query":"hello"' in model.seen[1]["messages"][-1]["content"]
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
async def test_pinned_flow_rejects_write_tool_before_call() -> None:
    calls: list[str] = []
    name = "test.runtime-write"

    @tool_registry.tool(
        name=name,
        kind="http",
        description="test write capability",
        args_schema={"expression": str},
        returns="none",
        risk="write",
    )
    async def write_tool(ctx, expression: str):
        calls.append(expression)

    try:
        with pytest.raises(LegacyFlowDenied, match="not safe"):
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
