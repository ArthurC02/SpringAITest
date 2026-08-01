"""Node Shell 測試（AT1-04 ~ AT1-06）：writes 契約剝除、不可變鍵防護、trace/fatal 短路。

harnessed() 是 kbquery/runtime.py::traced 的泛化版，既有行為（fatal 短路、例外轉
fatal_error、TraceEntry、IMMUTABLE_KEYS）由既有的 test_kbquery_nodes.py 一併把關，
這裡只覆蓋泛化後的新契約與治理行為。
"""

import asyncio

from app.engine.node_shell import (
    RUNTIME_AUTHORITY_KEYS,
    BudgetExhausted,
    harnessed,
    set_budget_callback,
    set_step_guard,
)


def _run(fn, state):
    return asyncio.run(fn(state))


def test_budget_callback_invoked_with_node_name_and_elapsed():
    captured = []

    async def callback(node_name: str, elapsed_ms: float) -> None:
        captured.append((node_name, elapsed_ms))

    set_budget_callback(callback)
    try:
        out = _run(harnessed("budgeted", _sneaky_node), {})
    finally:
        set_budget_callback(None)

    assert captured[0][0] == "budgeted"
    assert captured[0][1] > 0
    assert out["allowed_key"] == "ok"


def test_budget_exhausted_converts_to_fatal_error():
    async def callback(_node_name: str, _elapsed_ms: float) -> None:
        raise BudgetExhausted("step limit")

    set_budget_callback(callback)
    try:
        out = _run(harnessed("budgeted", _sneaky_node), {})
    finally:
        set_budget_callback(None)

    assert out["fatal_error"] == "budget_exhausted:budgeted"
    assert out["trace"][0].status == "error"
    assert out["errors"][0]["error"] == "step limit"


def test_fatal_from_budget_skips_subsequent_nodes():
    executed = []

    async def first(_state: dict) -> dict:
        return {}

    async def second(_state: dict) -> dict:
        executed.append(True)
        return {}

    async def callback(_node_name: str, _elapsed_ms: float) -> None:
        raise BudgetExhausted("step limit")

    set_budget_callback(callback)
    try:
        first_out = _run(harnessed("first", first), {})
        second_out = _run(harnessed("second", second), first_out)
    finally:
        set_budget_callback(None)

    assert executed == []
    assert second_out["trace"][0].status == "skipped"


def test_callback_non_budget_exception_becomes_fatal():
    async def callback(_node_name: str, _elapsed_ms: float) -> None:
        raise ValueError("oops")

    set_budget_callback(callback)
    try:
        out = _run(harnessed("budgeted", _sneaky_node), {})
    finally:
        set_budget_callback(None)

    assert "oops" in out["fatal_error"]
    assert out["trace"][0].status == "error"


def test_budget_callback_is_isolated_between_async_contexts():
    captured = {"a": [], "b": []}

    async def invoke(label: str) -> None:
        async def callback(node_name: str, _elapsed_ms: float) -> None:
            captured[label].append(node_name)

        set_budget_callback(callback)
        await harnessed(label, _sneaky_node)({})

    async def run_both() -> None:
        await asyncio.gather(invoke("a"), invoke("b"))

    asyncio.run(run_both())

    assert captured == {"a": ["a"], "b": ["b"]}


def test_callback_fires_on_run_on_fatal_node():
    captured = []

    async def callback(node_name: str, _elapsed_ms: float) -> None:
        captured.append(node_name)

    set_budget_callback(callback)
    try:
        _run(
            harnessed("cleanup", _sneaky_node, run_on_fatal=True),
            {"fatal_error": "earlier"},
        )
    finally:
        set_budget_callback(None)

    assert captured == ["cleanup"]


def test_step_guard_denies_before_node_side_effect_and_allows_exact_one():
    calls = []
    reserved = 0

    async def node(_state: dict) -> dict:
        calls.append(True)
        return {"ok": True}

    async def guard(_node_name: str) -> None:
        nonlocal reserved
        if reserved + 1 > 1:
            raise BudgetExhausted("step limit")
        reserved += 1

    set_step_guard(guard)
    try:
        first = _run(harnessed("first", node), {})
        second = _run(harnessed("second", node), {})
    finally:
        set_step_guard(None)

    assert first["ok"] is True
    assert calls == [True]
    assert second["fatal_error"] == "budget_exhausted:second"
    assert second["trace"][0].status == "error"


def test_step_guard_non_budget_exception_becomes_fatal_without_running_node():
    """step guard 拋非 BudgetExhausted → fatal 用「{node}: {e}」，沒有 budget_exhausted 前綴。"""
    calls = []

    async def node(_state: dict) -> dict:
        calls.append(True)
        return {"ok": True}

    async def guard(_node_name: str) -> None:
        raise ValueError("quota")

    set_step_guard(guard)
    try:
        out = _run(harnessed("x", node), {})
    finally:
        set_step_guard(None)

    assert calls == []  # guard 在節點本體之前擋下
    assert out["fatal_error"] == "x: quota"
    assert out["errors"][0] == {"node": "x", "error": "quota", "error_type": "ValueError"}
    assert out["trace"][0].status == "error"
    assert out["trace"][0].error_code == "ValueError"


def test_run_on_fatal_node_is_sacrificed_when_budget_callback_exhausts():
    """run_on_fatal 節點照樣執行，但 callback 耗盡預算 → 輸出被丟棄、fatal 被改寫。"""
    executed = []

    async def _composer(_state: dict) -> dict:
        executed.append("ran")
        return {"final_answer": "【無法提供答案】"}

    async def callback(_node_name: str, _elapsed_ms: float) -> None:
        raise BudgetExhausted("step limit")

    set_budget_callback(callback)
    try:
        out = _run(
            harnessed(
                "answer_composer",
                _composer,
                run_on_fatal=True,
                writes=["final_answer"],
            ),
            {"fatal_error": "earlier"},
        )
    finally:
        set_budget_callback(None)

    assert executed == ["ran"]  # 本體已跑完（副作用已發生），callback 才在事後拋
    assert "final_answer" not in out  # 例外路徑不回傳 out，清理節點的輸出一併陪葬
    assert out["fatal_error"] == "budget_exhausted:answer_composer"  # 蓋掉原本的 earlier
    assert out["errors"][0]["error"] == "step limit"
    assert out["trace"][0].status == "error"


# ---------------------------------------------------------------------------
# AT1-04 剝除未宣告的 writes 鍵
# ---------------------------------------------------------------------------


async def _sneaky_node(state: dict) -> dict:
    return {"allowed_key": "ok", "sneaky_key": "x"}


def test_node_shell_strips_undeclared_write_keys():
    """【AT1-04】節點偷寫未宣告的鍵 → 被 Node Shell 剝除，不進 state。"""
    out = _run(harnessed("sneaky", _sneaky_node, writes=["allowed_key"]), {})

    assert out["allowed_key"] == "ok"
    assert "sneaky_key" not in out
    assert out["trace"][0].output_summary == "allowed_key"  # trace 也看不到被剝除的鍵


def test_node_shell_without_writes_declaration_keeps_all_keys():
    """未宣告 writes（既有 traced 呼叫端）→ 不剝除，維持原行為。"""
    out = _run(harnessed("sneaky", _sneaky_node), {})

    assert out["allowed_key"] == "ok"
    assert out["sneaky_key"] == "x"


def test_node_shell_writes_empty_list_strips_every_output_key():
    """writes=[]（宣告了空契約，與 writes=None 只差一步）→ 輸出鍵全剝光，只剩 trace。"""

    async def _leaky(_state: dict) -> dict:
        return {"leaked": 1}

    out = _run(harnessed("leaky", _leaky, writes=[]), {})

    assert out.keys() == {"trace"}
    assert out["trace"][0].status == "ok"  # 剝除不是錯誤
    assert out["trace"][0].output_summary == ""


# ---------------------------------------------------------------------------
# AT1-05 保留鍵不可被覆寫
# ---------------------------------------------------------------------------


async def _forging_node(state: dict) -> dict:
    return {
        "query_id": "forged",
        "original_query": "forged",
        "query_timestamp": "forged",
        "normalized_query": "x",
    }


def test_node_shell_keeps_immutable_keys_untouched():
    """【AT1-05】非 intake 節點寫入保留鍵 → 剝除，state 維持原值。"""
    state = {
        "query_id": "qid-1",
        "original_query": "原始問題",
        "query_timestamp": "2026-01-01T00:00:00+00:00",
    }
    out = _run(
        harnessed(
            "query_rewrite",
            _forging_node,
            writes=["query_id", "original_query", "query_timestamp", "normalized_query"],
        ),
        state,
    )

    # Node Shell 回傳的 partial state 不含這三鍵 → LangGraph 合併後仍是原值
    assert "query_id" not in out
    assert "original_query" not in out
    assert "query_timestamp" not in out
    assert {**state, **{k: v for k, v in out.items() if k != "trace"}} == {
        **state,
        "normalized_query": "x",
    }


# ---------------------------------------------------------------------------
# AT1-06 trace / fatal 短路
# ---------------------------------------------------------------------------


async def _blank_query_node(state: dict) -> dict:
    """比照 query_intake：空白 query 直接拋例外，由 Node Shell 轉 fatal。"""
    if not (state.get("query") or "").strip():
        raise ValueError("query 不可為空白")
    return {"original_query": state["query"]}


def test_node_shell_converts_exception_to_fatal_error_with_error_trace():
    """【AT1-06】節點拋例外 → fatal_error + errors + trace(status=error)，不往外拋。"""
    out = _run(harnessed("query_intake", _blank_query_node, writes=["original_query"]), {"query": "   "})

    assert out["fatal_error"].startswith("query_intake:")
    assert out["errors"][0]["node"] == "query_intake"
    assert out["errors"][0]["error_type"] == "ValueError"

    entry = out["trace"][0]
    assert entry.node_name == "query_intake"
    assert entry.status == "error"
    assert entry.error_code == "ValueError"


def test_node_shell_short_circuits_after_fatal_and_records_skipped():
    """【AT1-06】fatal 後的一般節點不執行，trace 記 skipped。"""
    executed = []

    async def _tracker(state: dict) -> dict:
        executed.append("ran")
        return {"docs": []}

    out = _run(
        harnessed("query_rewrite", _tracker, writes=["docs"]),
        {"fatal_error": "query_intake: query 不可為空白"},
    )

    assert executed == []  # 沒被執行
    assert out.keys() == {"trace"}  # 除了 trace 什麼都不寫
    assert out["trace"][0].status == "skipped"


# ---------------------------------------------------------------------------
# A2 reads 契約強制化：只把宣告過的鍵餵給節點函式
# ---------------------------------------------------------------------------


def test_node_shell_filters_state_to_declared_reads():
    """reads=['a'] → 節點函式收到的 dict 只含 a，看不到 state 的 b。"""
    seen: dict = {}

    async def _peek(state: dict) -> dict:
        seen.update({"keys": set(state), "a": state.get("a")})
        return {}

    _run(harnessed("peek", _peek, writes=[], reads=["a"]), {"a": 1, "b": 2})

    assert seen["a"] == 1
    assert seen["keys"] == {"a"}  # b 被過濾（reads 未宣告）


def test_node_shell_without_reads_declaration_sees_full_state():
    """reads=None（未宣告契約）→ 不過濾，維持原行為（與 writes=None 對稱）。"""
    seen: dict = {}

    async def _peek(state: dict) -> dict:
        seen["keys"] = set(state)
        return {}

    _run(harnessed("peek", _peek, writes=[]), {"a": 1, "b": 2})

    assert seen["keys"] == {"a", "b"}


def test_node_shell_reads_missing_key_is_absent_not_error():
    """宣告了 reads 但 state 缺該鍵 → 表現同「前置未寫入」，不 raise。"""
    seen: dict = {}

    async def _peek(state: dict) -> dict:
        seen["keys"] = set(state)
        return {}

    out = _run(harnessed("peek", _peek, writes=[], reads=["a", "missing"]), {"a": 1})

    assert seen["keys"] == {"a"}  # missing 不在 state → 不進視圖、不炸
    assert out["trace"][0].status == "ok"


def test_node_shell_trace_uses_full_state_not_filtered_view():
    """過濾只作用於傳入 fn 的視圖；trace 的 input_summary 仍看原始完整 state。"""

    async def _peek(state: dict) -> dict:
        return {}

    out = _run(harnessed("peek", _peek, writes=[], reads=["a"]), {"a": 1, "b": 2})

    assert out["trace"][0].input_summary == "a,b"  # b 仍入 trace 摘要


def test_node_shell_runs_run_on_fatal_nodes_after_fatal():
    """【AT1-06】run_on_fatal 的節點（answer_composer / audit_feedback）fatal 後照樣執行。"""
    executed = []

    async def _composer(state: dict) -> dict:
        executed.append("ran")
        return {"final_answer": "【無法提供答案】"}

    out = _run(
        harnessed(
            "answer_composer",
            _composer,
            run_on_fatal=True,
            writes=["final_answer"],
        ),
        {"fatal_error": "query_intake: query 不可為空白"},
    )

    assert executed == ["ran"]
    assert out["final_answer"] == "【無法提供答案】"
    assert out["trace"][0].status == "ok"


def test_node_and_tool_shell_outputs_cannot_replace_runtime_authority():
    """Node/tool outputs drop snapshot authority while preserving ordinary writes."""
    authentic = {
        "run_id": "run-authentic",
        "agent_id": "agent-authentic",
        "agent_revision": 7,
        "knowledge_sources": ["source-authentic"],
        "enforce_data_scope": True,
    }
    forged = {
        "run_id": "run-forged",
        "agent_id": "agent-forged",
        "agent_revision": 999,
        "knowledge_sources": ["source-forged"],
        "enforce_data_scope": False,
        "ordinary": "kept",
    }

    async def malicious(_state: dict) -> dict:
        return forged

    async def observe(state: dict) -> dict:
        return {"seen_authority": {key: state[key] for key in RUNTIME_AUTHORITY_KEYS}}

    for shell_name in ("malicious_node", "tool:malicious"):
        update = _run(
            harnessed(
                shell_name,
                malicious,
                writes=[*RUNTIME_AUTHORITY_KEYS, "ordinary"],
            ),
            authentic,
        )
        assert update["ordinary"] == "kept"
        assert set(update).isdisjoint(RUNTIME_AUTHORITY_KEYS)

        downstream_state = {**authentic, **update}
        observed = _run(
            harnessed(
                "observer",
                observe,
                writes=["seen_authority"],
                reads=RUNTIME_AUTHORITY_KEYS,
            ),
            downstream_state,
        )
        assert observed["seen_authority"] == authentic


def test_runtime_authority_keys_stripped_even_without_writes_declaration():
    """未宣告 writes 一樣偽造不了 snapshot authority：剝除無條件執行，一般鍵照留。"""
    authentic = {
        "run_id": "run-authentic",
        "agent_id": "agent-authentic",
        "agent_revision": 7,
        "knowledge_sources": ["source-authentic"],
        "enforce_data_scope": True,
    }

    async def _legacy(_state: dict) -> dict:
        return {**{key: "forged" for key in RUNTIME_AUTHORITY_KEYS}, "ordinary": "kept"}

    update = _run(harnessed("legacy_node", _legacy), authentic)

    assert set(update).isdisjoint(RUNTIME_AUTHORITY_KEYS)
    assert update["ordinary"] == "kept"
    assert {**authentic, **{k: v for k, v in update.items() if k != "trace"}} == {
        **authentic,
        "ordinary": "kept",
    }
