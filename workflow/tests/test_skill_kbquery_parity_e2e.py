"""kb_query Skill 的 parity e2e（AT2-21 ~ AT2-27）：編譯圖 == 手寫圖。

七個案例逐一複製 test_kbquery_e2e.py，假依賴沿用 kbquery_fakes.py，只把「手寫圖」
換成「skills/kb_query.yaml 的編譯圖」。每案除了複製原斷言之外，再跑一次手寫圖做
**逐鍵比對**：兩張圖在同一組輸入與同一組假依賴下，最終 state 必須完全相同。

比對排除的鍵只有「本質上不可能相同」的三類（不是為了遷就實作而放寬）：
- query_id：uuid4，每次執行必不同。
- query_timestamp、TraceEntry 的 start_time/end_time/latency_ms：牆鐘時間。
- audit_trail：其內容是上述兩者 + node_trace 的組合，改為逐欄位比對（見 _assert_parity）。

trace 是逐筆比對 TraceEntry 的**全部確定性欄位**（node_name/status/input_summary/
output_summary/error_code/component_version），不是只比節點名：
- output_summary 是 Harness `writes` 剝除行為的唯一觀測點——只比節點名的話，
  兩張圖有一邊剝錯鍵這個測試抓不到。
- component_version 兩邊的判定來源不同（編譯圖看 `"llm" in spec.deps`，手寫圖硬編
  `_LLM_NODES`），逐欄位比對才能把「目前結果一致」從巧合變成有測試保護的等價。
"""

import asyncio

from app import skills
from app.engine import compiler
from app.kbquery.graph import build_kb_query_graph
from app.kbquery.models import AnswerMode, IssueLabel, VerificationResult
from tests.kbquery_fakes import (
    TABLE_2025,
    TEXT_2025Q2,
    TEXT_2025Q3,
    TEXT_WRONG_PERIOD,
    FakeSearch,
    make_deps,
)

QUERY_2025Q3 = "2025Q3 稅後淨利是多少？"

# 每次執行必然不同的鍵（uuid / 牆鐘時間），逐鍵比對時排除
NONDETERMINISTIC = {"query_id", "query_timestamp", "trace", "audit_trail"}
# AuditTrail 內同樣不可能相同的欄位（node_trace 另以 _trace_projection 逐筆比對）
AUDIT_NONDETERMINISTIC = {"query_id", "query_timestamp", "node_trace"}
# TraceEntry 內的牆鐘欄位；其餘（node_name/status/input_summary/output_summary/
# error_code/component_version）皆為確定性，一律比對
TRACE_NONDETERMINISTIC = {"start_time", "end_time", "latency_ms"}


def _trace_projection(entries) -> list[dict]:
    """TraceEntry 去掉牆鐘欄位後的完整投影（其餘欄位全比）。"""
    return [
        e.model_dump(exclude=TRACE_NONDETERMINISTIC)
        if hasattr(e, "model_dump")
        else e
        for e in entries
    ]


def run_skill_graph(deps, query: str, **extra_state) -> dict:
    """用 skills/kb_query.yaml 的編譯圖跑一次（對照 kbquery_fakes.run_graph 的手寫圖版本）。"""
    graph = compiler.compile(skills.get("kb_query").skill, deps)
    output = asyncio.run(
        graph.ainvoke({"query": query, "tenant_id": "t-test", **extra_state})
    )
    return compiler.public_output(output)


def _assert_audit_landed(deps, result):
    """通用斷言：不論成敗，稽核必須恰好落地一筆，trace 必含首尾節點。"""
    assert len(deps.audit_repo.saved) == 1
    node_names = [t.node_name for t in result["trace"]]
    assert "query_intake" in node_names
    assert "audit_feedback" in node_names


def _assert_parity(make_deps_fn, query: str, skill_result: dict) -> None:
    """同一輸入下，手寫圖與編譯圖的最終 state 逐鍵相同。"""
    hand = asyncio.run(
        build_kb_query_graph(make_deps_fn()).ainvoke(
            {"query": query, "tenant_id": "t-test"}
        )
    )

    assert set(hand) == set(skill_result), (
        f"鍵集合不同: 只在手寫圖 {set(hand) - set(skill_result)}, "
        f"只在編譯圖 {set(skill_result) - set(hand)}"
    )
    for key in hand:
        if key in NONDETERMINISTIC:
            continue
        assert hand[key] == skill_result[key], f"鍵 {key} 不一致"

    # trace：逐筆比對所有確定性欄位（含 output_summary → Harness writes 剝除行為，
    # 與 component_version → LLM 節點判定），fatal 時的 skipped entry 也要對得上
    assert _trace_projection(hand["trace"]) == _trace_projection(skill_result["trace"])

    # audit_trail：逐欄位比對，只排除 uuid / 時間；node_trace 用同一投影逐筆比
    hand_trail = hand["audit_trail"]
    skill_trail = skill_result["audit_trail"]
    assert hand_trail.model_dump(exclude=AUDIT_NONDETERMINISTIC) == skill_trail.model_dump(
        exclude=AUDIT_NONDETERMINISTIC
    )
    assert _trace_projection(hand_trail.node_trace) == _trace_projection(
        skill_trail.node_trace
    )


def test_skill_e2e_single_value_lookup_success():
    """【AT2-21】單一數值查詢成功：ANSWER + 正確數值 + citation，無議題與回歸測項。"""

    def deps_fn():
        return make_deps({"vector": FakeSearch(lambda q, f: [TEXT_2025Q3])})

    deps = deps_fn()
    result = run_skill_graph(deps, QUERY_2025Q3)

    assert result["answer_mode"] == AnswerMode.ANSWER
    assert "1,234" in result["final_answer"]
    assert result["source_citations"]
    assert result["verification_result"] == VerificationResult.PASS
    assert result["issue_label"] is None
    assert result["regression_test_item"] is None
    _assert_audit_landed(deps, result)
    _assert_parity(deps_fn, QUERY_2025Q3, result)


def test_skill_e2e_table_cell_lookup_success():
    """【AT2-22】表格儲存格查詢成功：citation 精確到 row × column。"""
    query = "損益表中 2025Q3 稅後淨利是多少？"

    def deps_fn():
        return make_deps({"table": FakeSearch(lambda q, f: [TABLE_2025])})

    deps = deps_fn()
    result = run_skill_graph(deps, query)

    assert result["answer_mode"] == AnswerMode.ANSWER
    citation = result["source_citations"][0]
    assert citation.row == "稅後淨利"
    assert citation.column == "2025Q3"
    assert "1234" in result["final_answer"]
    _assert_audit_landed(deps, result)
    _assert_parity(deps_fn, query, result)


def test_skill_e2e_cross_document_comparison_success():
    """【AT2-23】跨文件比較成功：兩期間各選一筆證據，兩個數值都進最終答案。"""
    query = "比較 2025Q2 與 2025Q3 的稅後淨利"

    def deps_fn():
        return make_deps(
            {
                "vector": FakeSearch(lambda q, f: [TEXT_2025Q3, TEXT_2025Q2]),
                "metadata": FakeSearch(lambda q, f: []),
            }
        )

    deps = deps_fn()
    result = run_skill_graph(deps, query)

    assert result["answer_mode"] == AnswerMode.ANSWER
    assert len(result["selected_evidence"]) == 2
    assert {e.period for e in result["selected_evidence"]} == {"2025Q2", "2025Q3"}
    assert len(result["source_citations"]) == 2
    assert "1,100" in result["final_answer"]
    assert "1,234" in result["final_answer"]
    _assert_audit_landed(deps, result)
    _assert_parity(deps_fn, query, result)


def test_skill_e2e_retry_then_success():
    """【AT2-24】第一次驗證失敗（期間錯誤）、第二次依 PERIOD_MISMATCH 加 metadata 檢索成功。

    這案是 loop 語意的關鍵對照：YAML 的 until 條件必須與手寫圖的
    route_after_verification 產生同樣的重試輪數與同樣的 retrieval_plans 累加。
    """

    def deps_fn():
        return make_deps(
            {
                "vector": FakeSearch(lambda q, f: [TEXT_WRONG_PERIOD]),
                "metadata": FakeSearch(lambda q, f: [TEXT_2025Q3]),
            }
        )

    deps = deps_fn()
    result = run_skill_graph(deps, QUERY_2025Q3)

    assert result["answer_mode"] == AnswerMode.ANSWER
    assert result["retrieval_attempt"] == 2
    assert len(result["retrieval_plans"]) == 2
    assert "PERIOD_MISMATCH" in result["retrieval_plans"][1].adjustment_reason
    assert "1,234" in result["final_answer"]
    _assert_audit_landed(deps, result)
    _assert_parity(deps_fn, QUERY_2025Q3, result)


def test_skill_e2e_retry_exhausted_safe_abstain():
    """【AT2-25】重試耗盡：安全 ABSTAIN + 議題標籤 + 回歸測項 + 改進清單，稽核照樣落地。"""

    def deps_fn():
        return make_deps({"vector": FakeSearch(lambda q, f: [TEXT_WRONG_PERIOD])})

    deps = deps_fn()
    result = run_skill_graph(deps, QUERY_2025Q3)

    assert result["answer_mode"] == AnswerMode.ABSTAIN
    assert result["retrieval_attempt"] == 2  # == max_retrieval_attempts
    assert result["final_answer"].startswith("【無法提供答案】")
    assert "PERIOD_MISMATCH" in result["final_answer"]
    assert result["issue_label"] == IssueLabel.RETRIEVAL_MISS
    assert result["regression_test_item"] is not None
    assert result["regression_test_item"].question == QUERY_2025Q3
    assert result["improvement_backlog"]
    _assert_audit_landed(deps, result)
    _assert_parity(deps_fn, QUERY_2025Q3, result)


def test_skill_e2e_retry_cap_prevents_infinite_loop():
    """【AT2-26】RETRY 次數上限防護：max_attempts=3 時恰好在第 3 次停止。

    業務上限（max_retrieval_attempts，由 deps 注入、寫進 state）先於引擎上限
    （YAML 的 max_iterations: 10）收斂 —— 兩層護欄並存，行為與手寫圖相同。
    """

    def deps_fn():
        return make_deps(
            {"vector": FakeSearch(lambda q, f: [TEXT_WRONG_PERIOD])}, max_attempts=3
        )

    deps = deps_fn()
    result = run_skill_graph(deps, QUERY_2025Q3)

    assert result["retrieval_attempt"] == 3  # 到上限即停，不無限循環
    assert result["answer_mode"] == AnswerMode.ABSTAIN
    assert len(result["retrieval_plans"]) == 3
    _assert_audit_landed(deps, result)
    _assert_parity(deps_fn, QUERY_2025Q3, result)


def test_skill_e2e_blank_query_fatal_short_circuit():
    """【AT2-27】空白 query：intake fatal 短路，仍走安全 ABSTAIN + 稽核落地，errors 非空。

    fatal 必須能離開迴圈：until 的第一個子句 state.fatal_error != None 對應
    routing.py 的「fatal → 直接 compose」；loop body 跑一輪、四個節點皆為 skipped。
    """

    def deps_fn():
        return make_deps({"vector": FakeSearch(lambda q, f: [TEXT_2025Q3])})

    deps = deps_fn()
    result = run_skill_graph(deps, "   ")

    assert result["answer_mode"] == AnswerMode.ABSTAIN
    assert result["errors"]
    assert result["fatal_error"].startswith("query_intake")
    _assert_audit_landed(deps, result)
    _assert_parity(deps_fn, "   ", result)

    # fatal 後 loop body 的節點只會產生 skipped entry，不得真的執行
    skipped = {t.node_name for t in result["trace"] if t.status == "skipped"}
    assert {
        "retrieval_planner",
        "source_retrieval_rerank",
        "data_locator",
        "evidence_verification",
    } <= skipped
