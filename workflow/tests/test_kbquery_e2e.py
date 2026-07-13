"""kb_query 端到端測試：全用 Fake Adapter 跑完整圖，可重複執行、不打網路、不用 LLM。

涵蓋五個規格要求的 e2e 情境（成功、表格、比較、重試後成功、重試耗盡 abstain），
外加 RETRY 上限防護與空白 query fatal 短路。每個案例都以 _assert_audit_landed
確認稽核一定落地、trace 首尾節點齊全。
"""

from app.kbquery.models import AnswerMode, IssueLabel, VerificationResult
from tests.kbquery_fakes import (
    TABLE_2025,
    TEXT_2025Q2,
    TEXT_2025Q3,
    TEXT_WRONG_PERIOD,
    FakeSearch,
    make_deps,
    run_graph,
)

QUERY_2025Q3 = "2025Q3 稅後淨利是多少？"


def _assert_audit_landed(deps, result):
    """通用斷言：不論成敗，稽核必須恰好落地一筆，trace 必含首尾節點。"""
    assert len(deps.audit_repo.saved) == 1
    node_names = [t.node_name for t in result["trace"]]
    assert "query_intake" in node_names
    assert "audit_feedback" in node_names


def test_e2e_single_value_lookup_success():
    """【E2E-1】單一數值查詢成功：ANSWER + 正確數值 + citation，無議題與回歸測項。"""
    deps = make_deps({"vector": FakeSearch(lambda q, f: [TEXT_2025Q3])})
    result = run_graph(deps, QUERY_2025Q3)

    assert result["answer_mode"] == AnswerMode.ANSWER
    assert "1,234" in result["final_answer"]
    assert result["source_citations"]
    assert result["verification_result"] == VerificationResult.PASS
    assert result["issue_label"] is None
    assert result["regression_test_item"] is None
    _assert_audit_landed(deps, result)


def test_e2e_table_cell_lookup_success():
    """【E2E-2】表格儲存格查詢成功：citation 精確到 row × column。"""
    deps = make_deps({"table": FakeSearch(lambda q, f: [TABLE_2025])})
    result = run_graph(deps, "損益表中 2025Q3 稅後淨利是多少？")

    assert result["answer_mode"] == AnswerMode.ANSWER
    citation = result["source_citations"][0]
    assert citation.row == "稅後淨利"
    assert citation.column == "2025Q3"
    assert "1234" in result["final_answer"]
    _assert_audit_landed(deps, result)


def test_e2e_cross_document_comparison_success():
    """【E2E-3】跨文件比較成功：兩期間各選一筆證據，兩個數值都進最終答案。"""
    deps = make_deps(
        {
            "vector": FakeSearch(lambda q, f: [TEXT_2025Q3, TEXT_2025Q2]),
            "metadata": FakeSearch(lambda q, f: []),
        }
    )
    result = run_graph(deps, "比較 2025Q2 與 2025Q3 的稅後淨利")

    assert result["answer_mode"] == AnswerMode.ANSWER
    assert len(result["selected_evidence"]) == 2
    assert {e.period for e in result["selected_evidence"]} == {"2025Q2", "2025Q3"}
    assert len(result["source_citations"]) == 2
    assert "1,100" in result["final_answer"]
    assert "1,234" in result["final_answer"]
    _assert_audit_landed(deps, result)


def test_e2e_retry_then_success():
    """【E2E-4】第一次驗證失敗（期間錯誤）、第二次依 PERIOD_MISMATCH 加 metadata 檢索成功。"""
    deps = make_deps(
        {
            # vector 永遠回錯誤期間；metadata 只在第二輪計畫（因 PERIOD_MISMATCH 加入）被啟用
            "vector": FakeSearch(lambda q, f: [TEXT_WRONG_PERIOD]),
            "metadata": FakeSearch(lambda q, f: [TEXT_2025Q3]),
        }
    )
    result = run_graph(deps, QUERY_2025Q3)

    assert result["answer_mode"] == AnswerMode.ANSWER
    assert result["retrieval_attempt"] == 2
    assert len(result["retrieval_plans"]) == 2
    assert "PERIOD_MISMATCH" in result["retrieval_plans"][1].adjustment_reason
    assert "1,234" in result["final_answer"]
    _assert_audit_landed(deps, result)


def test_e2e_retry_exhausted_safe_abstain():
    """【E2E-5】重試耗盡：安全 ABSTAIN + 議題標籤 + 回歸測項 + 改進清單，稽核照樣落地。"""
    deps = make_deps({"vector": FakeSearch(lambda q, f: [TEXT_WRONG_PERIOD])})
    result = run_graph(deps, QUERY_2025Q3)

    assert result["answer_mode"] == AnswerMode.ABSTAIN
    assert result["retrieval_attempt"] == 2  # == max_retrieval_attempts
    assert result["final_answer"].startswith("【無法提供答案】")
    assert "PERIOD_MISMATCH" in result["final_answer"]
    assert result["issue_label"] == IssueLabel.RETRIEVAL_MISS
    assert result["regression_test_item"] is not None
    assert result["regression_test_item"].question == QUERY_2025Q3
    assert result["improvement_backlog"]
    _assert_audit_landed(deps, result)


def test_e2e_retry_cap_prevents_infinite_loop():
    """【E2E-6／規格 13】RETRY 次數上限防護：max_attempts=3 時恰好在第 3 次停止。"""
    deps = make_deps(
        {"vector": FakeSearch(lambda q, f: [TEXT_WRONG_PERIOD])}, max_attempts=3
    )
    result = run_graph(deps, QUERY_2025Q3)

    assert result["retrieval_attempt"] == 3  # 到上限即停，不無限循環
    assert result["answer_mode"] == AnswerMode.ABSTAIN
    assert len(result["retrieval_plans"]) == 3
    _assert_audit_landed(deps, result)


def test_e2e_blank_query_fatal_short_circuit():
    """【E2E-7】空白 query：intake fatal 短路，仍走安全 ABSTAIN + 稽核落地，errors 非空。"""
    deps = make_deps({"vector": FakeSearch(lambda q, f: [TEXT_2025Q3])})
    result = run_graph(deps, "   ")

    assert result["answer_mode"] == AnswerMode.ABSTAIN
    assert result["errors"]
    assert result["fatal_error"].startswith("query_intake")
    _assert_audit_landed(deps, result)
