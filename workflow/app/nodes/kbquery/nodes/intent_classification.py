"""Intent Classification：確定性關鍵詞規則優先，UNKNOWN 才問 LLM 且信心不足不硬猜。"""

from app.engine.node_registry import node
from app.nodes.kbquery.models import IntentOutput, IntentType
from app.nodes.kbquery.ports import StructuredLLMPort

# 規則依序比對，先中先贏
_RULES: list[tuple[IntentType, str, tuple[str, ...]]] = [
    (
        IntentType.COMPARISON,
        "comparison",
        ("比較", "相比", "差異", "高於", "低於", "對比", " vs "),
    ),
    (
        IntentType.CALCULATION,
        "calculation",
        (
            "成長率",
            "增長率",
            "計算",
            "合計",
            "平均",
            "比率",
            "YoY",
            "QoQ",
            "年增",
            "季增",
        ),
    ),
    (IntentType.CAUSE_ANALYSIS, "cause", ("為什麼", "為何", "原因", "導致")),
    (
        IntentType.DOCUMENT_LOCATION,
        "location",
        ("哪一份文件", "哪份文件", "出自哪", "在哪份", "哪一頁", "文件位置"),
    ),
    (IntentType.PERFORMANCE_ANALYSIS, "performance", ("績效", "達成率", "考核", "KPI")),
    (
        IntentType.TABLE_LOOKUP,
        "table",
        ("表格", "表中", "明細表", "欄位", "損益表", "資產負債表"),
    ),
    (IntentType.SINGLE_VALUE_LOOKUP, "factoid", ("多少", "金額", "餘額", "幾")),
]

_LABELS: dict[IntentType, str] = {intent: label for intent, label, _ in _RULES} | {
    IntentType.UNKNOWN: "unknown"
}

_SYSTEM_PROMPT = (
    "你是問題意圖分類器。請將使用者問題分類為以下其中一種 intent_type："
    "SINGLE_VALUE_LOOKUP、TABLE_LOOKUP、COMPARISON、CALCULATION、CAUSE_ANALYSIS、"
    "DOCUMENT_LOCATION、PERFORMANCE_ANALYSIS、UNKNOWN，"
    "並給出 0~1 的 confidence 與簡短的 question_type 標籤。無法判斷時回 UNKNOWN。"
)


def classify_by_rules(text: str) -> tuple[IntentType, str]:
    """確定性關鍵詞分類；獨立成 module 級函式方便單元測試。"""
    for intent, label, keywords in _RULES:
        if any(k in text for k in keywords):
            return intent, label
    return IntentType.UNKNOWN, "unknown"


@node(
    name="intent_classification",
    version="1.0",
    description="問題意圖分類：確定性關鍵詞規則優先，UNKNOWN 才問 LLM",
    reads=["normalized_query"],
    writes=[
        "intent_type",
        "question_type",
        "requires_table",
        "requires_calculation",
        "requires_multi_doc",
    ],
    # confidence 門檻由注入的依賴帶入（P4c 促升為 per-config 可覆寫）：deps 依序取
    # llm、intent_confidence_threshold 當位置參數，由 KbQueryDeps 的同名欄位提供。
    deps=["llm", "intent_confidence_threshold"],
    requires_tools=[],
)
def make_intent_classification_node(
    llm: StructuredLLMPort | None, confidence_threshold: float = 0.6
):
    """建立 intent_classification 節點：規則先行，LLM 僅補位且 confidence >= 門檻才採用。

    confidence_threshold 預設 0.6（歷史寫死值），可由租戶 Configuration Set 覆寫。
    """

    async def intent_classification_node(state: dict) -> dict:
        q = state.get("normalized_query", "")
        intent, question_type = classify_by_rules(q)

        if intent == IntentType.UNKNOWN and llm is not None:
            out = await llm.structured(
                system=_SYSTEM_PROMPT, user=q, schema=IntentOutput
            )
            if isinstance(out, IntentOutput) and out.confidence >= confidence_threshold:
                intent = out.intent_type
                question_type = out.question_type or _LABELS.get(intent, "unknown")

        return {
            "intent_type": intent,
            "question_type": question_type,
            "requires_table": intent == IntentType.TABLE_LOOKUP,
            "requires_calculation": intent == IntentType.CALCULATION,
            "requires_multi_doc": intent == IntentType.COMPARISON,
        }

    return intent_classification_node
