"""kb_query 節點單元測試：直接呼叫節點函式或 helper，全程不打網路、不用 LLM。

規格編號對照寫在各測試 docstring（【規格 N】）；async 節點一律以
asyncio.run(...) 執行（本專案無 pytest-asyncio，不新增依賴）。
"""

import asyncio

import pytest

from app.engine.node_shell import harnessed as traced
from app.nodes.kbquery import calculator
from app.nodes.kbquery.adapters import (
    LangChainStructuredLLM,
    ScoreReranker,
    StaticGlossary,
)
from app.nodes.kbquery.locators import TableCellLocator
from app.nodes.kbquery.models import (
    AnswerMode,
    CalculationTrace,
    Evidence,
    FailureCode,
    IntentOutput,
    IntentType,
    QueryRewriteOutput,
    RetrievalPlan,
    SourceResult,
    VerificationResult,
)
from app.nodes.kbquery.nodes.answer_composer import make_answer_composer_node
from app.nodes.kbquery.nodes.context_resolver import make_context_resolver_node
from app.nodes.kbquery.nodes.data_locator import make_data_locator_node
from app.nodes.kbquery.nodes.evidence_verification import (
    make_evidence_verification_node,
)
from app.nodes.kbquery.nodes.intent_classification import (
    classify_by_rules,
    make_intent_classification_node,
)
from app.nodes.kbquery.nodes.query_intake import make_query_intake_node
from app.nodes.kbquery.nodes.query_rewrite import make_query_rewrite_node
from app.nodes.kbquery.nodes.retrieval_planner import make_retrieval_planner_node
from app.nodes.kbquery.nodes.source_retrieval_rerank import (
    make_source_retrieval_rerank_node,
)
from tests.kbquery_fakes import (
    TABLE_2025,
    TEXT_2025Q3,
    TEXT_WRONG_PERIOD,
    FakeSearch,
    FakeStructuredLLM,
    make_deps,
    run_graph,
)

_GLOSSARY = StaticGlossary()


# ---------------------------------------------------------------------------
# runtime.traced：不可變鍵防護
# ---------------------------------------------------------------------------


async def _tampering_node(state: dict) -> dict:
    """假節點：企圖覆寫 original_query。"""
    return {"original_query": "被竄改", "normalized_query": "x"}


def test_traced_strips_immutable_keys():
    """【規格 1】original_query 不可覆寫：非 intake 節點的輸出被 traced 剝除該鍵。"""
    out = asyncio.run(traced("query_rewrite", _tampering_node)({}))
    assert "original_query" not in out
    assert out["normalized_query"] == "x"
    assert out["trace"][0].node_name == "query_rewrite"


def test_traced_keeps_immutable_keys_for_intake():
    """【規格 1】query_intake 是唯一可寫入不可變鍵的節點，traced 不剝除。"""
    out = asyncio.run(traced("query_intake", _tampering_node)({}))
    assert out["original_query"] == "被竄改"


# ---------------------------------------------------------------------------
# Query Intake
# ---------------------------------------------------------------------------


def test_intake_registers_query():
    """【規格 2】intake 登記 query_id / original_query / timestamp，attempt 歸零。"""
    node = make_query_intake_node(2)
    out = asyncio.run(node({"query": "2025Q3 稅後淨利是多少"}))
    assert out["query_id"]
    assert out["original_query"] == "2025Q3 稅後淨利是多少"
    assert out["query_timestamp"]
    assert out["retrieval_attempt"] == 0


def test_intake_blank_query_raises():
    """【規格 2】空白 query 直接 raise ValueError。"""
    node = make_query_intake_node(2)
    with pytest.raises(ValueError):
        asyncio.run(node({"query": "  "}))


def test_intake_blank_query_becomes_fatal_via_traced():
    """【規格 2】經 traced 包裝後，空白 query 轉為 fatal_error 而非拋出例外。"""
    wrapped = traced("query_intake", make_query_intake_node(2))
    out = asyncio.run(wrapped({"query": "  "}))
    assert out["fatal_error"].startswith("query_intake")
    assert out["errors"]
    assert out["trace"][0].status == "error"


# ---------------------------------------------------------------------------
# Query Rewrite
# ---------------------------------------------------------------------------


def test_rewrite_fallback_without_llm():
    """【規格 3】LLM 不可用：退回原始問題，variants 含原句，reason 提及不可用。"""
    node = make_query_rewrite_node(None, _GLOSSARY)
    q = "2025Q3 稅後淨利是多少"
    out = asyncio.run(node({"original_query": q}))
    assert out["normalized_query"] == q
    assert q in out["query_variants"]
    assert "不可用" in out["rewrite_reason"]


def test_rewrite_drops_variant_with_new_period():
    """【規格 4】語意防護：引入原問題沒有的期間（2024Q1）的 variant 必須丟棄。"""
    q = "2025Q3 稅後淨利是多少"
    llm = FakeStructuredLLM(
        {
            QueryRewriteOutput: QueryRewriteOutput(
                normalized_query=q,
                query_variants=["2025Q3 稅後淨利金額", "2024Q1 稅後淨利"],
                rewrite_reason="同義改寫",
            )
        }
    )
    out = asyncio.run(make_query_rewrite_node(llm, _GLOSSARY)({"original_query": q}))
    assert "2025Q3 稅後淨利金額" in out["query_variants"]  # 合法同義句保留
    assert all("2024Q1" not in v for v in out["query_variants"])  # 新期間被丟棄
    assert q in out["query_variants"]  # 必含 original


def test_rewrite_caps_merged_variants_at_five():
    """【規格 3】上限邊界：original + 5 個合法 variant 共 6 筆 → 截斷成剛好 5 筆，
    original 保留在第一位，超出上限的最後一個 variant 被丟掉。"""
    q = "2025Q3 稅後淨利是多少"
    variants = [
        "2025Q3 稅後淨利金額",
        "2025Q3 稅後淨利數字",
        "2025Q3 的稅後淨利",
        "稅後淨利 2025Q3",
        "2025Q3 稅後淨利多少錢",  # 第 6 筆（含 original），超出上限
    ]
    llm = FakeStructuredLLM(
        {
            QueryRewriteOutput: QueryRewriteOutput(
                normalized_query=q,
                query_variants=variants,
                rewrite_reason="同義改寫",
            )
        }
    )
    out = asyncio.run(make_query_rewrite_node(llm, _GLOSSARY)({"original_query": q}))
    assert out["query_variants"] == [q, *variants[:4]]
    assert variants[4] not in out["query_variants"]


# ---------------------------------------------------------------------------
# Intent Classification：確定性規則
# ---------------------------------------------------------------------------


@pytest.mark.parametrize(
    ("question", "expected"),
    [
        ("2025Q3 稅後淨利是多少", IntentType.SINGLE_VALUE_LOOKUP),
        ("損益表中稅後淨利是多少", IntentType.TABLE_LOOKUP),
        ("比較 2025Q2 與 2025Q3 的稅後淨利", IntentType.COMPARISON),
        ("2025Q3 相對 2025Q2 稅後淨利成長率是多少", IntentType.CALCULATION),
        ("為什麼 2025Q3 稅後淨利下降", IntentType.CAUSE_ANALYSIS),
        ("2025Q3 的績效達成率如何", IntentType.PERFORMANCE_ANALYSIS),
        ("這個數字出自哪份文件", IntentType.DOCUMENT_LOCATION),
        ("完全無關的閒聊", IntentType.UNKNOWN),
    ],
    ids=[
        "single-value",
        "table",
        "comparison",
        "calculation",
        "cause",
        "performance",
        "document-location",
        "unknown",
    ],
)
def test_classify_by_rules(question, expected):
    """【規格 5】關鍵詞規則分類：先中先贏，無法判斷回 UNKNOWN。"""
    intent, _ = classify_by_rules(question)
    assert intent == expected


def test_classify_by_rules_first_match_wins_on_overlap():
    """【規格 5】同時命中多組關鍵詞（比較詞 + factoid 詞）時，_RULES 列表順序決定優先權：
    COMPARISON 排在 SINGLE_VALUE_LOOKUP 之前，先中先贏。"""
    intent, label = classify_by_rules("比較 2025Q3 稅後淨利金額")
    assert intent == IntentType.COMPARISON
    assert label == "comparison"


def test_intent_classification_llm_fallback_confidence_threshold():
    """【規格 5】節點層的 LLM 補位分支：規則判 UNKNOWN 才問 LLM，且信心值剛好等於
    門檻（0.6）才採用、低於門檻（0.59）維持 UNKNOWN 不硬猜——先前只測 module 級的
    classify_by_rules，整條 UNKNOWN→LLM 交棒與 requires_* 輸出零覆蓋。"""
    q = "完全無關的閒聊"

    accepted = make_intent_classification_node(
        FakeStructuredLLM(
            {IntentOutput: IntentOutput(intent_type=IntentType.TABLE_LOOKUP, confidence=0.6)}
        )
    )
    out = asyncio.run(accepted({"normalized_query": q}))
    assert out["intent_type"] == IntentType.TABLE_LOOKUP
    assert out["question_type"] == "table"  # LLM 未給 question_type → 取規則標籤
    assert out["requires_table"] is True
    assert out["requires_calculation"] is False
    assert out["requires_multi_doc"] is False

    rejected = make_intent_classification_node(
        FakeStructuredLLM(
            {IntentOutput: IntentOutput(intent_type=IntentType.TABLE_LOOKUP, confidence=0.59)}
        )
    )
    out_low = asyncio.run(rejected({"normalized_query": q}))
    assert out_low["intent_type"] == IntentType.UNKNOWN
    assert out_low["question_type"] == "unknown"
    assert out_low["requires_table"] is False


# ---------------------------------------------------------------------------
# Context Resolver：口徑版本與期間
# ---------------------------------------------------------------------------


@pytest.mark.parametrize(
    ("question", "dimension", "value", "excluded"),
    [
        ("2025Q3 稅前淨利是多少", "tax", "PRE_TAX", "稅後"),
        ("2025Q3 稅後淨利是多少", "tax", "POST_TAX", "稅前"),
        ("2025Q3 提存前淨利是多少", "provision", "PRE_PROVISION", "提存後"),
        ("2025Q3 績效帳收益是多少", "book", "PERFORMANCE", "會計帳"),
    ],
    ids=["pre-tax", "post-tax", "pre-provision", "performance-book"],
)
def test_context_version_policy(question, dimension, value, excluded):
    """【規格 6】口徑詞解析：version_policy 設定正確維度，反義詞進 excluded_terms。"""
    node = make_context_resolver_node(_GLOSSARY)
    out = asyncio.run(node({"normalized_query": question}))
    assert out["version_policy"][dimension] == value
    assert excluded in out["excluded_terms"]


def test_context_missing_period_not_guessed():
    """【規格 6】無期間且 intent 需要期間：進 unresolved_context，不做任何猜測。"""
    node = make_context_resolver_node(_GLOSSARY)
    out = asyncio.run(
        node(
            {
                "normalized_query": "稅後淨利是多少",
                "intent_type": IntentType.SINGLE_VALUE_LOOKUP,
            }
        )
    )
    assert "target_period" in out["unresolved_context"]
    assert out["target_period"] == ""  # 不猜測


def test_context_bare_quarter_unresolved():
    """【規格 6】只有季別沒有年度（Q3）：target_period 進 unresolved，不補年度。"""
    node = make_context_resolver_node(_GLOSSARY)
    out = asyncio.run(node({"normalized_query": "Q3 稅後淨利是多少"}))
    assert "target_period" in out["unresolved_context"]


# ---------------------------------------------------------------------------
# Retrieval Planner
# ---------------------------------------------------------------------------


@pytest.mark.parametrize(
    ("intent", "must_have", "must_not_have"),
    [
        (IntentType.TABLE_LOOKUP, ["table"], []),
        (IntentType.SINGLE_VALUE_LOOKUP, ["vector"], ["table"]),
        (IntentType.CALCULATION, ["structured"], []),
    ],
    ids=["table-lookup", "single-value", "calculation"],
)
def test_planner_methods_by_intent(intent, must_have, must_not_have):
    """【規格 7】planner 依 intent 分流檢索方法。"""
    node = make_retrieval_planner_node(8)
    out = asyncio.run(node({"intent_type": intent}))
    methods = out["retrieval_plan"].methods
    for m in must_have:
        assert m in methods
    for m in must_not_have:
        assert m not in methods


def test_planner_retry_period_mismatch_adds_metadata():
    """【規格 8】重試調整：PERIOD_MISMATCH → 加 metadata 檢索 + strict_period。"""
    node = make_retrieval_planner_node(8)
    out = asyncio.run(
        node(
            {
                "intent_type": IntentType.SINGLE_VALUE_LOOKUP,
                "retrieval_attempt": 1,
                "failure_codes": [FailureCode.PERIOD_MISMATCH],
                "target_period": "2025Q3",
            }
        )
    )
    plan = out["retrieval_plan"]
    assert "metadata" in plan.methods
    assert out["filters"]["strict_period"] is True
    assert plan.adjustment_reason
    assert out["retrieval_attempt"] == 2


def test_planner_retry_insufficient_evidence_doubles_top_k():
    """【規格 8】重試調整：INSUFFICIENT_EVIDENCE → top_k 加倍。"""
    node = make_retrieval_planner_node(8)
    out = asyncio.run(
        node(
            {
                "intent_type": IntentType.SINGLE_VALUE_LOOKUP,
                "retrieval_attempt": 1,
                "failure_codes": [FailureCode.INSUFFICIENT_EVIDENCE],
            }
        )
    )
    assert out["top_k"] == 16


def test_planner_remaining_adjustment_branches_and_multi_doc_top_k():
    """【規格 8】其餘重試調整分支各驗一次：METRIC_MISMATCH → 加 keyword 檢索；
    TABLE_CELL_MISMATCH → 前插 table/structured。另驗 requires_multi_doc 在首輪
    （attempt=1、無 failure_codes）就讓 top_k 加倍，與重試調整無關。"""
    node = make_retrieval_planner_node(8)

    out_metric = asyncio.run(
        node(
            {
                "intent_type": IntentType.TABLE_LOOKUP,  # 基本盤無 keyword，才看得出加了
                "retrieval_attempt": 1,
                "failure_codes": [FailureCode.METRIC_MISMATCH],
            }
        )
    )
    plan_metric = out_metric["retrieval_plan"]
    assert "keyword" in plan_metric.methods
    assert plan_metric.use_keyword is True
    assert "加 keyword 檢索" in plan_metric.adjustment_reason
    assert out_metric["top_k"] == 8  # 這條規則不動 top_k

    out_cell = asyncio.run(
        node(
            {
                "intent_type": IntentType.SINGLE_VALUE_LOOKUP,
                "retrieval_attempt": 1,
                "failure_codes": [FailureCode.TABLE_CELL_MISMATCH],
            }
        )
    )
    plan_cell = out_cell["retrieval_plan"]
    assert plan_cell.methods[:2] == ["table", "structured"]  # 前插，不是附加
    assert plan_cell.use_table is True and plan_cell.use_structured is True
    assert "前插 table/structured 檢索" in plan_cell.adjustment_reason

    out_multi = asyncio.run(
        node(
            {
                "intent_type": IntentType.SINGLE_VALUE_LOOKUP,
                "requires_multi_doc": True,
            }
        )
    )
    assert out_multi["retrieval_attempt"] == 1
    assert out_multi["top_k"] == 16
    assert out_multi["retrieval_plan"].adjustment_reason == ""  # 首輪不做重試調整


# ---------------------------------------------------------------------------
# Rerank 與 Locator
# ---------------------------------------------------------------------------


def test_rerank_prefers_target_period():
    """【規格 9】rerank：期間符合的來源排前面（即使 original_score 較低），留下理由。"""
    sources = [s.model_copy(deep=True) for s in (TEXT_WRONG_PERIOD, TEXT_2025Q3)]
    ranked = asyncio.run(
        ScoreReranker().rerank(
            "2025Q3 稅後淨利是多少",
            sources,
            policy="score_with_context_boost",
            context={
                "target_period": "2025Q3",
                "metric_terms": ["稅後淨利"],
                "excluded_terms": [],
            },
        )
    )
    assert ranked[0].source_id == TEXT_2025Q3.source_id
    assert all(s.rerank_reason for s in ranked)


def test_table_cell_locator_exact_cell():
    """【規格 10】表格定位：必須定位到唯一的 row × column 儲存格，不回整張表。"""
    evidences = TableCellLocator().locate(
        TABLE_2025.model_copy(deep=True),
        context={
            "canonical_metric": "稅後淨利",
            "metric_terms": ["稅後淨利"],
            "target_period": "2025Q3",
            "excluded_terms": [],
            "query": "損益表中 2025Q3 稅後淨利是多少",
        },
    )
    assert len(evidences) == 1
    e = evidences[0]
    assert e.row_identifier == "稅後淨利"
    assert e.column_identifier == "2025Q3"
    assert e.exact_value == "1234.0"
    assert e.unit == "百萬元"


def test_table_cell_locator_without_metric_terms_or_target_period():
    """【規格 10】另兩種輸入組合：無 metric_terms → 無法確定列，一律不回證據；
    有 metric_terms 但無 target_period → 該列所有欄位都成為證據，分數降為 0.6。"""
    locator = TableCellLocator()

    no_metric = locator.locate(
        TABLE_2025.model_copy(deep=True),
        context={
            "canonical_metric": "稅後淨利",
            "metric_terms": [],
            "target_period": "2025Q3",
            "excluded_terms": [],
            "query": "損益表中 2025Q3 稅後淨利是多少",
        },
    )
    assert no_metric == []

    no_period = locator.locate(
        TABLE_2025.model_copy(deep=True),
        context={
            "canonical_metric": "稅後淨利",
            "metric_terms": ["稅後淨利"],
            "excluded_terms": [],
            "query": "損益表中稅後淨利是多少",
        },
    )
    assert [e.column_identifier for e in no_period] == ["2025Q2", "2025Q3"]
    assert all(e.row_identifier == "稅後淨利" for e in no_period)
    assert all(e.locator_score == 0.6 for e in no_period)


# ---------------------------------------------------------------------------
# Source Retrieval & Rerank：未串接方法略過、去重保留高分
# ---------------------------------------------------------------------------


def test_source_retrieval_rerank_dedup_and_unwired_method_skipped():
    """未串接的檢索方法（keyword）被 debug log 略過、不 raise；同一來源重複結果
    去重時保留 original_score 較高者——目前只有 vector 接線，這是讓 kb-query
    在其餘方法接上前仍能正常運作的關鍵防護。"""
    dup_low = SourceResult(
        source_id="fin-2025q3#c1",
        document_id="doc-fin-2025q3",
        document_title="2025Q3 財務季報",
        page=3,
        source_type="text",
        retrieval_method="vector",
        original_score=0.5,
    )
    dup_high = dup_low.model_copy(update={"original_score": 0.9})
    plan = RetrievalPlan(methods=["vector", "keyword"], source_priority=["vector"])
    node = make_source_retrieval_rerank_node(
        {"vector": FakeSearch(lambda q, f: [dup_low, dup_high])}, ScoreReranker()
    )
    out = asyncio.run(
        node(
            {
                "retrieval_plan": plan,
                "normalized_query": "2025Q3 稅後淨利",
                "tenant_id": "t-test",
            }
        )
    )
    # keyword 沒有對應 searcher：若未略過會 raise，能跑到這裡本身就是斷言的一部分
    assert len(out["ranked_sources"]) == 1
    assert out["ranked_sources"][0].original_score == 0.9


# ---------------------------------------------------------------------------
# Data Locator：差異與比率公式規則
# ---------------------------------------------------------------------------


class _FixedLocator:
    """假 EvidenceLocatorPort：依 source_id 回傳預先準備好的 Evidence。"""

    def __init__(self, evidences_by_source_id: dict[str, Evidence]):
        self._by_id = evidences_by_source_id

    def locate(self, source, *, context):
        e = self._by_id.get(source.source_id)
        return [e] if e is not None else []


def test_data_locator_formula_rules_diff_and_ratio():
    """【_FORMULA_RULES】「差/差異」與「比率/占比」公式規則各驗一次——先前只有
    「成長率」被 revenue_qa 整合測試間接練到,這兩條規則完全沒有專屬測試。"""
    earlier = Evidence(
        source_id="s-early",
        document_id="doc",
        document_title="t",
        source_type="text",
        period="2025Q2",
        exact_value="1100.0",
    )
    later = Evidence(
        source_id="s-late",
        document_id="doc",
        document_title="t",
        source_type="text",
        period="2025Q3",
        exact_value="1234.0",
    )
    sources = [
        SourceResult(
            source_id="s-early",
            document_id="doc",
            document_title="t",
            source_type="text",
            retrieval_method="vector",
            original_score=0.5,
        ),
        SourceResult(
            source_id="s-late",
            document_id="doc",
            document_title="t",
            source_type="text",
            retrieval_method="vector",
            original_score=0.5,
        ),
    ]
    locator = _FixedLocator({"s-early": earlier, "s-late": later})
    node = make_data_locator_node({"text": locator})
    base_state = {
        "ranked_sources": sources,
        "requires_calculation": True,
        "requires_multi_doc": True,
        "target_period": ["2025Q2", "2025Q3"],
        "canonical_metric": "稅後淨利",
    }

    diff_out = asyncio.run(
        node({**base_state, "normalized_query": "稅後淨利差異是多少"})
    )
    assert diff_out["calculation_trace"].formula == "a - b"
    assert diff_out["calculation_result"] == pytest.approx(134.0)

    ratio_out = asyncio.run(
        node({**base_state, "normalized_query": "稅後淨利比率是多少"})
    )
    assert ratio_out["calculation_trace"].formula == "a / b"
    assert ratio_out["calculation_result"] == pytest.approx(1234.0 / 1100.0)


def test_data_locator_single_value_lookup_without_calculation():
    """【全對偶：requires_calculation=False × requires_multi_doc=False × 單一期間字串】
    最普通的 SINGLE_VALUE_LOOKUP 路徑：只取分數最高的一筆證據，candidate_answer 由
    「指標 期間 為 值單位」組成，不得產生任何計算軌跡——先前 data_locator 只測過
    (True, True, list) 那一格。"""
    only = Evidence(
        source_id="s-late",
        document_id="doc",
        document_title="t",
        source_type="text",
        page_number=3,
        exact_excerpt="2025Q3 稅後淨利為 1234.0 百萬元",
        period="2025Q3",
        exact_value="1234.0",
        unit="百萬元",
    )
    source = SourceResult(
        source_id="s-late",
        document_id="doc",
        document_title="t",
        source_type="text",
        retrieval_method="vector",
        original_score=0.5,
    )
    node = make_data_locator_node({"text": _FixedLocator({"s-late": only})})
    out = asyncio.run(
        node(
            {
                "ranked_sources": [source],
                "requires_calculation": False,
                "requires_multi_doc": False,
                "target_period": "2025Q3",
                "canonical_metric": "稅後淨利",
                "normalized_query": "2025Q3 稅後淨利是多少",
            }
        )
    )
    assert out["selected_evidence"] == [only]
    assert out["page_evidence"] == [only]
    assert out["table_cell_evidence"] == []
    assert out["text_claims"] == ["2025Q3 稅後淨利為 1234.0 百萬元"]
    assert out["candidate_answer"] == "稅後淨利 2025Q3 為 1234.0百萬元"
    assert out["calculation_result"] is None
    assert out["calculation_trace"] is None


# ---------------------------------------------------------------------------
# Evidence Verification
# ---------------------------------------------------------------------------


def _text_evidence_2024q3() -> Evidence:
    """期間錯誤（2024Q3）的文字證據。"""
    return Evidence(
        source_id="fin-2024q3#c1",
        document_id="doc-fin-2024q3",
        document_title="2024Q3 財務季報",
        source_type="text",
        page_number=7,
        exact_excerpt="2024Q3 稅後淨利為 999 百萬元",
        exact_value="999",
        metric="稅後淨利",
        period="2024Q3",
        unit="百萬元",
    )


def test_verification_period_mismatch_retry():
    """【規格 11】期間不一致：RETRY + PERIOD_MISMATCH，verified_evidence 必須為空。"""
    node = make_evidence_verification_node()
    out = asyncio.run(
        node(
            {
                "selected_evidence": [_text_evidence_2024q3()],
                "target_period": "2025Q3",
                "canonical_metric": "稅後淨利",
                "metric_terms": ["稅後淨利"],
            }
        )
    )
    assert out["verification_result"] == VerificationResult.RETRY
    assert FailureCode.PERIOD_MISMATCH in out["failure_codes"]
    assert out["verified_evidence"] == []


def test_verification_table_cell_mismatch_not_pass():
    """【規格 12】表格列名與指標詞不符（稅前 vs 稅後）：不得 PASS + TABLE_CELL_MISMATCH。"""
    evidence = Evidence(
        source_id="fin-2025q3#t1",
        document_id="doc-fin-2025q3",
        document_title="2025Q3 財務季報",
        source_type="table",
        page_number=12,
        exact_value="1500.0",
        row_identifier="稅前淨利",  # 定位到錯誤的列
        column_identifier="2025Q3",
        metric="稅後淨利",
        period="2025Q3",
    )
    node = make_evidence_verification_node()
    out = asyncio.run(
        node(
            {
                "selected_evidence": [evidence],
                "target_period": "2025Q3",
                "canonical_metric": "稅後淨利",
                "metric_terms": ["稅後淨利"],
            }
        )
    )
    assert out["verification_result"] != VerificationResult.PASS
    assert FailureCode.TABLE_CELL_MISMATCH in out["failure_codes"]


def test_verification_passes_with_consistent_evidence():
    """【決策表1，正面覆蓋】期間/指標/數值皆一致、可追溯的乾淨證據必須 PASS——
    先前整個範圍內沒有任何節點級測試直接餵乾淨證據確認 PASS 分支，一個把
    PASS 改成永不回傳的迴歸不會被抓到。"""
    evidence = Evidence(
        source_id="fin-2025q3#c1",
        document_id="doc-fin-2025q3",
        document_title="2025Q3 財務季報",
        source_type="text",
        page_number=3,
        exact_excerpt="2025Q3 稅後淨利為 1234.0 百萬元",
        exact_value="1234.0",
        metric="稅後淨利",
        period="2025Q3",
        unit="百萬元",
        locator_score=0.9,
    )
    node = make_evidence_verification_node()
    out = asyncio.run(
        node(
            {
                "selected_evidence": [evidence],
                "target_period": "2025Q3",
                "canonical_metric": "稅後淨利",
                "metric_terms": ["稅後淨利"],
                "excluded_terms": [],
                "candidate_answer": "稅後淨利 2025Q3 為 1234.0 百萬元",
            }
        )
    )
    assert out["verification_result"] == VerificationResult.PASS
    assert out["verified_evidence"] == [evidence]
    assert out["failure_codes"] == []
    assert out["confidence"] > 0


def test_verification_hard_fail_codes():
    """【決策表1】CONFLICTING_EVIDENCE 與 CALCULATION_ERROR 屬 hard_fail 集合，
    必須產出 FAIL 而非 RETRY——先前這是 kb-query 閘門唯一會走到 FAIL 的入口，
    卻完全零覆蓋（composer 只用假 state 測「已知 FAIL」，沒人驗證誰真的會產出它）。"""
    node = make_evidence_verification_node()

    conflicting_a = Evidence(
        source_id="fin-2025q3#c1",
        document_id="doc-a",
        document_title="A",
        source_type="text",
        page_number=1,
        exact_excerpt="x",
        exact_value="1234.0",
        metric="稅後淨利",
        period="2025Q3",
    )
    conflicting_b = Evidence(
        source_id="fin-2025q3#c2",
        document_id="doc-b",
        document_title="B",
        source_type="text",
        page_number=2,
        exact_excerpt="y",
        exact_value="9999.0",
        metric="稅後淨利",
        period="2025Q3",
    )
    out_conflict = asyncio.run(
        node(
            {
                "selected_evidence": [conflicting_a, conflicting_b],
                "target_period": "2025Q3",
                "canonical_metric": "稅後淨利",
                "metric_terms": ["稅後淨利"],
            }
        )
    )
    assert out_conflict["verification_result"] == VerificationResult.FAIL
    assert FailureCode.CONFLICTING_EVIDENCE in out_conflict["failure_codes"]

    evidence = Evidence(
        source_id="fin-2025q3#c3",
        document_id="doc-c",
        document_title="C",
        source_type="text",
        page_number=1,
        exact_excerpt="z",
        exact_value="1234.0",
        metric="稅後淨利",
        period="2025Q3",
    )
    bad_trace = CalculationTrace(
        formula="a + b", inputs={"a": 1.0, "b": 2.0}, result=999.0
    )
    out_calc = asyncio.run(
        node(
            {
                "selected_evidence": [evidence],
                "target_period": "2025Q3",
                "canonical_metric": "稅後淨利",
                "metric_terms": ["稅後淨利"],
                "requires_calculation": True,
                "calculation_trace": bad_trace,
            }
        )
    )
    assert out_calc["verification_result"] == VerificationResult.FAIL
    assert FailureCode.CALCULATION_ERROR in out_calc["failure_codes"]


def test_verification_metric_mismatch_and_excluded_term_version_mismatch():
    """【決策表1】規則2 METRIC_MISMATCH（指標不符）與規則3 排除詞命中—口徑類
    VERSION_MISMATCH 各驗一次，先前兩者皆零覆蓋。"""
    node = make_evidence_verification_node()

    wrong_metric = Evidence(
        source_id="e1",
        document_id="doc-1",
        document_title="財報",
        source_type="text",
        page_number=1,
        exact_excerpt="x",
        metric="逾放比",
        period="2025Q3",
    )
    out_metric = asyncio.run(
        node(
            {
                "selected_evidence": [wrong_metric],
                "target_period": "2025Q3",
                "canonical_metric": "稅後淨利",
                "metric_terms": ["稅後淨利"],
            }
        )
    )
    assert out_metric["verification_result"] == VerificationResult.RETRY
    assert FailureCode.METRIC_MISMATCH in out_metric["failure_codes"]

    version_hit = Evidence(
        source_id="e2",
        document_id="doc-2",
        document_title="2025Q3 稅前淨利報告",
        source_type="text",
        page_number=1,
        exact_excerpt="x",
        metric="稅後淨利",
        period="2025Q3",
    )
    out_version = asyncio.run(
        node(
            {
                "selected_evidence": [version_hit],
                "target_period": "2025Q3",
                "canonical_metric": "稅後淨利",
                "metric_terms": ["稅後淨利"],
                "excluded_terms": ["稅前"],
            }
        )
    )
    assert out_version["verification_result"] == VerificationResult.RETRY
    assert FailureCode.VERSION_MISMATCH in out_version["failure_codes"]


def test_verification_source_not_traceable():
    """【決策表1】規則5 不可追溯：缺 document_id，或缺頁碼/摘錄/表格定位（非
    structured）時判 SOURCE_NOT_TRACEABLE，先前零覆蓋。"""
    node = make_evidence_verification_node()

    no_locator = Evidence(
        source_id="e1",
        document_id="doc-1",
        document_title="財報",
        source_type="text",
        metric="稅後淨利",
        period="2025Q3",
    )  # 無 page_number、無 exact_excerpt、非 structured
    out = asyncio.run(
        node(
            {
                "selected_evidence": [no_locator],
                "target_period": "2025Q3",
                "canonical_metric": "稅後淨利",
                "metric_terms": ["稅後淨利"],
            }
        )
    )
    assert out["verification_result"] == VerificationResult.RETRY
    assert FailureCode.SOURCE_NOT_TRACEABLE in out["failure_codes"]

    no_document_id = no_locator.model_copy(
        update={"document_id": "", "page_number": 1}
    )
    out2 = asyncio.run(
        node(
            {
                "selected_evidence": [no_document_id],
                "target_period": "2025Q3",
                "canonical_metric": "稅後淨利",
                "metric_terms": ["稅後淨利"],
            }
        )
    )
    assert FailureCode.SOURCE_NOT_TRACEABLE in out2["failure_codes"]


def test_verification_value_mismatch_when_candidate_number_has_no_source():
    """【決策表1】規則6 數值一致性：candidate_answer 講出的數字對不回任何證據值時判
    VALUE_MISMATCH（RETRY），先前所有測試的 candidate_answer 都與證據一致，這條零覆蓋。"""
    evidence = Evidence(
        source_id="fin-2025q3#c1",
        document_id="doc-fin-2025q3",
        document_title="2025Q3 財務季報",
        source_type="text",
        page_number=3,
        exact_excerpt="2025Q3 稅後淨利為 1234.0 百萬元",
        exact_value="1234.0",
        metric="稅後淨利",
        period="2025Q3",
        unit="百萬元",
    )
    node = make_evidence_verification_node()
    out = asyncio.run(
        node(
            {
                "selected_evidence": [evidence],
                "target_period": "2025Q3",
                "canonical_metric": "稅後淨利",
                "metric_terms": ["稅後淨利"],
                "candidate_answer": "稅後淨利 2025Q3 為 5678.0 百萬元",  # 憑空捏造
            }
        )
    )
    assert out["verification_result"] == VerificationResult.RETRY
    assert out["failure_codes"] == [FailureCode.VALUE_MISMATCH]
    assert out["verified_evidence"] == []


def test_verification_requires_calculation_without_trace_is_insufficient():
    """【決策表1】requires_calculation=True 但完全沒有 calculation_trace：判
    INSUFFICIENT_EVIDENCE（RETRY，非 hard_fail 的 CALCULATION_ERROR）——先前只測過
    「有 trace 但算錯」，缺 trace 這個守衛零覆蓋。"""
    evidence = Evidence(
        source_id="fin-2025q3#c1",
        document_id="doc-fin-2025q3",
        document_title="2025Q3 財務季報",
        source_type="text",
        page_number=3,
        exact_excerpt="2025Q3 稅後淨利為 1234.0 百萬元",
        exact_value="1234.0",
        metric="稅後淨利",
        period="2025Q3",
    )
    node = make_evidence_verification_node()
    out = asyncio.run(
        node(
            {
                "selected_evidence": [evidence],
                "target_period": "2025Q3",
                "canonical_metric": "稅後淨利",
                "metric_terms": ["稅後淨利"],
                "requires_calculation": True,  # 無 calculation_trace
            }
        )
    )
    assert out["verification_result"] == VerificationResult.RETRY
    assert out["failure_codes"] == [FailureCode.INSUFFICIENT_EVIDENCE]
    assert out["verified_evidence"] == []


def test_verification_empty_evidence_is_insufficient():
    """【決策表1】邊界：selected_evidence 為零筆時，光是這個守衛就必須產出
    INSUFFICIENT_EVIDENCE，不得因為「沒有任何一筆證據違規」而落入 PASS 分支。"""
    node = make_evidence_verification_node()
    out = asyncio.run(
        node(
            {
                "selected_evidence": [],
                "target_period": "2025Q3",
                "canonical_metric": "稅後淨利",
                "metric_terms": ["稅後淨利"],
            }
        )
    )
    assert out["verification_result"] == VerificationResult.RETRY
    assert out["failure_codes"] == [FailureCode.INSUFFICIENT_EVIDENCE]
    assert out["confidence"] == 0.0
    assert out["verified_evidence"] == []


# ---------------------------------------------------------------------------
# Structured LLM Adapter：吞下非 schema 相容的回應
# ---------------------------------------------------------------------------


def test_langchain_structured_llm_swallows_malformed_response():
    """【決策表：nl_extract/nl_logic BVT 缺口收斂點】LangChainStructuredLLM.structured()
    對「LLM 回傳非 schema 相容內容」（非 JSON／缺欄位／多餘欄位都收斂成同一個
    Exception 分支）必須吞下例外回 None，不能讓 LLM 失敗中斷流程——先前全部
    測試都用手寫 Fake 繞過這顆真實 adapter，這是整個範圍內唯一直接測到它的案例。"""

    class _BrokenStructuredClient:
        async def ainvoke(self, messages):
            raise ValueError("malformed llm response")

    class _BrokenChatClient:
        def with_structured_output(self, schema):
            return _BrokenStructuredClient()

    llm = LangChainStructuredLLM()
    llm._client = _BrokenChatClient()  # 繞過 get_llm()，直接注入壞掉的底層 client
    result = asyncio.run(llm.structured("sys", "user", IntentOutput))
    assert result is None


# ---------------------------------------------------------------------------
# Answer Composer
# ---------------------------------------------------------------------------


@pytest.mark.parametrize(
    "verification_result",
    [VerificationResult.RETRY, VerificationResult.FAIL, None],
    ids=["retry", "fail", "absent"],
)
def test_composer_abstains_when_not_pass(verification_result):
    """【規格 13】驗證未通過（RETRY / FAIL / 缺省）：一律 ABSTAIN，不輸出實質答案。"""
    state: dict = {"failure_codes": [FailureCode.PERIOD_MISMATCH]}
    if verification_result is not None:
        state["verification_result"] = verification_result
    out = asyncio.run(make_answer_composer_node()(state))
    assert out["answer_mode"] == AnswerMode.ABSTAIN
    assert out["final_answer"].startswith("【無法提供答案】")
    assert out["source_citations"] == []


def test_composer_pass_includes_citation():
    """【規格 14】驗證通過：必附可定位到頁碼與儲存格的 citation。"""
    evidence = Evidence(
        source_id="fin-2025q3#t1",
        document_id="doc-fin-2025q3",
        document_title="2025Q3 財務季報",
        document_version="v1.0",
        source_type="table",
        page_number=12,
        exact_value="1234.0",
        table_name="損益表",
        sheet_name="IS",
        row_identifier="稅後淨利",
        column_identifier="2025Q3",
        metric="稅後淨利",
        period="2025Q3",
        unit="百萬元",
    )
    out = asyncio.run(
        make_answer_composer_node()(
            {
                "verification_result": VerificationResult.PASS,
                "verified_evidence": [evidence],
                "candidate_answer": "稅後淨利 2025Q3 為 1234.0百萬元",
            }
        )
    )
    assert out["answer_mode"] == AnswerMode.ANSWER
    assert len(out["source_citations"]) == 1
    citation = out["source_citations"][0]
    assert citation.page == 12
    assert citation.row == "稅後淨利"
    assert citation.column == "2025Q3"
    assert "【引用】" in out["final_answer"]


def test_verification_fatal_error_overrides_pass_in_composer():
    """【決策表2】answer_composer.py 的短路優先序：即使 verification_result=PASS
    且證據齊全，只要 fatal_error 已設定就仍必須 ABSTAIN——先前沒有任何測試驗證
    這個優先序，一個把判斷順序顛倒的迴歸不會被抓到。"""
    evidence = Evidence(
        source_id="fin-2025q3#t1",
        document_id="doc-fin-2025q3",
        document_title="2025Q3 財務季報",
        source_type="table",
        page_number=12,
        exact_value="1234.0",
        row_identifier="稅後淨利",
        column_identifier="2025Q3",
        metric="稅後淨利",
        period="2025Q3",
        unit="百萬元",
    )
    out = asyncio.run(
        make_answer_composer_node()(
            {
                "fatal_error": "data_locator: boom",
                "verification_result": VerificationResult.PASS,
                "verified_evidence": [evidence],
                "candidate_answer": "稅後淨利 2025Q3 為 1234.0百萬元",
            }
        )
    )
    assert out["answer_mode"] == AnswerMode.ABSTAIN
    assert out["final_answer"].startswith("【無法提供答案】")
    assert out["source_citations"] == []


# ---------------------------------------------------------------------------
# Calculator：確定性計算
# ---------------------------------------------------------------------------


def test_calculator_deterministic():
    """【規格 15】同公式同輸入兩次求值完全相等，結果 ≈ 12.1818…。"""
    formula, inputs = "(a - b) / b * 100", {"a": 1234.0, "b": 1100.0}
    r1 = calculator.evaluate(formula, inputs)
    r2 = calculator.evaluate(formula, inputs)
    assert r1 == r2
    assert r1 == pytest.approx(12.181818, rel=1e-6)


def test_calculator_rejects_unsupported_node():
    """【規格 15】白名單外的節點（函式呼叫）一律 ValueError，不得執行任意程式。"""
    with pytest.raises(ValueError):
        calculator.evaluate("__import__('os')", {})


def test_calculator_whitelist_add_pow_usub_and_invalid_operands():
    """【規格 15】白名單其餘分支：Add / Pow / 一元負號可求值；未提供的變數與非數值
    常數（字串）一律 ValueError，不得靜默當 0 或當成可用值——先前只練到 Sub/Mult/Div
    與函式呼叫這一種拒絕樣本。"""
    assert calculator.evaluate("a + b ** 2", {"a": 1.0, "b": 3.0}) == pytest.approx(10.0)
    assert calculator.evaluate("-a", {"a": 2.0}) == pytest.approx(-2.0)
    with pytest.raises(ValueError):
        calculator.evaluate("x", {})  # 未提供的變數
    with pytest.raises(ValueError):
        calculator.evaluate("'s'", {})  # 非數值常數


def test_calculator_zero_division_propagates():
    """【規格 15】除以零自然拋出 ZeroDivisionError，由節點層轉為 CALCULATION_ERROR。"""
    with pytest.raises(ZeroDivisionError):
        calculator.evaluate("a / b", {"a": 1.0, "b": 0.0})


# ---------------------------------------------------------------------------
# Audit：不得洩漏模型私有推理
# ---------------------------------------------------------------------------


def test_audit_trail_excludes_private_thinking():
    """【規格 16】模型內部推理絕不出現在 State 或 Audit Trail；rewrite_reason ≤ 200 字。"""
    sentinel = "SECRET_CHAIN_OF_THOUGHT"
    q = "2025Q3 稅後淨利是多少？"
    llm = FakeStructuredLLM(
        outputs={
            QueryRewriteOutput: QueryRewriteOutput(
                normalized_query="2025Q3 稅後淨利是多少",
                query_variants=["2025Q3 稅後淨利"],
                rewrite_reason="同義改寫",
            )
        },
        private_thinking=sentinel,
    )
    deps = make_deps({"vector": FakeSearch(lambda q, f: [TEXT_2025Q3])}, llm=llm)
    result = run_graph(deps, q)

    assert len(deps.audit_repo.saved) == 1
    assert sentinel not in deps.audit_repo.saved[0].model_dump_json()
    assert sentinel not in repr(result)  # State 也不得夾帶
    assert len(result["rewrite_reason"]) <= 200
