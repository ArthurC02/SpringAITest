"""共用低階函式：呼叫 backend 的向量檢索 API。

app/nodes/retrieve.py 與 app/kbquery/adapters.py（BackendVectorSearch）都手建
httpx.AsyncClient → POST /api/retrieval/search → 同組 header → 取 chunks，抽成
此處單一函式；兩條呼叫路徑各自把 chunks 組成自己要的輸出形狀（docs / SourceResult），
不動輸出行為。放在 app/ 層級而非 app/nodes/ 下，避免 app/kbquery/ 反向依賴 app/nodes/。
"""

import httpx

from app.settings import settings


async def search_chunks(query: str, top_k: int, tenant_id: str) -> list[dict]:
    """呼叫 backend POST /api/retrieval/search，回傳原始 chunks（list[dict]）。

    逾時放寬到 30s：openai 嵌入模式下 backend 要先算查詢嵌入，httpx 預設 5s 偶發不夠。
    """
    async with httpx.AsyncClient(
        base_url=settings.backend_base_url, timeout=httpx.Timeout(30.0)
    ) as client:
        resp = await client.post(
            "/api/retrieval/search",
            json={"query": query, "top_k": top_k},
            headers={
                "X-Internal-Token": settings.internal_api_token,
                "X-Tenant-Id": tenant_id,
            },
        )
        resp.raise_for_status()
        return resp.json()["chunks"]
