from typing import TypedDict

from langgraph.graph import END, START, StateGraph
from langgraph.graph.state import CompiledStateGraph

from app.llm import get_llm
from app.workflows.registry import register


class TriageState(TypedDict, total=False):
    """分流回答流程的狀態：question 為輸入問題，category 為分類結果，answer 為最終回答。"""

    question: str
    category: str
    answer: str


async def classify(state: TriageState) -> dict:
    """請 LLM 判斷問題的複雜度，只回覆 SIMPLE 或 COMPLEX 一個詞。"""
    resp = await get_llm().ainvoke(
        [
            (
                "system",
                "判斷使用者問題是簡單(SIMPLE)還是複雜(COMPLEX)，只回覆 SIMPLE 或 COMPLEX 一個詞",
            ),
            ("user", state["question"]),
        ]
    )
    return {"category": resp.content}


def route(state: TriageState) -> str:
    """依 classify 節點的分類結果決定走哪個分支。

    預設（找不到 COMPLEX 字樣）一律視為 simple，這是刻意的確定性預設值：
    即使換成固定回覆的 mock-gpt，也能穩定跑通 simple 分支；
    換成真正的 LLM 後，兩條分支才都有機會被觸發。
    """
    return "complex" if "COMPLEX" in state.get("category", "").upper() else "simple"


async def quick_answer(state: TriageState) -> dict:
    """簡單問題：一句話回答。"""
    resp = await get_llm().ainvoke(
        [
            ("system", "請用一句話簡潔回答使用者的問題。"),
            ("user", state["question"]),
        ]
    )
    return {"answer": resp.content}


async def deep_answer(state: TriageState) -> dict:
    """複雜問題：逐步詳盡回答。"""
    resp = await get_llm().ainvoke(
        [
            ("system", "請逐步、詳盡地回答使用者的問題。"),
            ("user", state["question"]),
        ]
    )
    return {"answer": resp.content}


@register("triage", description="依問題複雜度分流回答（條件分支流程）")
def build() -> CompiledStateGraph:
    """條件分支圖：classify 之後依 route() 的結果走向不同節點，兩者最終都收斂到 END。"""
    g = StateGraph(TriageState)
    g.add_node("classify", classify)
    g.add_node("quick_answer", quick_answer)
    g.add_node("deep_answer", deep_answer)
    g.add_edge(START, "classify")
    g.add_conditional_edges(
        "classify", route, {"simple": "quick_answer", "complex": "deep_answer"}
    )
    g.add_edge("quick_answer", END)
    g.add_edge("deep_answer", END)
    return g.compile()
