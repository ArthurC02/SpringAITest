from typing import Any, TypedDict

from langgraph.graph import END, START, StateGraph
from langgraph.graph.state import CompiledStateGraph
from pydantic import BaseModel, Field

from app.llm import get_llm
from app.nodes.retrieve import make_retrieve_node
from app.workflows.registry import register


class AnalyzeReportInput(BaseModel):
    """analyze_report 工作流的輸入 schema：topic 必須是非空字串。"""

    topic: str = Field(min_length=1)


class AnalyzeReportState(TypedDict, total=False):
    """主題分析報告流程的狀態；tenant_id 由 app.main 依呼叫者自動注入。"""

    topic: str
    tenant_id: str
    docs: list[dict[str, Any]]
    insights: str
    report: str


_NO_DATA_INSIGHTS = "（無資料）"


async def analyze(state: AnalyzeReportState) -> dict:
    """從檢索到的文件片段中萃取與主題相關的要點。

    查無任何文件片段時直接給固定文案，不呼叫 LLM（沒有依據就沒有要點可萃取）。
    """
    docs = state.get("docs") or []
    if not docs:
        return {"insights": _NO_DATA_INSIGHTS}

    context = "\n\n".join(
        f"[{i + 1}] {doc['title']}：{doc['content']}" for i, doc in enumerate(docs)
    )
    resp = await get_llm().ainvoke(
        [
            (
                "system",
                "你是資料分析助手，請從下方提供的租戶文件內容中，"
                "萃取與指定主題相關的重點，以條列式繁體中文呈現。",
            ),
            ("user", f"主題：{state['topic']}\n\n文件內容：\n{context}"),
        ]
    )
    return {"insights": resp.content}


async def synthesize(state: AnalyzeReportState) -> dict:
    """依萃取出的要點，撰寫一份結構化的主題分析報告。"""
    resp = await get_llm().ainvoke(
        [
            (
                "system",
                "你是資料分析助手，請依提供的要點撰寫結構化報告，"
                "包含摘要、關鍵發現、建議三個段落，全程使用繁體中文。",
            ),
            ("user", f"主題：{state['topic']}\n\n要點：\n{state['insights']}"),
        ]
    )
    return {"report": resp.content}


@register(
    "analyze_report",
    description="檢索租戶文件並產出主題分析報告（管理員限定）",
    required_role="ADMIN",
    input_model=AnalyzeReportInput,
)
def build() -> CompiledStateGraph:
    """線性圖：START → retrieve → analyze → synthesize → END。"""
    g = StateGraph(AnalyzeReportState)
    g.add_node("retrieve", make_retrieve_node(query_key="topic"))
    g.add_node("analyze", analyze)
    g.add_node("synthesize", synthesize)
    g.add_edge(START, "retrieve")
    g.add_edge("retrieve", "analyze")
    g.add_edge("analyze", "synthesize")
    g.add_edge("synthesize", END)
    return g.compile()
