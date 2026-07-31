import asyncio

import pytest

from app.engine.skill import Skill
from app.engine import tool_registry
from app.engine.node_shell import BudgetExhausted, set_budget_callback
from app.engine.tool_registry import ToolContext
from app.runtime.flow_harness import invoke_flow_with_governance


class _Graph:
    def __init__(self, output=None, delay=0):
        self.output = output or {}
        self.delay = delay
        self.calls = 0

    async def ainvoke(self, state, config=None):
        self.calls += 1
        await asyncio.sleep(self.delay)
        return {**state, **self.output, "__private": True}


@pytest.mark.asyncio
async def test_flow_governance_produces_preflight_and_finalize():
    skill = Skill(name="probe", flow=[])
    result = await invoke_flow_with_governance(
        skill=skill,
        raw_input={"tenant_id": "t", "query": "q"},
        deps=None,
        timeout_seconds=1,
        step_budget=1,
        tool_round_budget=1,
        graph=_Graph({"answer": "ok"}),
    )

    assert result.status == "completed"
    assert result.output == {"query": "q", "answer": "ok"}
    assert result.governance["events"] == ["workflow_completed"]
    assert result.governance["finalize"]["status"] == "completed"


@pytest.mark.asyncio
async def test_flow_governance_timeout_finalizes():
    result = await invoke_flow_with_governance(
        skill=Skill(name="slow", flow=[]),
        raw_input={},
        deps=None,
        timeout_seconds=0.01,
        step_budget=1,
        tool_round_budget=1,
        graph=_Graph(delay=0.1),
    )

    assert result.status == "timeout"
    assert result.governance["finalize"]["status"] == "timeout"


@pytest.mark.asyncio
async def test_flow_budget_rejects_before_side_effect():
    result = await invoke_flow_with_governance(
        skill=Skill(name="bounded", flow=[{"script": "state['x'] = 1"}]),
        raw_input={}, deps=None, timeout_seconds=1, step_budget=1,
        tool_round_budget=1, graph=_Graph({"side_effect": True}),
    )
    assert result.status == "budget_exhausted"
    assert result.output == {}


@pytest.mark.asyncio
async def test_flow_preflight_rejects_authoritative_hash_mismatch():
    graph = _Graph()
    result = await invoke_flow_with_governance(
        skill=Skill(name="pinned", flow=[]), raw_input={}, deps=None,
        timeout_seconds=1, step_budget=1, tool_round_budget=1,
        graph=graph, definition="name: pinned", definition_sha256="0" * 64,
    )
    assert result.status == "error"
    assert result.governance["preflight"]["status"] == "error"
    assert graph.calls == 0


@pytest.mark.asyncio
async def test_actual_tool_charge_allows_exact_one_and_blocks_second_before_effect():
    name = "test.phase4-budget"
    effects = []
    used = 0

    @tool_registry.tool(name=name, kind="local", description="probe", risk="read", returns="probe")
    async def probe(ctx):
        effects.append(True)
        return "ok"

    async def charge(node_name: str, elapsed_ms: float) -> None:
        del elapsed_ms
        nonlocal used
        if node_name.startswith("__tool__:"):
            if used + 1 > 1:
                raise BudgetExhausted("tool limit")
            used += 1

    ctx = ToolContext(tenant_id="t", user_id="u", role="USER", deps=None)
    set_budget_callback(charge)
    try:
        assert await tool_registry.invoke(name, ctx, {name}, {}) == "ok"
        with pytest.raises(BudgetExhausted, match="tool limit"):
            await tool_registry.invoke(name, ctx, {name}, {})
    finally:
        set_budget_callback(None)
        tool_registry._REGISTRY.pop(name, None)
    assert effects == [True]
