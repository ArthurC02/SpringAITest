from typing import TypedDict

from langgraph.graph import END, START, StateGraph
from langgraph.graph.state import CompiledStateGraph

from app.llm import get_llm
from app.workflows.registry import register


class SummarizeState(TypedDict, total=False):
    """線性摘要流程的狀態：text 為輸入原文，summary 為摘要結果。"""

    text: str
    summary: str


async def summarize(state: SummarizeState) -> dict:
    """呼叫 LLM 將輸入文字濃縮成三句以內的繁體中文摘要。"""
    resp = await get_llm().ainvoke(
        [
            ("system", "你是摘要助手，將輸入濃縮成三句以內的繁體中文摘要。"),
            ("user", state["text"]),
        ]
    )
    return {"summary": resp.content}


@register("summarize", description="將輸入文字做三句以內的摘要（線性流程）")
def build() -> CompiledStateGraph:
    """最簡單的線性圖：START → summarize → END，只有單一節點。"""
    g = StateGraph(SummarizeState)
    g.add_node("summarize", summarize)
    g.add_edge(START, "summarize")
    g.add_edge("summarize", END)
    return g.compile()
