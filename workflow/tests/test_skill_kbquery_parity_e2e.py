"""kb_query Skill 的 golden e2e（AT2-21 ~ AT2-27，原 parity e2e 改造）。

手寫圖（原 app.kbquery.graph）已隨 Node-First 遷移退役，不再有第二張圖可互比；七個案例
改為對編譯後的 skills/kb_query.yaml 輸出做**固定期望值**比對，取代原本的「兩圖逐鍵互比」。
業務層斷言（answer_mode／final_answer／citations…）逐字沿用已刪除的 test_kbquery_e2e.py，
行為覆蓋不因刪圖而消失。

trace 投影原本對七案例各自的 _GOLDEN_TRACE 做全欄位逐鍵比對；維護成本（kb_query 十節點族
任一個 state 鍵新增/改名/搬動都要重新產生全部 7 份快照，失敗訊息只會是「兩個超長 dict
不相等」，看不出真正改了什麼）與所守的觀測點（Harness `writes` 剝除行為／output_summary
與 component_version 判定）不成比例。收斂為：只留 single_value_lookup 一個代表案例做
全欄位 golden 比對，作為該觀測點的唯一錨點；其餘六案例改用 `_assert_audit_trail_structure`
（audit_feedback 恆為 trace 最後一筆、audit_trail.node_trace 與 trace 的結構關係——這正是
原本 golden trace 全比對真正在守的東西）加上案例特有的局部斷言（重試三案例驗
retrieval_planner 在 trace 中的重複次數＝迴圈實際跑的輪數；blank_query 案例驗 skipped
節點集合）。業務層斷言（各測試函式開頭那幾行）完全不動。

七個 AT 案例只覆蓋 {SINGLE_VALUE_LOOKUP, TABLE_LOOKUP, COMPARISON}×{PASS, RETRY}
與 max_retrieval_attempts 的 2／3；檔尾另有四個補洞案例（CALCULATION 的計算複驗、
PERFORMANCE_ANALYSIS 走 structured 來源、CAUSE_ANALYSIS 的無數值證據、
max_retrieval_attempts 下界 1），沿用同一組 helper 與斷言風格。
"""

import asyncio

from app import skills
from app.engine import compiler
from app.nodes.kbquery.models import (
    AnswerMode,
    IntentType,
    IssueLabel,
    SourceResult,
    VerificationResult,
)
from tests.kbquery_fakes import (
    TABLE_2025,
    TEXT_2025Q2,
    TEXT_2025Q3,
    TEXT_WRONG_PERIOD,
    FakeSearch,
    make_deps,
)

QUERY_2025Q3 = "2025Q3 稅後淨利是多少？"

# 本檔專用語料：共用語料庫（tests/kbquery_fakes.py）只有 text／table 兩種來源，缺
# structured 來源，也缺「命中指標詞但整句無數值」的文字段落——這兩種形狀各自打開一條
# 別的案例走不到的定位／驗證路徑（structured 免頁碼的可追溯性、摘錄型 candidate_answer）。
STRUCTURED_2025Q3 = SourceResult(
    source_id="fin-2025q3#s1",
    document_id="doc-fin-2025q3",
    document_title="2025Q3 財務季報",
    version="v1.0",
    page=None,
    source_type="structured",
    retrieval_method="structured",
    original_score=0.7,
    metadata={
        "record": {
            "metric": "稅後淨利",
            "period": "2025Q3",
            "value": 1234.0,
            "unit": "百萬元",
            "query_conditions": {"legal_entity": "TW"},
        }
    },
)

TEXT_CAUSE_2025Q3 = SourceResult(
    source_id="fin-2025q3#c9",
    document_id="doc-fin-2025q3",
    document_title="2025Q3 財務季報",
    version="v1.0",
    page=9,
    source_type="text",
    retrieval_method="vector",
    original_score=0.7,
    metadata={"content": "2025Q3 稅後淨利下降主因為市場波動。"},
)

# TraceEntry 內的牆鐘欄位；其餘（node_name/status/input_summary/output_summary/
# error_code/component_version）皆為確定性，一律比對
TRACE_NONDETERMINISTIC = {"start_time", "end_time", "latency_ms"}

# 唯一保留全欄位快照比對的代表案例（改造前對現行輸出跑一次、原樣入檔，見模組 docstring）。
_GOLDEN_TRACE = {'single_value_lookup': [{'node_name': 'query_intake',
                          'status': 'ok',
                          'input_summary': 'errors,query,retrieval_plans,tenant_id,trace',
                          'output_summary': 'answer_format_policy,max_retrieval_attempts,original_query,query_id,query_timestamp,retrieval_attempt,session_context,system_entrypoint,user_role',
                          'error_code': '',
                          'component_version': ''},
                         {'node_name': 'query_rewrite',
                          'status': 'ok',
                          'input_summary': 'answer_format_policy,errors,max_retrieval_attempts,original_query,query,query_id,query_timestamp,retrieval_attempt,retrieval_plans,session_context,system_entrypoint,tenant_id,trace,user_role',
                          'output_summary': 'normalized_query,query_variants,rewrite_reason',
                          'error_code': '',
                          'component_version': ''},
                         {'node_name': 'intent_classification',
                          'status': 'ok',
                          'input_summary': 'answer_format_policy,errors,max_retrieval_attempts,normalized_query,original_query,query,query_id,query_timestamp,query_variants,retrieval_attempt,retrieval_plans,rewrite_reason,session_context,system',
                          'output_summary': 'intent_type,question_type,requires_calculation,requires_multi_doc,requires_table',
                          'error_code': '',
                          'component_version': ''},
                         {'node_name': 'context_resolver',
                          'status': 'ok',
                          'input_summary': 'answer_format_policy,errors,intent_type,max_retrieval_attempts,normalized_query,original_query,query,query_id,query_timestamp,query_variants,question_type,requires_calculation,requires_multi_doc,requi',
                          'output_summary': 'canonical_metric,context_warnings,excluded_terms,metric_terms,target_period,unresolved_context,version_policy',
                          'error_code': '',
                          'component_version': ''},
                         {'node_name': 'retrieval_planner',
                          'status': 'ok',
                          'input_summary': 'answer_format_policy,canonical_metric,context_warnings,errors,excluded_terms,intent_type,max_retrieval_attempts,metric_terms,normalized_query,original_query,query,query_id,query_timestamp,query_varian',
                          'output_summary': 'filters,rerank_policy,retrieval_attempt,retrieval_plan,retrieval_plans,source_priority,top_k',
                          'error_code': '',
                          'component_version': ''},
                         {'node_name': 'source_retrieval_rerank',
                          'status': 'ok',
                          'input_summary': 'answer_format_policy,canonical_metric,context_warnings,errors,excluded_terms,filters,intent_type,max_retrieval_attempts,metric_terms,normalized_query,original_query,query,query_id,query_timestamp,quer',
                          'output_summary': 'candidate_documents,candidate_pages,ranked_sources',
                          'error_code': '',
                          'component_version': ''},
                         {'node_name': 'data_locator',
                          'status': 'ok',
                          'input_summary': 'answer_format_policy,candidate_documents,candidate_pages,canonical_metric,context_warnings,errors,excluded_terms,filters,intent_type,max_retrieval_attempts,metric_terms,normalized_query,original_query',
                          'output_summary': 'calculation_result,calculation_trace,candidate_answer,page_evidence,selected_evidence,table_cell_evidence,text_claims',
                          'error_code': '',
                          'component_version': ''},
                         {'node_name': 'evidence_verification',
                          'status': 'ok',
                          'input_summary': 'answer_format_policy,calculation_result,calculation_trace,candidate_answer,candidate_documents,candidate_pages,canonical_metric,context_warnings,errors,excluded_terms,filters,intent_type,max_retrieval',
                          'output_summary': 'confidence,failure_codes,failure_reason,verification_result,verified_evidence',
                          'error_code': '',
                          'component_version': ''},
                         {'node_name': 'answer_composer',
                          'status': 'ok',
                          'input_summary': 'answer_format_policy,calculation_result,calculation_trace,candidate_answer,candidate_documents,candidate_pages,canonical_metric,confidence,context_warnings,errors,excluded_terms,failure_codes,failure_',
                          'output_summary': 'answer_format_policy,answer_mode,assumption_note,final_answer,source_citations',
                          'error_code': '',
                          'component_version': ''},
                         {'node_name': 'audit_feedback',
                          'status': 'ok',
                          'input_summary': 'answer_format_policy,answer_mode,assumption_note,calculation_result,calculation_trace,candidate_answer,candidate_documents,candidate_pages,canonical_metric,confidence,context_warnings,errors,excluded_',
                          'output_summary': 'audit_trail,improvement_backlog,issue_label,regression_test_item',
                          'error_code': '',
                          'component_version': ''}]}


def _trace_projection(entries) -> list[dict]:
    """TraceEntry 去掉牆鐘欄位後的投影（其餘欄位全比）。"""
    return [e.model_dump(exclude=TRACE_NONDETERMINISTIC) for e in entries]


def run_skill_graph(deps, query: str, **extra_state) -> dict:
    """用 skills/kb_query.yaml 的編譯圖跑一次。"""
    graph = compiler.compile(skills.get("kb-query").skill, deps)
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


def _assert_audit_trail_structure(result: dict) -> None:
    """七案例共通的結構不變式（Harness `writes` 剝除行為與 component_version 判定的真正
    觀測點）：audit_feedback 恆為 trace 最後一筆；audit_trail.node_trace 落地時
    state["trace"] 尚未含 audit_feedback 自己這筆（trace 用 operator.add，節點回傳後才
    追加），因此 node_trace 恰為 trace 少最後一筆。非代表案例改用此結構性斷言取代全欄位
    golden trace 比對，見模組 docstring。
    """
    trace_projection = _trace_projection(result["trace"])
    assert result["trace"][-1].node_name == "audit_feedback"
    assert _trace_projection(result["audit_trail"].node_trace) == trace_projection[:-1]


def _assert_golden_trace(case: str, result: dict) -> None:
    """唯一的全欄位快照比對（僅 single_value_lookup 使用）：trace 投影逐鍵對照
    _GOLDEN_TRACE 釘死的期望值，並額外核對結構不變式。
    """
    trace_projection = _trace_projection(result["trace"])
    assert trace_projection == _GOLDEN_TRACE[case]
    _assert_audit_trail_structure(result)


def _count_node_runs(result: dict, node_name: str) -> int:
    """trace 中某節點被排進去的次數：重試迴圈每跑一輪，該節點就會在 trace 多一筆同名
    entry，藉此從 trace 結構（而非只看 state 累積欄位如 retrieval_plans 長度）獨立驗證
    迴圈確實重複執行了幾輪，且沒有多跑一輪。
    """
    return sum(1 for t in result["trace"] if t.node_name == node_name)


def test_skill_e2e_single_value_lookup_success():
    """【AT2-21】單一數值查詢成功：ANSWER + 正確數值 + citation，無議題與回歸測項。"""
    deps = make_deps({"vector": FakeSearch(lambda q, f: [TEXT_2025Q3])})
    result = run_skill_graph(deps, QUERY_2025Q3)

    assert result["answer_mode"] == AnswerMode.ANSWER
    assert "1,234" in result["final_answer"]
    assert result["source_citations"]
    assert result["verification_result"] == VerificationResult.PASS
    assert result["issue_label"] is None
    assert result["regression_test_item"] is None
    _assert_audit_landed(deps, result)
    _assert_golden_trace("single_value_lookup", result)


def test_skill_e2e_table_cell_lookup_success():
    """【AT2-22】表格儲存格查詢成功：citation 精確到 row × column。"""
    query = "損益表中 2025Q3 稅後淨利是多少？"
    deps = make_deps({"table": FakeSearch(lambda q, f: [TABLE_2025])})
    result = run_skill_graph(deps, query)

    assert result["answer_mode"] == AnswerMode.ANSWER
    citation = result["source_citations"][0]
    assert citation.row == "稅後淨利"
    assert citation.column == "2025Q3"
    assert "1234" in result["final_answer"]
    _assert_audit_landed(deps, result)
    _assert_audit_trail_structure(result)


def test_skill_e2e_cross_document_comparison_success():
    """【AT2-23】跨文件比較成功：兩期間各選一筆證據，兩個數值都進最終答案。"""
    query = "比較 2025Q2 與 2025Q3 的稅後淨利"
    deps = make_deps(
        {
            "vector": FakeSearch(lambda q, f: [TEXT_2025Q3, TEXT_2025Q2]),
            "metadata": FakeSearch(lambda q, f: []),
        }
    )
    result = run_skill_graph(deps, query)

    assert result["answer_mode"] == AnswerMode.ANSWER
    assert len(result["selected_evidence"]) == 2
    assert {e.period for e in result["selected_evidence"]} == {"2025Q2", "2025Q3"}
    assert len(result["source_citations"]) == 2
    assert "1,100" in result["final_answer"]
    assert "1,234" in result["final_answer"]
    _assert_audit_landed(deps, result)
    _assert_audit_trail_structure(result)


def test_skill_e2e_retry_then_success():
    """【AT2-24】第一次驗證失敗（期間錯誤）、第二次依 PERIOD_MISMATCH 加 metadata 檢索成功。"""
    deps = make_deps(
        {
            "vector": FakeSearch(lambda q, f: [TEXT_WRONG_PERIOD]),
            "metadata": FakeSearch(lambda q, f: [TEXT_2025Q3]),
        }
    )
    result = run_skill_graph(deps, QUERY_2025Q3)

    assert result["answer_mode"] == AnswerMode.ANSWER
    assert result["retrieval_attempt"] == 2
    assert len(result["retrieval_plans"]) == 2
    assert "PERIOD_MISMATCH" in result["retrieval_plans"][1].adjustment_reason
    assert "1,234" in result["final_answer"]
    _assert_audit_landed(deps, result)
    _assert_audit_trail_structure(result)
    assert _count_node_runs(result, "retrieval_planner") == 2  # 迴圈確實跑了兩輪


def test_skill_e2e_retry_exhausted_safe_abstain():
    """【AT2-25】重試耗盡：安全 ABSTAIN + 議題標籤 + 回歸測項 + 改進清單，稽核照樣落地。"""
    deps = make_deps({"vector": FakeSearch(lambda q, f: [TEXT_WRONG_PERIOD])})
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
    _assert_audit_trail_structure(result)
    assert _count_node_runs(result, "retrieval_planner") == 2  # 耗盡即停，不多跑一輪


def test_skill_e2e_retry_cap_prevents_infinite_loop():
    """【AT2-26】RETRY 次數上限防護：max_attempts=3 時恰好在第 3 次停止。

    業務上限（max_retrieval_attempts，由 deps 注入、寫進 state）先於引擎上限
    （YAML 的 max_iterations: 10）收斂。
    """
    deps = make_deps(
        {"vector": FakeSearch(lambda q, f: [TEXT_WRONG_PERIOD])}, max_attempts=3
    )
    result = run_skill_graph(deps, QUERY_2025Q3)

    assert result["retrieval_attempt"] == 3  # 到上限即停，不無限循環
    assert result["answer_mode"] == AnswerMode.ABSTAIN
    assert len(result["retrieval_plans"]) == 3
    _assert_audit_landed(deps, result)
    _assert_audit_trail_structure(result)
    assert _count_node_runs(result, "retrieval_planner") == 3  # 恰好 3 輪，不多跑第 4 輪


def test_skill_e2e_blank_query_fatal_short_circuit():
    """【AT2-27】空白 query：intake fatal 短路，仍走安全 ABSTAIN + 稽核落地，errors 非空。

    fatal 後 loop body 的節點只會產生 skipped entry，不得真的執行。
    """
    deps = make_deps({"vector": FakeSearch(lambda q, f: [TEXT_2025Q3])})
    result = run_skill_graph(deps, "   ")

    assert result["answer_mode"] == AnswerMode.ABSTAIN
    assert result["errors"]
    assert result["fatal_error"].startswith("query_intake")
    _assert_audit_landed(deps, result)
    _assert_audit_trail_structure(result)

    skipped = {t.node_name for t in result["trace"] if t.status == "skipped"}
    assert {
        "retrieval_planner",
        "source_retrieval_rerank",
        "data_locator",
        "evidence_verification",
    } <= skipped


# ---------------------------------------------------------------------------
# 七個 AT 案例之外的補洞案例（見模組 docstring）
# ---------------------------------------------------------------------------


def test_skill_e2e_calculation_growth_rate_success():
    """CALCULATION 意圖成功：requires_calculation 打開 evidence_verification 的計算複驗。

    複驗做兩件 AT2-21~27 都碰不到的事：以 calculator 重算公式比對 trace.result，
    以及要求每個輸入值都能對回某筆證據的 exact_value；兩者都過才 PASS。
    """
    deps = make_deps({"vector": FakeSearch(lambda q, f: [TEXT_2025Q3, TEXT_2025Q2])})
    result = run_skill_graph(deps, "2025Q2 到 2025Q3 稅後淨利成長率")

    assert result["intent_type"] == IntentType.CALCULATION
    assert result["requires_calculation"] is True
    assert result["verification_result"] == VerificationResult.PASS
    assert result["answer_mode"] == AnswerMode.ANSWER
    trace = result["calculation_trace"]
    assert trace.formula == "(a - b) / b * 100"
    assert trace.inputs == {"a": 1234.0, "b": 1100.0}  # a=較晚期間、b=較早期間
    assert trace.input_sources == [TEXT_2025Q2.source_id, TEXT_2025Q3.source_id]
    # candidate_answer 只留 round(…, 4) 的顯示值，複驗以 _DISPLAY_EPS 容差放行
    assert result["final_answer"].startswith("【結論】稅後淨利成長率為 12.1818%")
    assert "【計算】(a - b) / b * 100" in result["final_answer"]
    _assert_audit_landed(deps, result)
    _assert_audit_trail_structure(result)
    assert _count_node_runs(result, "retrieval_planner") == 1  # 一次就 PASS，不進重試


def test_skill_e2e_structured_record_lookup_success():
    """structured 來源成功：StructuredDataLocator 定位資料列，無頁碼仍通過可追溯性。

    PERFORMANCE_ANALYSIS 的檢索計畫含 structured 方法；證據以「查詢條件 JSON」當
    row_identifier，可追溯性檢查對 structured 免頁碼（其餘來源缺頁碼即 NOT_TRACEABLE）。
    """
    deps = make_deps({"structured": FakeSearch(lambda q, f: [STRUCTURED_2025Q3])})
    result = run_skill_graph(deps, "2025Q3 稅後淨利的達成率如何？")

    assert result["intent_type"] == IntentType.PERFORMANCE_ANALYSIS
    assert result["verification_result"] == VerificationResult.PASS
    assert result["answer_mode"] == AnswerMode.ANSWER
    evidence = result["selected_evidence"][0]
    assert evidence.source_type == "structured"
    assert evidence.page_number is None
    citation = result["source_citations"][0]
    assert citation.page is None
    assert citation.row == '{"legal_entity": "TW"}'
    assert result["confidence"] == 0.95
    assert "1234.0" in result["final_answer"]
    _assert_audit_landed(deps, result)
    _assert_audit_trail_structure(result)


def test_skill_e2e_cause_analysis_excerpt_answer_success():
    """CAUSE_ANALYSIS 意圖成功：證據無精確數值，以原文摘錄成稿仍 PASS。

    數值一致性檢查對「沒有數字的候選答案」不得誤判（期間 token 已被遮罩），
    可追溯性靠 exact_excerpt + 頁碼成立。
    """
    deps = make_deps({"vector": FakeSearch(lambda q, f: [TEXT_CAUSE_2025Q3])})
    result = run_skill_graph(deps, "為什麼 2025Q3 稅後淨利下降？")

    assert result["intent_type"] == IntentType.CAUSE_ANALYSIS
    assert result["verification_result"] == VerificationResult.PASS
    assert result["answer_mode"] == AnswerMode.ANSWER
    evidence = result["selected_evidence"][0]
    assert evidence.exact_value == ""
    assert evidence.locator_score == 0.3  # 無數值文字證據的固定分數
    assert result["confidence"] == 0.3
    assert result["final_answer"].startswith("【結論】2025Q3 稅後淨利下降主因為市場波動")
    assert result["source_citations"][0].page == 9
    _assert_audit_landed(deps, result)
    _assert_audit_trail_structure(result)


def test_skill_e2e_max_attempts_one_disallows_any_retry():
    """max_retrieval_attempts=1（業務上限下界）：第一輪就達上限，RETRY 也不再重試。

    對照 AT2-25（上限 2，可重試一次）：同樣的干擾語料在上限 1 時，verification_result
    停在 RETRY 就收斂，計畫只留一份且不含任何重試調整。
    """
    deps = make_deps(
        {"vector": FakeSearch(lambda q, f: [TEXT_WRONG_PERIOD])}, max_attempts=1
    )
    result = run_skill_graph(deps, QUERY_2025Q3)

    assert result["retrieval_attempt"] == 1  # == max_retrieval_attempts
    assert result["verification_result"] == VerificationResult.RETRY  # 未收斂即被上限截停
    assert result["answer_mode"] == AnswerMode.ABSTAIN
    assert len(result["retrieval_plans"]) == 1
    assert result["retrieval_plans"][0].adjustment_reason == ""  # 沒有第二輪可調整
    assert result["issue_label"] == IssueLabel.RETRIEVAL_MISS
    _assert_audit_landed(deps, result)
    _assert_audit_trail_structure(result)
    assert _count_node_runs(result, "retrieval_planner") == 1  # 迴圈只跑一輪
