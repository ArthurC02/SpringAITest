"""doc_insights / report_synthesize 節點：語意對齊已退役的手寫 analyze_report 工作流
（Node-First 遷移 Phase 1 移植、Phase 3a 隨 app/workflows/ 一併退役）。
"""

from pydantic import BaseModel

from app.engine.node_registry import node

# 手寫圖 analyze_report.py 的固定文案，逐字保留。
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

        context = "\n\n".join(
            f"[{i + 1}] {doc['title']}：{doc['content']}" for i, doc in enumerate(docs)
        )
        out = await llm.structured(
            system=(
                "你是資料分析助手，請從下方提供的租戶文件內容中，"
                "萃取與指定主題相關的重點，以條列式繁體中文呈現。"
            ),
            user=f"主題：{state['topic']}\n\n文件內容：\n{context}",
            schema=_DocInsightsOutput,
        )
        return {"insights": out.insights if out is not None else ""}

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
        out = await llm.structured(
            system=(
                "你是資料分析助手，請依提供的要點撰寫結構化報告，"
                "包含摘要、關鍵發現、建議三個段落，全程使用繁體中文。"
            ),
            user=f"主題：{state['topic']}\n\n要點：\n{state['insights']}",
            schema=_ReportSynthesizeOutput,
        )
        return {"report": out.report if out is not None else ""}

    return report_synthesize
