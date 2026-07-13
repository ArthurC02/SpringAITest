"""Context Resolver：解析期間、口徑版本與 canonical 指標；資料不足只進 unresolved，不猜測。"""

from app.kbquery import textutils
from app.kbquery.models import IntentType
from app.kbquery.ports import GlossaryPort

# 口徑詞 → (dimension, value)；evidence_verification 也會 import 這兩個常數
VERSION_TERMS: dict[str, tuple[str, str]] = {
    "稅前": ("tax", "PRE_TAX"),
    "稅後": ("tax", "POST_TAX"),
    "提存前": ("provision", "PRE_PROVISION"),
    "提存後": ("provision", "POST_PROVISION"),
    "績效帳": ("book", "PERFORMANCE"),
    "會計帳": ("book", "ACCOUNTING"),
}
OPPOSITE_TERMS: dict[str, str] = {
    "稅前": "稅後",
    "稅後": "稅前",
    "提存前": "提存後",
    "提存後": "提存前",
    "績效帳": "會計帳",
    "會計帳": "績效帳",
}

# 需要明確期間與指標才能回答的意圖
_NEEDS_PERIOD_AND_METRIC = {
    IntentType.SINGLE_VALUE_LOOKUP,
    IntentType.TABLE_LOOKUP,
    IntentType.CALCULATION,
    IntentType.COMPARISON,
}


def make_context_resolver_node(glossary: GlossaryPort):
    """建立 context_resolver 節點：全部確定性解析，不呼叫 LLM。"""

    async def context_resolver_node(state: dict) -> dict:
        q = state.get("normalized_query", "")

        periods = textutils.find_periods(q)
        target_period: str | list[str]
        if not periods:
            target_period = ""
        elif len(periods) == 1:
            target_period = periods[0]
        else:
            target_period = list(periods)

        unresolved: list[str] = []
        warnings: list[str] = []
        if textutils.has_bare_quarter(q) and not periods:
            unresolved.append("target_period")
            warnings.append("期間僅含季別未含年度，不做猜測")

        # 口徑版本：長 term 先比（避免重疊誤中），同一 dimension 只取一個
        version_policy: dict[str, str] = {}
        excluded: list[str] = []
        for term in sorted(VERSION_TERMS, key=len, reverse=True):
            dimension, value = VERSION_TERMS[term]
            if dimension in version_policy:
                continue
            if term in q:
                version_policy[dimension] = value
                excluded.append(OPPOSITE_TERMS[term])

        canonical = glossary.canonical(q)
        if canonical:
            canonical_metric = canonical
            metric_terms = list(
                dict.fromkeys([canonical, *glossary.synonyms(canonical)])
            )
            excluded += glossary.confusables(canonical)
        else:
            canonical_metric = ""
            metric_terms = []

        if state.get("intent_type") in _NEEDS_PERIOD_AND_METRIC:
            if not periods and "target_period" not in unresolved:
                unresolved.append("target_period")
                warnings.append("問題未指明期間，無法鎖定目標期間")
            if not canonical_metric:
                unresolved.append("canonical_metric")
                warnings.append("問題未對應到已知的 canonical 指標")

        return {
            "target_period": target_period,
            "version_policy": version_policy,
            "canonical_metric": canonical_metric,
            "metric_terms": metric_terms,
            "excluded_terms": list(dict.fromkeys(excluded)),
            "unresolved_context": unresolved,
            "context_warnings": warnings,
        }

    return context_resolver_node
