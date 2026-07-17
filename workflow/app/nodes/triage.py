"""triage_* 節點：逐字移植 app/workflows/triage.py 的 classify/quick_answer/deep_answer。

手寫圖把「LLM 回覆非標準字串 → 預設 simple」這個確定性預設放在 route()（條件邊）；
Node-First 版本改在 triage_classify 節點內就把 category 正規化成 'SIMPLE'/'COMPLEX'
兩個字面值（同一套判斷規則：不分大小寫含 COMPLEX 字樣才算 complex），讓 YAML 的
branch when 只需比較 state.category == 'COMPLEX' 這種簡單、可靠的條件式
（app/engine/expressions.py 的白名單只認 state.<key> 比較字面量，不支援字串方法
呼叫，因此正規化必須在節點內做，不能留給條件式做）。
"""

from pydantic import BaseModel

from app.engine.node_registry import node


class _TriageClassifyOutput(BaseModel):
    category: str


class _TriageAnswerOutput(BaseModel):
    answer: str


@node(
    name="triage_classify",
    version="1.0",
    description="判斷問題複雜度，正規化為 'SIMPLE'/'COMPLEX'（找不到 COMPLEX 字樣一律 SIMPLE）",
    reads=["question"],
    writes=["category"],
    deps=["llm"],
    requires_tools=[],
)
def make_triage_classify_node(llm):
    """建立 triage_classify 節點函式（對齊 workflows/triage.py::classify + route 的語意）。"""

    async def triage_classify(state: dict) -> dict:
        out = await llm.structured(
            system=(
                "判斷使用者問題是簡單(SIMPLE)還是複雜(COMPLEX)，只回覆 SIMPLE 或 COMPLEX 一個詞"
            ),
            user=state["question"],
            schema=_TriageClassifyOutput,
        )
        raw = out.category if out is not None else ""
        # 確定性預設值：找不到 COMPLEX 字樣一律視為 simple（對齊手寫圖 route() 的語意）
        category = "COMPLEX" if "COMPLEX" in raw.upper() else "SIMPLE"
        return {"category": category}

    return triage_classify


@node(
    name="triage_quick_answer",
    version="1.0",
    description="簡單問題：一句話回答",
    reads=["question"],
    writes=["answer"],
    deps=["llm"],
    requires_tools=[],
)
def make_triage_quick_answer_node(llm):
    """建立 triage_quick_answer 節點函式（對齊 workflows/triage.py::quick_answer 的語意）。"""

    async def triage_quick_answer(state: dict) -> dict:
        out = await llm.structured(
            system="請用一句話簡潔回答使用者的問題。",
            user=state["question"],
            schema=_TriageAnswerOutput,
        )
        return {"answer": out.answer if out is not None else ""}

    return triage_quick_answer


@node(
    name="triage_deep_answer",
    version="1.0",
    description="複雜問題：逐步詳盡回答",
    reads=["question"],
    writes=["answer"],
    deps=["llm"],
    requires_tools=[],
)
def make_triage_deep_answer_node(llm):
    """建立 triage_deep_answer 節點函式（對齊 workflows/triage.py::deep_answer 的語意）。"""

    async def triage_deep_answer(state: dict) -> dict:
        out = await llm.structured(
            system="請逐步、詳盡地回答使用者的問題。",
            user=state["question"],
            schema=_TriageAnswerOutput,
        )
        return {"answer": out.answer if out is not None else ""}

    return triage_deep_answer
