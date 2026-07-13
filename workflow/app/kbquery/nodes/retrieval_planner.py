"""Retrieval Planner：依意圖排定檢索方法；重試時依 failure_codes 做針對性調整。"""

from app.kbquery.models import FailureCode, IntentType, RetrievalPlan

# 意圖 → 檢索方法基本盤
_INTENT_METHODS: dict[IntentType, list[str]] = {
    IntentType.TABLE_LOOKUP: ["table", "vector"],
    IntentType.SINGLE_VALUE_LOOKUP: ["vector", "keyword"],
    IntentType.CALCULATION: ["table", "structured", "vector"],
    IntentType.COMPARISON: ["vector", "metadata"],
    IntentType.DOCUMENT_LOCATION: ["metadata", "keyword"],
    IntentType.CAUSE_ANALYSIS: ["vector", "keyword"],
    IntentType.PERFORMANCE_ANALYSIS: ["vector", "structured"],
    IntentType.UNKNOWN: ["vector", "keyword"],
}


def make_retrieval_planner_node(default_top_k: int):
    """建立 retrieval_planner 節點：只產出計畫，不執行檢索。"""

    async def retrieval_planner_node(state: dict) -> dict:
        attempt = state.get("retrieval_attempt", 0) + 1
        intent = state.get("intent_type", IntentType.UNKNOWN)
        methods = list(_INTENT_METHODS.get(intent, _INTENT_METHODS[IntentType.UNKNOWN]))
        top_k = default_top_k
        if state.get("requires_multi_doc"):
            top_k *= 2

        filters: dict = {}
        for key, value in (
            ("period", state.get("target_period")),
            ("metric", state.get("canonical_metric")),
            ("exclude_terms", state.get("excluded_terms")),
            ("version_policy", state.get("version_policy")),
        ):
            if value:
                filters[key] = value

        # 重試調整：依上一輪 failure_codes 針對性換打法，不可只加大 top_k
        adjustments: list[str] = []
        if attempt > 1:
            codes = set(state.get("failure_codes") or [])

            def hit(*targets: FailureCode) -> list[FailureCode]:
                return [c for c in targets if c in codes]

            matched = hit(FailureCode.PERIOD_MISMATCH, FailureCode.VERSION_MISMATCH)
            if matched:
                methods.append("metadata")
                filters["strict_period"] = True
                adjustments += [
                    f"{c.value}→加 metadata 檢索並設 strict_period" for c in matched
                ]

            matched = hit(FailureCode.METRIC_MISMATCH)
            if matched:
                methods.append("keyword")
                adjustments += [f"{c.value}→加 keyword 檢索" for c in matched]

            matched = hit(
                FailureCode.INSUFFICIENT_EVIDENCE, FailureCode.SOURCE_NOT_TRACEABLE
            )
            if matched:
                top_k *= 2
                methods.append("keyword")
                adjustments += [
                    f"{c.value}→top_k 加倍並加 keyword 檢索" for c in matched
                ]

            matched = hit(FailureCode.TABLE_CELL_MISMATCH, FailureCode.VALUE_MISMATCH)
            if matched:
                methods = ["table", "structured", *methods]
                adjustments += [
                    f"{c.value}→前插 table/structured 檢索" for c in matched
                ]

        methods = list(dict.fromkeys(methods))
        plan = RetrievalPlan(
            methods=methods,
            source_priority=list(methods),
            use_keyword="keyword" in methods,
            use_vector="vector" in methods,
            use_metadata="metadata" in methods,
            use_table="table" in methods,
            use_structured="structured" in methods,
            top_k=top_k,
            rerank_policy="score_with_context_boost",
            adjustment_reason="; ".join(adjustments),
        )
        return {
            "retrieval_plan": plan,
            "retrieval_plans": [plan],  # reducer 累加，稽核用
            "retrieval_attempt": attempt,
            "filters": filters,
            "source_priority": list(methods),
            "top_k": top_k,
            "rerank_policy": plan.rerank_policy,
        }

    return retrieval_planner_node
