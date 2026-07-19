"""共用低階函式：呼叫 backend 的向量檢索 API，以及全服務共用的 backend HTTP client。

app/nodes/retrieve.py 與 app/kbquery/adapters.py（BackendVectorSearch）都經此處
POST /api/retrieval/search → 同組 header → 取 chunks，抽成單一函式；兩條呼叫路徑
各自把 chunks 組成自己要的輸出形狀（docs / SourceResult），不動輸出行為。放在 app/
層級而非 app/nodes/ 下，避免 app/kbquery/ 反向依賴 app/nodes/。

共用 client 的理由：每次呼叫新建 httpx.AsyncClient 會丟掉連線池、每請求重做 TCP/TLS
握手，高頻檢索下純屬浪費。改成 module 級單例、由 FastAPI lifespan 於關機時 aclose；
skills/custom.py 走同一顆（同一個 backend base_url）。lifespan 沒跑到的情境（測試以
TestClient 不進 context manager）則靠 get_client() 延遲建立，行為與過去一致。
"""

import httpx

from app.settings import settings

_client: httpx.AsyncClient | None = None


def get_client() -> httpx.AsyncClient:
    """取得共用的 backend HTTP client（延遲建立；預設逾時 30s，個別呼叫可自帶 timeout 覆寫）。

    逾時預設放寬到 30s：openai 嵌入模式下 backend 要先算查詢嵌入，httpx 預設 5s 偶發不夠。
    """
    global _client
    if _client is None:
        _client = httpx.AsyncClient(
            base_url=settings.backend_base_url, timeout=httpx.Timeout(30.0)
        )
    return _client


async def aclose_client() -> None:
    """關閉共用 client（lifespan 關機時呼叫；下次 get_client 會重新建立）。"""
    global _client
    if _client is not None:
        await _client.aclose()
        _client = None


async def search_chunks(query: str, top_k: int, tenant_id: str) -> list[dict]:
    """呼叫 backend POST /api/retrieval/search，回傳原始 chunks（list[dict]）。"""
    resp = await get_client().post(
        "/api/retrieval/search",
        json={"query": query, "top_k": top_k},
        headers={
            "X-Internal-Token": settings.internal_api_token,
            "X-Tenant-Id": tenant_id,
        },
    )
    resp.raise_for_status()
    return resp.json()["chunks"]
