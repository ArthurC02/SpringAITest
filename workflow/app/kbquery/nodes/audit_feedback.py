"""Audit Feedback：組完整稽核紀錄並落地；失敗時產出議題標籤、回歸測項與改進清單。"""

from app.engine.node_registry import node
from app.kbquery.models import (
    AnswerMode,
    AuditTrail,
    FailureCode,
    IssueLabel,
    RegressionTestItem,
)
from app.kbquery.ports import AuditRepositoryPort

# fatal_error 開頭的節點名 → 議題標籤
_FATAL_NODE_ISSUE: dict[str, IssueLabel] = {
    "query_intake": IssueLabel.QUERY_REWRITE_ERROR,
    "query_rewrite": IssueLabel.QUERY_REWRITE_ERROR,
    "intent_classification": IssueLabel.INTENT_CLASSIFICATION_ERROR,
    "context_resolver": IssueLabel.CONTEXT_RESOLUTION_ERROR,
    "retrieval_planner": IssueLabel.RETRIEVAL_MISS,
    "source_retrieval_rerank": IssueLabel.RETRIEVAL_MISS,
    "data_locator": IssueLabel.DATA_LOCATION_ERROR,
    "evidence_verification": IssueLabel.EVIDENCE_VERIFICATION_ERROR,
    "answer_composer": IssueLabel.ANSWER_FORMAT_ERROR,
}

# ABSTAIN 時的 failure code → 議題標籤
_FAILURE_CODE_ISSUE: dict[FailureCode, IssueLabel] = {
    FailureCode.INSUFFICIENT_EVIDENCE: IssueLabel.RETRIEVAL_MISS,
    FailureCode.PERIOD_MISMATCH: IssueLabel.RETRIEVAL_MISS,
    FailureCode.METRIC_MISMATCH: IssueLabel.RETRIEVAL_MISS,
    FailureCode.VERSION_MISMATCH: IssueLabel.SOURCE_VERSION_ERROR,
    FailureCode.TABLE_CELL_MISMATCH: IssueLabel.DATA_LOCATION_ERROR,
    FailureCode.VALUE_MISMATCH: IssueLabel.DATA_LOCATION_ERROR,
    FailureCode.CALCULATION_ERROR: IssueLabel.CALCULATION_ERROR,
    FailureCode.CONFLICTING_EVIDENCE: IssueLabel.EVIDENCE_VERIFICATION_ERROR,
    FailureCode.SOURCE_NOT_TRACEABLE: IssueLabel.EVIDENCE_VERIFICATION_ERROR,
}

_RESOLVED_CONTEXT_KEYS = (
    "target_period",
    "version_policy",
    "canonical_metric",
    "excluded_terms",
    "unresolved_context",
    "context_warnings",
)


@node(
    name="audit_feedback",
    version="1.0",
    description="組完整稽核紀錄並落地；失敗時產出議題標籤、回歸測項與改進清單",
    reads=[
        "query",
        "query_id",
        "query_timestamp",
        "original_query",
        "normalized_query",
        "query_variants",
        "intent_type",
        "target_period",
        "version_policy",
        "canonical_metric",
        "excluded_terms",
        "unresolved_context",
        "context_warnings",
        "retrieval_plans",
        "retrieval_attempt",
        "ranked_sources",
        "selected_evidence",
        "verification_result",
        "failure_codes",
        "failure_reason",
        "confidence",
        "answer_mode",
        "final_answer",
        "source_citations",
        "calculation_trace",
        "trace",
        "errors",
        "fatal_error",
        "user_feedback",
    ],
    writes=[
        "audit_trail",
        "issue_label",
        "regression_test_item",
        "improvement_backlog",
    ],
    deps=["audit_repo"],
    requires_tools=[],
    run_on_fatal=True,  # 稽核是治理硬規則：fatal 後也必須落地
)
def make_audit_feedback_node(repo: AuditRepositoryPort):
    """建立 audit_feedback 節點：成功、失敗、abstain 都會走到這裡。"""

    async def audit_feedback_node(state: dict) -> dict:
        original_query = state.get("original_query", state.get("query", ""))
        trail = AuditTrail(
            query_id=state.get("query_id", ""),
            query_timestamp=state.get("query_timestamp", ""),
            original_query=original_query,
            normalized_query=state.get("normalized_query", ""),
            query_variants=state.get("query_variants", []),
            intent_type=state.get("intent_type"),
            resolved_context={
                k: state[k] for k in _RESOLVED_CONTEXT_KEYS if k in state
            },
            retrieval_plans=state.get("retrieval_plans", []),
            ranked_sources=state.get("ranked_sources", []),
            selected_evidence=state.get("selected_evidence", []),
            verification_result=state.get("verification_result"),
            failure_codes=state.get("failure_codes", []),
            confidence=state.get("confidence"),
            answer_mode=state.get("answer_mode"),
            final_answer=state.get("final_answer", ""),
            source_citations=state.get("source_citations", []),
            calculation_trace=state.get("calculation_trace"),
            retry_count=max(0, state.get("retrieval_attempt", 0) - 1),
            node_trace=state.get("trace", []),
            errors=state.get("errors", []),
            user_feedback=state.get("user_feedback") or "",
        )

        fatal = state.get("fatal_error", "")
        issue: IssueLabel | None = None
        if fatal:
            issue = _FATAL_NODE_ISSUE.get(fatal.split(":", 1)[0].strip())
        elif state.get("answer_mode") == AnswerMode.ABSTAIN:
            issue = next(
                (
                    _FAILURE_CODE_ISSUE[c]
                    for c in state.get("failure_codes") or []
                    if c in _FAILURE_CODE_ISSUE
                ),
                None,
            )

        regression: RegressionTestItem | None = None
        backlog: list[str] = []
        if issue is not None:
            plans = state.get("retrieval_plans") or []
            regression = RegressionTestItem(
                question=original_query,
                expected_intent=state.get("intent_type"),
                expected_period=str(state.get("target_period", "")),
                expected_metric=state.get("canonical_metric", ""),
                expected_sources=list(plans[-1].methods) if plans else [],
                expected_evidence_location="需可定位到文件頁碼或表格儲存格",
                expected_answer_rule="驗證通過才可輸出，否則 ABSTAIN",
                original_issue=issue,
            )
            backlog = [f"{issue.value}: {state.get('failure_reason') or fatal}"] + [
                f"未解析情境: {x}" for x in state.get("unresolved_context") or []
            ]

        await repo.save(trail)
        return {
            "audit_trail": trail,
            "issue_label": issue,
            "regression_test_item": regression,
            "improvement_backlog": backlog,
        }

    return audit_feedback_node
