"""共用節點：從租戶向量庫中檢索與查詢字串最相關的片段。

供各個需要 RAG 檢索的工作流（rag_qa、analyze_report 等）共用，避免每個工作流
各自重寫一份「嵌入查詢字串 → 呼叫向量庫 search()」的樣板程式碼。
"""

from typing import Any

from app.embeddings import get_embeddings
from app.settings import settings
from app.vectorstore import get_vector_store


def make_retrieve_node(query_key: str, top_k: int | None = None):
    """建立一個 LangGraph 節點函式。

    節點會讀取 state[query_key]（要拿來檢索的查詢字串）與 state["tenant_id"]，
    嵌入查詢字串後呼叫向量庫的 search()，並把結果寫入 state["docs"]
    （list[dict]，每筆含 document_id、title、content、score）。

    top_k 未指定時，使用 settings.retrieval_top_k 這個全域預設值；
    呼叫端可視需求（例如分析類工作流想看更多資料）傳入不同的 top_k。
    """

    async def retrieve(state: dict[str, Any]) -> dict:
        query = state[query_key]
        tenant_id = state["tenant_id"]
        k = top_k if top_k is not None else settings.retrieval_top_k

        embedding = await get_embeddings().aembed_query(query)
        hits = await get_vector_store().search(tenant_id, embedding, k)

        docs = [
            {
                "document_id": hit.document_id,
                "title": hit.title,
                "content": hit.content,
                "score": hit.score,
            }
            for hit in hits
        ]
        return {"docs": docs}

    return retrieve
