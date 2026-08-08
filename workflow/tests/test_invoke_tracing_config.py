"""tracing callbacks 必須真的抵達 graph.ainvoke —— flow 與 agentic 兩條 invoke 路徑皆然。

回歸背景：main.py 組出 `{**tracing.runnable_config(), "recursion_limit": ...}` 之後，只有
agentic 分支把它交給 graph.ainvoke；flow 分支走 invoke_flow_with_governance，其內部
`_prepare_flow_state` 自建 config，callbacks 整段被丟掉 —— Langfuse 靜默收不到任何一筆
Business Workflow 的追蹤。test_infra_hardening 只驗 `runnable_config()` 本身回什麼，
驗不到「回來的東西有沒有被用上」，所以護欄放在這裡：釘住抵達圖的那份 config。
"""

from types import SimpleNamespace

import pytest
from fastapi.testclient import TestClient

from app import skills, tracing
from app.engine.skill import Skill
from app.main import app
from app.runtime import flow_harness
from app.runtime.flow_harness import invoke_pinned_flow
from app.settings import settings
from tests.conftest import auth_headers, swap_skill
from tests.test_agent_runtime import snapshot
from tests.test_agent_runtime_flow import flow_artifact

client = TestClient(app)

HEADERS = auth_headers()

_SENTINEL = object()


@pytest.fixture
def langfuse_handler(monkeypatch):
    """讓 tracing.runnable_config() 回一個可辨識的 handler（不碰真的 Langfuse）。"""
    monkeypatch.setattr(settings, "langfuse_enabled", True)
    monkeypatch.setattr(tracing, "_handler", lambda: _SENTINEL)
    return _SENTINEL


class _CaptureGraph:
    def __init__(self):
        self.configs: list[dict] = []

    async def ainvoke(self, state, config=None):
        self.configs.append(config or {})
        return {**state, "ran": True}


def _invoke(kind: str, graph: _CaptureGraph):
    name = f"__tracing-{kind}__"
    definition = {"name": name.strip("_"), "flow": [{"node": "t"}]}
    if kind == "agentic":
        definition["kind"] = "agentic"
    with swap_skill(
        name,
        base=skills.get("kb-query"),
        skill=Skill.model_validate(definition),
        graph=graph,
        input_model=None,
        deps=None,
    ):
        return client.post(
            f"/skills/{name}/invoke", json={"input": {"query": "x"}}, headers=HEADERS
        )


@pytest.mark.parametrize("kind", ["flow", "agentic"])
def test_invoke_passes_tracing_callbacks_to_the_graph(kind, langfuse_handler):
    """兩條路徑都必須把 callbacks 帶進圖，且不得覆蓋 recursion_limit 護欄。"""
    graph = _CaptureGraph()

    assert _invoke(kind, graph).status_code == 200

    assert len(graph.configs) == 1
    assert graph.configs[0]["callbacks"] == [langfuse_handler]
    assert isinstance(graph.configs[0]["recursion_limit"], int)


@pytest.mark.parametrize("kind", ["flow", "agentic"])
def test_invoke_without_tracing_keeps_only_the_recursion_guard(kind, monkeypatch):
    """決策表另一半：關閉 Langfuse 時不得憑空生出 callbacks 鍵。"""
    monkeypatch.setattr(settings, "langfuse_enabled", False)
    graph = _CaptureGraph()

    assert _invoke(kind, graph).status_code == 200

    assert "callbacks" not in graph.configs[0]
    assert isinstance(graph.configs[0]["recursion_limit"], int)


# 第三條路徑：D3 Harness 內嵌 Business Workflow（graph.py → invoke_pinned_flow）。
# 它沒有 HTTP 層可繼承 config，自己取 tracing，護欄同上：callbacks 要進圖、recursion 上界不動。
RECURSION_CAP = 20


async def _invoke_pinned(monkeypatch, graph: _CaptureGraph):
    monkeypatch.setattr(flow_harness.compiler, "compile", lambda skill, deps: graph)
    return await invoke_pinned_flow(
        artifact=flow_artifact(),
        raw_input={"query": "hello"},
        snapshot=snapshot(tools=["local.calculator"], with_skill=True, skill_kind="flow"),
        rule_tools=None,
        deps=SimpleNamespace(max_retrieval_attempts=1),
        timeout_seconds=2,
        recursion_cap=RECURSION_CAP,
        remaining_tool_rounds=4,
    )


@pytest.mark.asyncio
async def test_pinned_flow_passes_tracing_callbacks_to_the_graph(
    monkeypatch, langfuse_handler
):
    graph = _CaptureGraph()

    result = await _invoke_pinned(monkeypatch, graph)

    assert result.status == "completed"
    assert len(graph.configs) == 1
    assert graph.configs[0]["callbacks"] == [langfuse_handler]
    assert 0 < graph.configs[0]["recursion_limit"] <= RECURSION_CAP


@pytest.mark.asyncio
async def test_pinned_flow_without_tracing_keeps_only_the_recursion_guard(monkeypatch):
    monkeypatch.setattr(settings, "langfuse_enabled", False)
    graph = _CaptureGraph()

    result = await _invoke_pinned(monkeypatch, graph)

    assert result.status == "completed"
    assert "callbacks" not in graph.configs[0]
    assert 0 < graph.configs[0]["recursion_limit"] <= RECURSION_CAP
