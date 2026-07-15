"""Harness 測試（AT1-04 ~ AT1-06）：writes 契約剝除、不可變鍵防護、trace/fatal 短路。

harnessed() 是 kbquery/runtime.py::traced 的泛化版，既有行為（fatal 短路、例外轉
fatal_error、TraceEntry、IMMUTABLE_KEYS）由既有的 test_kbquery_nodes.py 一併把關，
這裡只覆蓋泛化後的新契約與治理行為。
"""

import asyncio

from app.engine.harness import harnessed


def _run(fn, state):
    return asyncio.run(fn(state))


# ---------------------------------------------------------------------------
# AT1-04 剝除未宣告的 writes 鍵
# ---------------------------------------------------------------------------


async def _sneaky_node(state: dict) -> dict:
    return {"allowed_key": "ok", "sneaky_key": "x"}


def test_harness_strips_undeclared_write_keys():
    """【AT1-04】節點偷寫未宣告的鍵 → 被 Harness 剝除，不進 state。"""
    out = _run(harnessed("sneaky", _sneaky_node, writes=["allowed_key"]), {})

    assert out["allowed_key"] == "ok"
    assert "sneaky_key" not in out
    assert out["trace"][0].output_summary == "allowed_key"  # trace 也看不到被剝除的鍵


def test_harness_without_writes_declaration_keeps_all_keys():
    """未宣告 writes（既有 traced 呼叫端）→ 不剝除，維持原行為。"""
    out = _run(harnessed("sneaky", _sneaky_node), {})

    assert out["allowed_key"] == "ok"
    assert out["sneaky_key"] == "x"


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


def test_harness_keeps_immutable_keys_untouched():
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

    # Harness 回傳的 partial state 完全不含這三鍵 → LangGraph 合併後仍是原值
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
    """比照 query_intake：空白 query 直接拋例外，由 Harness 轉 fatal。"""
    if not (state.get("query") or "").strip():
        raise ValueError("query 不可為空白")
    return {"original_query": state["query"]}


def test_harness_converts_exception_to_fatal_error_with_error_trace():
    """【AT1-06】節點拋例外 → fatal_error + errors + trace(status=error)，不往外拋。"""
    out = _run(harnessed("query_intake", _blank_query_node, writes=["original_query"]), {"query": "   "})

    assert out["fatal_error"].startswith("query_intake:")
    assert out["errors"][0]["node"] == "query_intake"
    assert out["errors"][0]["error_type"] == "ValueError"

    entry = out["trace"][0]
    assert entry.node_name == "query_intake"
    assert entry.status == "error"
    assert entry.error_code == "ValueError"


def test_harness_short_circuits_after_fatal_and_records_skipped():
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


def test_harness_runs_run_on_fatal_nodes_after_fatal():
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
