"""kb_query 工作流的 LangGraph State。

- trace / errors / retrieval_plans 用 operator.add reducer 累加，其餘欄位覆寫。
- original_query / query_id / query_timestamp 由 Query Intake 建立後不可覆寫，
  由 runtime.traced() 強制剝除後續節點對這些鍵的寫入（見 runtime.py）。
- fatal_error：任一節點發生不可恢復錯誤時設定；之後的節點（除 Answer Composer
  與 Audit Feedback）一律短路跳過，最終走安全 ABSTAIN + 稽核路徑。
"""

import operator
from typing import Annotated, Any, TypedDict

from app.kbquery.models import (
    AnswerMode,
    AuditTrail,
    CalculationTrace,
    Citation,
    Evidence,
    FailureCode,
    IntentType,
    IssueLabel,
    RegressionTestItem,
    RetrievalPlan,
    SourceResult,
    TraceEntry,
    VerificationResult,
)


class KbQueryState(TypedDict, total=False):
    # --- 呼叫端輸入（app.main 會自動注入 tenant_id）---
    query: str
    tenant_id: str
    user_feedback: str

    # --- Query Intake ---
    query_id: str
    original_query: str
    session_context: dict[str, Any]
    user_role: str
    system_entrypoint: str
    query_timestamp: str

    # --- Query Rewrite ---
    normalized_query: str
    query_variants: list[str]
    rewrite_reason: str

    # --- Intent Classification ---
    intent_type: IntentType
    question_type: str
    requires_table: bool
    requires_calculation: bool
    requires_multi_doc: bool

    # --- Context Resolution ---
    target_period: str | list[str]
    version_policy: dict[str, str]
    canonical_metric: str
    metric_terms: list[str]  # canonical + 同義詞，供 Locator 對表格列名比對
    excluded_terms: list[str]
    unresolved_context: list[str]
    context_warnings: list[str]

    # --- Retrieval ---
    retrieval_plan: RetrievalPlan
    retrieval_plans: Annotated[list[RetrievalPlan], operator.add]  # 各次計畫，稽核用
    filters: dict[str, Any]
    source_priority: list[str]
    top_k: int
    rerank_policy: str
    retrieval_attempt: int
    max_retrieval_attempts: int

    # --- Retrieval Result ---
    ranked_sources: list[SourceResult]
    candidate_documents: list[dict[str, Any]]
    candidate_pages: list[dict[str, Any]]

    # --- Evidence ---
    selected_evidence: list[Evidence]
    page_evidence: list[Evidence]
    table_cell_evidence: list[Evidence]
    text_claims: list[str]
    candidate_answer: str
    calculation_result: float | None
    calculation_trace: CalculationTrace | None

    # --- Verification ---
    verification_result: VerificationResult
    confidence: float
    failure_reason: str
    failure_codes: list[FailureCode]
    verified_evidence: list[Evidence]

    # --- Answer ---
    answer_format_policy: str
    answer_mode: AnswerMode
    final_answer: str
    source_citations: list[Citation]
    assumption_note: str

    # --- Audit / 錯誤處理 ---
    fatal_error: str
    trace: Annotated[list[TraceEntry], operator.add]
    errors: Annotated[list[dict[str, Any]], operator.add]
    audit_trail: AuditTrail
    issue_label: IssueLabel | None
    regression_test_item: RegressionTestItem | None
    improvement_backlog: list[str]
