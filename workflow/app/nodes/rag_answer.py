"""rag_answer 節點：app/skills/rag-qa.yaml 的作答步驟（依檢索到的 docs 回答問題）。

為什麼用 llm.structured 而不是自己取全域 LLM：節點不得碰全域 settings 或單例
（見 app/engine/node_registry.py 的 deps 慣例），LLM 一律由 deps 注入，呼叫方式
對齊 nl_logic/nl_extract 節點（StructuredLLMPort.structured）。
「docs 為空 → 固定文案、不呼叫 LLM」是節點內的確定性守衛。
"""

from pydantic import BaseModel

from app.engine.node_registry import node
from app.nodes._llm_input import format_docs_context, structured_field

# 檢索不到任何 docs 時的固定回覆文案（不呼叫 LLM）。
_NOT_FOUND_ANSWER = "在你的租戶資料中找不到相關內容，請先上傳文件。"


class _RagAnswerOutput(BaseModel):
    """LLM 的結構化輸出：只取一段文字答案。"""

    answer: str


@node(
    name="rag_answer",
    version="1.0",
    description="依檢索結果回答問題；docs 為空時不呼叫 LLM，直接回固定文案 + 空引用",
    reads=["question", "docs"],
    writes=["answer", "citations"],
    deps=["llm"],
    requires_tools=[],
)
def make_rag_answer_node(llm):
    """建立 rag_answer 節點函式（對齊 workflows/rag_qa.py::answer 的語意）。"""

    async def rag_answer(state: dict) -> dict:
        docs = state.get("docs") or []
        if not docs:
            return {"answer": _NOT_FOUND_ANSWER, "citations": []}

        context = format_docs_context(docs)
        answer = await structured_field(
            llm,
            system=(
                "你是問答助手，請只根據下方提供的租戶文件內容回答問題，"
                "不要編造文件中沒有的資訊；請用繁體中文作答。"
            ),
            user=f"文件內容：\n{context}\n\n問題：{state['question']}",
            schema=_RagAnswerOutput,
            field="answer",
        )
        citations = [
            {
                "document_id": doc["document_id"],
                "title": doc["title"],
                "snippet": doc["content"][:80],
            }
            for doc in docs
        ]
        return {"answer": answer, "citations": citations}

    return rag_answer
