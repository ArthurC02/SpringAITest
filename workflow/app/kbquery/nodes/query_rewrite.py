"""Query Rewrite：LLM 改寫問題並產生變體；所有輸出都經確定性語意防護才寫入 State。"""

from app.engine.node_registry import node
from app.kbquery import textutils
from app.kbquery.models import QueryRewriteOutput
from app.kbquery.ports import GlossaryPort, StructuredLLMPort

_SYSTEM_PROMPT = (
    "你是查詢改寫助手。請將使用者的問題正規化，並產生 2~5 個語意相同的查詢變體，"
    "涵蓋：業務正式用語、文件常見用語、同義詞、完整句、關鍵字版本。"
    "嚴格要求：保持原問題語意；不得新增原問題未提及的期間（年度、季度、月份）或指標；"
    "rewrite_reason 為 40 字內的改寫摘要。"
)

_FALLBACK_REASON = "改寫服務不可用，退回原始問題"


@node(
    name="query_rewrite",
    version="1.0",
    description="LLM 改寫問題並產生變體；輸出經確定性語意防護才寫入 State",
    reads=["original_query"],
    writes=["normalized_query", "query_variants", "rewrite_reason"],
    deps=["llm", "glossary"],
    requires_tools=[],
)
def make_query_rewrite_node(llm: StructuredLLMPort | None, glossary: GlossaryPort):
    """建立 query_rewrite 節點：LLM 不可用或輸出未通過防護時，退回原始問題。"""

    def _periods(text: str) -> set[str]:
        return {textutils.normalize_period(p) for p in textutils.find_periods(text)}

    async def query_rewrite_node(state: dict) -> dict:
        original = state["original_query"]
        orig_periods = _periods(original)
        orig_canonical = glossary.canonical(original)

        normalized = original
        variants: list[str] = []
        reason = _FALLBACK_REASON

        out = None
        if llm is not None:
            out = await llm.structured(
                system=_SYSTEM_PROMPT, user=original, schema=QueryRewriteOutput
            )
        if isinstance(out, QueryRewriteOutput):
            reason = out.rewrite_reason
            # 語意防護（不信任 LLM）：引入原問題沒有的期間 → normalized 退回原文
            if _periods(out.normalized_query) <= orig_periods:
                normalized = out.normalized_query
            for v in out.query_variants:
                # 引入新期間 → 丟棄該 variant
                if not _periods(v) <= orig_periods:
                    continue
                # canonical 指標被改掉 → 丟棄該 variant
                vc = glossary.canonical(v)
                if (
                    orig_canonical is not None
                    and vc is not None
                    and vc != orig_canonical
                ):
                    continue
                variants.append(v)

        # 確定性補充 variant：原文用同義詞時，附加一個換成 canonical 用語的版本
        if orig_canonical and orig_canonical not in original:
            for syn in glossary.synonyms(orig_canonical):
                if syn and syn != orig_canonical and syn in original:
                    variants.append(original.replace(syn, orig_canonical))
                    break

        # 必含 original、去重保序、上限 5
        merged = list(dict.fromkeys([original, *variants]))[:5]
        return {
            "normalized_query": normalized,
            "query_variants": merged,
            "rewrite_reason": reason,
        }

    return query_rewrite_node
