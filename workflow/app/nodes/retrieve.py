"""共用節點：向 backend 的資料檢索 API 查詢與查詢字串最相關的租戶片段。

供各個需要 RAG 檢索的工作流（rag_qa、analyze_report 等）共用，避免每個工作流
各自重寫一份「呼叫 backend /api/retrieval/search → 組裝 docs」的樣板程式碼。

向量庫、切塊、嵌入等核心商業邏輯已搬到 backend/（見 architecture 重組計畫），
本服務只負責帶著租戶身分呼叫該 API；呼叫失敗（連線失敗、非 2xx）一律不在此處
攔截，直接往上拋，讓 app.main 既有的 workflow_execution_failed（500）處理接住。
"""

from typing import Any

import httpx

from app.settings import settings


def make_retrieve_node(query_key: str, top_k: int | None = None):
    """建立一個 LangGraph 節點函式。

    節點會讀取 state[query_key]（要拿來檢索的查詢字串）與 state["tenant_id"]，
    呼叫 backend 的 POST /api/retrieval/search，並把結果寫入 state["docs"]
    （list[dict]，每筆含 document_id、title、content、score）。

    top_k 未指定時，使用 settings.retrieval_top_k 這個全域預設值；
    呼叫端可視需求（例如分析類工作流想看更多資料）傳入不同的 top_k。
    """

    async def retrieve(state: dict[str, Any]) -> dict:
        query = state[query_key]
        tenant_id = state["tenant_id"]
        k = top_k if top_k is not None else settings.retrieval_top_k

        # 逾時放寬到 30s：openai 嵌入模式下 backend 要先算查詢嵌入，httpx 預設 5s 偶發不夠
        async with httpx.AsyncClient(
            base_url=settings.backend_base_url, timeout=httpx.Timeout(30.0)
        ) as client:
            resp = await client.post(
                "/api/retrieval/search",
                json={"query": query, "top_k": k},
                headers={
                    "X-Internal-Token": settings.internal_api_token,
                    "X-Tenant-Id": tenant_id,
                },
            )
            resp.raise_for_status()
            chunks = resp.json()["chunks"]

        docs = [
            {
                "document_id": chunk["document_id"],
                "title": chunk["title"],
                "content": chunk["content"],
                "score": chunk["score"],
            }
            for chunk in chunks
        ]
        return {"docs": docs}

    return retrieve
