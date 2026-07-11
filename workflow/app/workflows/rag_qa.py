from typing import Any, TypedDict

from langgraph.graph import END, START, StateGraph
from langgraph.graph.state import CompiledStateGraph
from pydantic import BaseModel, Field

from app.llm import get_llm
from app.nodes.retrieve import make_retrieve_node
from app.workflows.registry import register


class RagQaInput(BaseModel):
    """rag_qa 工作流的輸入 schema：question 必須是非空字串。"""

    question: str = Field(min_length=1)


class RagQaState(TypedDict, total=False):
    """檢索增強問答流程的狀態。

    tenant_id 由 app.main 依呼叫者的 X-Tenant-Id 自動注入，工作流本身不需要、
    也不應該接受呼叫端在 input 裡自行指定。
    """

    question: str
    tenant_id: str
    docs: list[dict[str, Any]]
    answer: str
    citations: list[dict[str, Any]]


_NOT_FOUND_ANSWER = "在你的租戶資料中找不到相關內容，請先上傳文件。"


async def answer(state: RagQaState) -> dict:
    """依檢索結果回答問題。

    查無任何文件片段時，直接給固定文案並回傳空引用清單，不呼叫 LLM
    （避免在完全沒有依據的情況下讓 LLM 憑空生成看似合理實則捏造的答案）。
    有片段時，把片段內容組成上下文交給 LLM 作答；引用清單一律由程式從
    docs 產生（而非要求 LLM 自行輸出），避免引用內容與實際檢索結果不一致。
    """
    docs = state.get("docs") or []
    if not docs:
        return {"answer": _NOT_FOUND_ANSWER, "citations": []}

    context = "\n\n".join(
        f"[{i + 1}] {doc['title']}：{doc['content']}" for i, doc in enumerate(docs)
    )
    resp = await get_llm().ainvoke(
        [
            (
                "system",
                "你是問答助手，請只根據下方提供的租戶文件內容回答問題，"
                "不要編造文件中沒有的資訊；請用繁體中文作答。",
            ),
            ("user", f"文件內容：\n{context}\n\n問題：{state['question']}"),
        ]
    )
    citations = [
        {
            "document_id": doc["document_id"],
            "title": doc["title"],
            "snippet": doc["content"][:80],
        }
        for doc in docs
    ]
    return {"answer": resp.content, "citations": citations}


@register(
    "rag_qa",
    description="檢索增強問答：以租戶內文件回答問題並附引用",
    required_role="USER",
    input_model=RagQaInput,
)
def build() -> CompiledStateGraph:
    """線性圖：START → retrieve → answer → END。"""
    g = StateGraph(RagQaState)
    g.add_node("retrieve", make_retrieve_node(query_key="question"))
    g.add_node("answer", answer)
    g.add_edge(START, "retrieve")
    g.add_edge("retrieve", "answer")
    g.add_edge("answer", END)
    return g.compile()
