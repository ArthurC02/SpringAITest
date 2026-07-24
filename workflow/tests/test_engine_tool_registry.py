"""Tool Registry 測試（AT3-15 ~ AT3-17）。

三件事：tool 呼叫入 trace 但 **args 的值不落**、uses_tools 白名單擋得住 script 的
動態呼叫（而且不得真的發出那次呼叫）、未註冊的 tool 存檔即拒。

初始 4 個 tool（規格 §6.2）只是既有 adapter 的 @tool 包裝 —— 因此測試沿用
tests/kbquery_fakes.py 的假依賴（FakeSearch…），不打網路、不新增 mocking 套件。
"""

import asyncio
from types import SimpleNamespace

import pytest

from app.engine import compiler, tool_registry
from app.engine.skill import UNKNOWN_TOOL, Skill, validate_definition
from app.engine.tool_registry import ToolContext, ToolNotAllowed, ToolTraceEntry

# import 觸發節點與 4 個初始 tool 的註冊
from app.nodes.kbquery import nodes as _kbquery_nodes  # noqa: F401
from app import tools as _tools  # noqa: F401
from app.nodes.kbquery.adapters import StaticGlossary
from tests.kbquery_fakes import TEXT_2025Q3, FakeSearch, RecordingAuditRepo

PROBE_TOOL = "local.probe"


@pytest.fixture(autouse=True, scope="module")
def _register_probe_tool():
    """throwaway tool：記錄自己被呼叫幾次（AT3-16 要斷言「沒有真的發出呼叫」）。"""
    calls: list[dict] = []

    @tool_registry.tool(
        name=PROBE_TOOL, kind="local", description="探針", args_schema={"x": int}
    )
    async def probe(ctx: ToolContext, **args) -> dict:
        calls.append(args)
        return {"called": True}

    yield calls
    tool_registry._REGISTRY.pop(PROBE_TOOL, None)


@pytest.fixture
def probe_calls(_register_probe_tool):
    _register_probe_tool.clear()
    return _register_probe_tool


def _deps(**extra):
    return SimpleNamespace(
        audit_repo=RecordingAuditRepo(),
        llm=None,
        glossary=StaticGlossary(),
        max_retrieval_attempts=2,
        **extra,
    )


def _run(definition: dict, state: dict | None = None, deps=None) -> dict:
    skill = Skill.model_validate({"name": "probe-skill", **definition})
    graph = compiler.compile(skill, deps or _deps())
    return compiler.public_output(
        asyncio.run(graph.ainvoke({"tenant_id": "t-test", **(state or {})}))
    )


def _tool_entries(result: dict) -> list[ToolTraceEntry]:
    return [e for e in result["trace"] if isinstance(e, ToolTraceEntry)]


def _codes(result) -> list[str]:
    return [e.code for e in result.errors]


# ---------------------------------------------------------------------------
# AT3-15 tool 呼叫入 trace（tool 名／耗時／狀態），args 不落值
# ---------------------------------------------------------------------------


def test_tool_call_enters_trace_without_arg_values():
    """【AT3-15】local.calculator(expression="1+1") → trace 有 tool/duration_ms/status，
    但 "1+1" 不得出現在 trace 的任何角落（只允許 args 鍵名摘要）。"""
    result = _run(
        {
            "flow": [
                {
                    "tool": "local.calculator",
                    "args": {"expression": "1+1"},
                    "save_as": "calc_result",
                }
            ]
        }
    )

    assert result["calc_result"] == 2.0

    entries = _tool_entries(result)
    assert len(entries) == 1
    entry = entries[0]
    assert entry.tool == "local.calculator"
    assert entry.status == "ok"
    assert isinstance(entry.duration_ms, float) and entry.duration_ms >= 0
    assert entry.args_keys == "expression"  # 只有鍵名

    dumped = "".join(e.model_dump_json() for e in result["trace"])
    assert "1+1" not in dumped  # args 的值一律不落


def test_tool_arg_values_absent_from_audit_trail():
    """稽核紀錄（含 node_trace）落地成 JSON 後也不得含 args 的值。"""
    deps = _deps()
    _run(
        {
            "flow": [
                {
                    "tool": "local.calculator",
                    "args": {"expression": "1+1"},
                    "save_as": "calc_result",
                }
            ]
        },
        deps=deps,
    )

    trail_json = deps.audit_repo.saved[0].model_dump_json()
    assert "1+1" not in trail_json
    assert "local.calculator" in trail_json  # tool 名要留下（稽核得知道呼叫過誰）


def test_failed_tool_call_is_traced_as_error():
    """tool 失敗一樣入 trace（status=error + 錯誤類別），並走 Harness 的 fatal 短路。"""
    result = _run(
        {
            "flow": [
                {
                    "tool": "local.calculator",
                    "args": {"expression": "1/0"},
                    "save_as": "calc_result",
                }
            ]
        }
    )

    entry = _tool_entries(result)[0]
    assert entry.status == "error"
    assert entry.error_code == "ZeroDivisionError"
    assert "fatal_error" in result
    assert result["trace"][-1].node_name == "audit_feedback"  # 稽核照樣落地


def test_tool_args_accept_state_references():
    """args 的 `$state.<key>` 引用在執行期解析成 state 的值（規格 §3.2）。"""
    result = _run(
        {
            "input_schema": {"formula": {"type": "str", "required": True}},
            "flow": [
                {
                    "tool": "local.calculator",
                    "args": {"expression": "$state.formula"},
                    "save_as": "calc_result",
                }
            ],
        },
        {"formula": "2 * 21"},
    )

    assert result["calc_result"] == 42.0


def test_tool_call_from_script_enters_trace():
    """script 內的 tools.call 一樣入 trace（一段 script 可以呼叫 N 次 → N 筆 entry）。"""
    result = _run(
        {
            "uses_tools": ["local.calculator"],
            "flow": [
                {
                    "script": (
                        "a = tools.call('local.calculator', expression='1+1')\n"
                        "b = tools.call('local.calculator', expression='2+2')\n"
                        "state['total'] = a + b\n"
                    )
                }
            ],
        }
    )

    assert result["total"] == 6.0
    entries = _tool_entries(result)
    assert [e.tool for e in entries] == ["local.calculator", "local.calculator"]
    assert all(e.status == "ok" and e.args_keys == "expression" for e in entries)
    assert "1+1" not in "".join(e.model_dump_json() for e in result["trace"])


# ---------------------------------------------------------------------------
# AT3-16 uses_tools 白名單
# ---------------------------------------------------------------------------


def test_script_calling_tool_outside_uses_tools_is_rejected_at_save_time(probe_calls):
    """【AT3-16】常數名的 tools.call 不在 uses_tools → 存檔即 unknown_tool。"""
    result = validate_definition(
        {
            "name": "probe-skill",
            "uses_tools": ["local.calculator"],
            "flow": [{"script": f"tools.call('{PROBE_TOOL}', x=1)"}],
        }
    )

    assert result.valid is False
    assert UNKNOWN_TOOL in _codes(result)
    assert probe_calls == []


def test_script_calling_tool_outside_uses_tools_is_rejected_at_run_time(probe_calls):
    """【AT3-16】tool 名是變數（靜態看不出來）→ 執行期擋，且**不得真的發出呼叫**。

    這一案是 uses_tools 存在的理由：靜態掃描看得見的呼叫在存檔就擋掉了，
    白名單真正要防的是這種執行期才決定名字的呼叫。
    """
    result = _run(
        {
            "uses_tools": ["local.calculator"],
            "flow": [
                {
                    "script": (
                        f"name = '{PROBE_TOOL}'\n"
                        "state['out'] = tools.call(name, x=1)\n"
                    )
                }
            ],
        }
    )

    assert probe_calls == []  # tool 函式一次都沒被執行到
    assert "out" not in result
    assert "ToolNotAllowed" in result["fatal_error"] or any(
        e["error_type"] == "ToolNotAllowed" for e in result["errors"]
    )
    assert result["trace"][-1].node_name == "audit_feedback"  # 稽核照樣落地


def test_tool_bag_rejects_before_scheduling_the_call(probe_calls):
    """ToolBag.call 直接測：不在白名單 → ToolNotAllowed，coroutine 根本不建立。"""
    bag = tool_registry.ToolBag(
        ctx=ToolContext(tenant_id="t1"),
        allowed={"local.calculator"},
        loop=asyncio.new_event_loop(),
        timeout_s=1.0,
    )

    with pytest.raises(ToolNotAllowed):
        bag.call(PROBE_TOOL, x=1)

    assert probe_calls == []


def test_tool_declared_in_flow_is_allowed_without_uses_tools(probe_calls):
    """決策表另一半：`tool:` 步驟宣告在 flow 裡就是白名單的一部分（flow 是靜態可稽核的）。"""
    result = _run({"flow": [{"tool": PROBE_TOOL, "args": {"x": 1}, "save_as": "out"}]})

    assert result["out"] == {"called": True}
    assert probe_calls == [{"x": 1}]


# ---------------------------------------------------------------------------
# AT3-17 unknown_tool
# ---------------------------------------------------------------------------


def test_unknown_tool_in_tool_step():
    """【AT3-17】tool 步驟引用未註冊的 tool → unknown_tool。"""
    result = validate_definition(
        {
            "name": "probe-skill",
            "flow": [{"tool": "no_such_tool", "args": {}, "save_as": "out"}],
        }
    )

    assert result.valid is False
    assert UNKNOWN_TOOL in _codes(result)


def test_unknown_tool_in_uses_tools():
    """uses_tools 列了未註冊的 tool 一樣是 unknown_tool（宣告即檢查）。"""
    result = validate_definition(
        {
            "name": "probe-skill",
            "uses_tools": ["no_such_tool"],
            "flow": [{"node": "query_intake"}],
        }
    )

    assert result.valid is False
    assert UNKNOWN_TOOL in _codes(result)


def test_known_tool_step_validates_clean():
    """決策表另一半：已註冊的 tool + 合法 save_as → 零錯誤。"""
    result = validate_definition(
        {
            "name": "probe-skill",
            "input_schema": {"formula": {"type": "str", "required": True}},
            "flow": [
                {
                    "tool": "local.calculator",
                    "args": {"expression": "$state.formula"},
                    "save_as": "calc_result",
                }
            ],
        }
    )

    assert result.errors == []
    assert result.valid is True


def test_compiler_rejects_unknown_tool_even_without_api_validation():
    """治理硬規則在引擎層：繞過 validate API 直接 compile() 也擋（對齊 AT-GOV-02 的形狀）。"""
    skill = Skill.model_validate(
        {
            "name": "probe-skill",
            "flow": [{"tool": "no_such_tool", "args": {}, "save_as": "out"}],
        }
    )

    with pytest.raises(compiler.SkillCompileError) as exc:
        compiler.compile(skill, _deps())

    assert "no_such_tool" in str(exc.value)


@pytest.mark.parametrize("save_as", ["query_id", "tenant_id", "trace", "__loop_0_count"])
def test_tool_step_cannot_save_into_reserved_key(save_as):
    """save_as 不得是保留鍵／引擎鍵／__ 前綴鍵（否則 tool 步驟就是一條覆寫保留鍵的路）。"""
    result = validate_definition(
        {
            "name": "probe-skill",
            "flow": [
                {"tool": "local.calculator", "args": {"expression": "1+1"}, "save_as": save_as}
            ],
        }
    )

    assert result.valid is False


# ---------------------------------------------------------------------------
# 註冊契約與初始 4 個 tool（規格 §6.1／§6.2）
# ---------------------------------------------------------------------------


def test_initial_four_tools_are_registered():
    """【規格 §6.2】初始 Tool 清單。"""
    names = {spec.name for spec in tool_registry.all_specs()}

    assert {
        "backend.retrieval_search",
        "local.calculator",
        "local.glossary",
        "local.rerank",
    } <= names


def test_tool_kinds():
    assert tool_registry.get("backend.retrieval_search").kind == "http"
    assert tool_registry.get("local.calculator").kind == "local"


def test_duplicate_tool_name_raises_value_error():
    """重複註冊同名 tool → 立即 ValueError（對齊 node_registry 的行為）。"""
    with pytest.raises(ValueError) as exc:
        tool_registry.tool(name="local.calculator", kind="local")(lambda ctx: None)

    assert "local.calculator" in str(exc.value)


def test_http_tool_passes_tenant_from_context_not_from_args():
    """http tool 的租戶邊界來自 ToolContext（伺服器注入），不是來自 args —— 跨租戶不可偽造。"""
    seen: list[str] = []

    class RecordingSearch(FakeSearch):
        async def search(self, query, *, filters, top_k, tenant_id):
            seen.append(tenant_id)
            return await super().search(
                query, filters=filters, top_k=top_k, tenant_id=tenant_id
            )

    deps = _deps(searchers={"vector": RecordingSearch(lambda q, f: [TEXT_2025Q3])})
    result = _run(
        {
            "input_schema": {"query": {"type": "str", "required": True}},
            "flow": [
                {
                    "tool": "backend.retrieval_search",
                    "args": {"query": "$state.query", "top_k": 3},
                    "save_as": "chunks",
                }
            ],
        },
        {"query": "2025Q3 稅後淨利"},
        deps=deps,
    )

    assert seen == ["t-test"]  # ctx.tenant_id，不是 args 帶進來的
    assert result["chunks"][0]["document_id"] == "doc-fin-2025q3"


def test_tool_step_receives_nonempty_identity_after_reads_hardening():
    """A2 守門：tool 步驟不套 reads 過濾 → ToolContext 拿到非空租戶、$state 引用正確解析。

    A2 把 node/agentic 步驟的 state 視圖收斂成宣告的 reads；tool 步驟刻意不過濾（身分由
    引擎自建的 run_tool 閉包從 state 讀）。若誤把 tool 也過濾，ctx.tenant_id 會是空字串 →
    多租戶隔離破功。此測直接捕獲 ctx 斷言非空。
    """
    captured: dict = {}

    @tool_registry.tool(
        name="local.identity_probe", kind="local", args_schema={"formula": str}
    )
    async def _probe(ctx: ToolContext, formula: str = "") -> dict:
        captured.update({"tenant": ctx.tenant_id, "formula": formula})
        return {"ok": True}

    try:
        _run(
            {
                "input_schema": {"formula": {"type": "str", "required": True}},
                "flow": [
                    {
                        "tool": "local.identity_probe",
                        "args": {"formula": "$state.formula"},
                        "save_as": "probe_out",
                    }
                ],
            },
            {"formula": "2 * 21"},
        )
    finally:
        tool_registry._REGISTRY.pop("local.identity_probe", None)

    assert captured["tenant"] == "t-test"  # 非空、來自 ToolContext
    assert captured["formula"] == "2 * 21"  # $state.formula 解析正確


def test_local_glossary_tool_wraps_existing_port():
    """local.glossary 只是既有 GlossaryPort 的包裝（ports.py 的介面不變）。"""
    out = asyncio.run(
        tool_registry.invoke(
            "local.glossary",
            ToolContext(tenant_id="t1", deps=_deps()),
            {"local.glossary"},
            {"text": "2025Q3 稅後淨利是多少"},
        )
    )

    assert out["canonical"] == "稅後淨利"
    assert "稅前淨利" in out["confusables"]


def test_local_rerank_tool_wraps_existing_port():
    """local.rerank 只是既有 RerankerPort 的包裝。"""
    from app.nodes.kbquery.adapters import ScoreReranker

    deps = _deps(reranker=ScoreReranker())
    out = asyncio.run(
        tool_registry.invoke(
            "local.rerank",
            ToolContext(tenant_id="t1", deps=deps),
            {"local.rerank"},
            {
                "query": "2025Q3 稅後淨利",
                "sources": [TEXT_2025Q3.model_dump()],
                "context": {"target_period": "2025Q3", "metric_terms": ["稅後淨利"]},
            },
        )
    )

    assert out[0]["rerank_score"] > out[0]["original_score"]  # 期間 + 指標命中加分
    assert out[0]["rerank_reason"]  # 留下排序依據供稽核
