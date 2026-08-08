"""doc_insights / report_synthesize 節點：app/skills/analyze-report.yaml 的兩個步驟
（該 skill 為 required_role: ADMIN）。
"""

from pydantic import BaseModel

from app.engine.node_registry import node
from app.nodes._llm_input import format_docs_context, structured_field

# 沒有任何文件可分析時的固定文案（不呼叫 LLM）。
_NO_DATA_INSIGHTS = "（無資料）"


class _DocInsightsOutput(BaseModel):
    insights: str


class _ReportSynthesizeOutput(BaseModel):
    report: str


@node(
    name="doc_insights",
    version="1.0",
    description="從檢索到的文件片段中萃取與主題相關的要點；docs 為空時不呼叫 LLM，回固定文案",
    reads=["topic", "docs"],
    writes=["insights"],
    deps=["llm"],
    requires_tools=[],
)
def make_doc_insights_node(llm):
    """建立 doc_insights 節點函式（對齊 workflows/analyze_report.py::analyze 的語意）。"""

    async def doc_insights(state: dict) -> dict:
        docs = state.get("docs") or []
        if not docs:
            return {"insights": _NO_DATA_INSIGHTS}

        context = format_docs_context(docs)
        insights = await structured_field(
            llm,
            system=(
                "你是資料分析助手，請從下方提供的租戶文件內容中，"
                "萃取與指定主題相關的重點，以條列式繁體中文呈現。"
            ),
            user=f"主題：{state['topic']}\n\n文件內容：\n{context}",
            schema=_DocInsightsOutput,
            field="insights",
        )
        return {"insights": insights}

    return doc_insights


@node(
    name="report_synthesize",
    version="1.0",
    description="依萃取出的要點，撰寫結構化的主題分析報告",
    reads=["topic", "insights"],
    writes=["report"],
    deps=["llm"],
    requires_tools=[],
)
def make_report_synthesize_node(llm):
    """建立 report_synthesize 節點函式（對齊 workflows/analyze_report.py::synthesize 的語意）。"""

    async def report_synthesize(state: dict) -> dict:
        report = await structured_field(
            llm,
            system=(
                "你是資料分析助手，請依提供的要點撰寫結構化報告，"
                "包含摘要、關鍵發現、建議三個段落，全程使用繁體中文。"
            ),
            user=f"主題：{state['topic']}\n\n要點：\n{state['insights']}",
            schema=_ReportSynthesizeOutput,
            field="report",
        )
        return {"report": report}

    return report_synthesize
