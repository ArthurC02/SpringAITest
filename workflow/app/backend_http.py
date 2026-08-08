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

import asyncio
from typing import Protocol

import httpx

from app import correlation
from app.settings import settings

_client: httpx.AsyncClient | None = None
# 建立 client 時所在的事件圈：async httpx client 的連線/transport 綁定該圈，換圈重用會炸。
_client_loop: asyncio.AbstractEventLoop | None = None


def get_client() -> httpx.AsyncClient:
    """取得共用的 backend HTTP client（延遲建立；預設逾時 30s，個別呼叫可自帶 timeout 覆寫）。

    逾時預設放寬到 30s：openai 嵌入模式下 backend 要先算查詢嵌入，httpx 預設 5s 偶發不夠。

    事件圈守衛：httpx.AsyncClient 的 transport/連線池綁定「建立當下的事件圈」。正式服務
    整個生命週期只有一個圈，此守衛永不觸發、行為與過去一致；但測試以 Starlette TestClient
    每個請求可能起用不同的短命事件圈（Windows proactor 尤甚），沿用綁在已關閉舊圈上的
    client 會在 transport.close() 拋 "Event loop is closed"。偵測到綁定圈已非當前執行圈
    （或已關閉）就重建，讓單例對多圈情境也安全。
    """
    global _client, _client_loop
    try:
        current = asyncio.get_running_loop()
    except RuntimeError:
        current = None
    if _client is not None and _client_loop is not None and current is not None:
        if _client_loop is not current or _client_loop.is_closed():
            _client = None  # 舊圈已作古：丟棄（不 aclose，其圈已關無從清理），下面重建
    if _client is None:
        _client = httpx.AsyncClient(
            base_url=settings.backend_base_url, timeout=httpx.Timeout(30.0)
        )
        _client_loop = current
    return _client


async def aclose_client() -> None:
    """關閉共用 client（lifespan 關機時呼叫；下次 get_client 會重新建立）。"""
    global _client, _client_loop
    if _client is not None:
        await _client.aclose()
        _client = None
        _client_loop = None


class Identity(Protocol):
    """帶租戶身分的 context（RequestContext 與 ToolContext 都符合這個形狀）。"""

    tenant_id: str
    user_id: str
    role: str


def internal_token_headers() -> dict[str, str]:
    """所有出站 backend 呼叫的共同基底：內部密鑰 ＋（有的話）本次請求的 correlation ID。

    correlation ID 由 app/correlation.py 的 middleware 從入站請求接手（不合格或缺就生一個），
    往 backend 轉回去，platform → backend → workflow → backend 的日誌才串得成同一條。
    背景工作（checkpoint retention、recovery 掃描）不在請求範圍內，current_correlation_id()
    回空字串 —— 那就不帶這個鍵，不造一個對不到任何請求的假值。
    """
    headers = {"X-Internal-Token": settings.internal_api_token}
    correlation_id = correlation.current_correlation_id()
    if correlation_id:
        headers[correlation.HEADER_NAME] = correlation_id
    return headers


def internal_headers(ctx: Identity) -> dict[str, str]:
    """服務間標頭：共同基底 + 身分（租戶邊界由 backend 依 X-Tenant-Id 過濾）。"""
    return internal_token_headers() | {
        "X-Tenant-Id": ctx.tenant_id,
        "X-User-Id": ctx.user_id,
        "X-User-Role": ctx.role,
    }


async def search_chunks(query: str, top_k: int, tenant_id: str) -> list[dict]:
    """呼叫 backend POST /api/retrieval/search，回傳原始 chunks（list[dict]）。"""
    return await _search_chunks({"query": query, "top_k": top_k}, tenant_id)


async def _search_chunks(payload: dict, tenant_id: str) -> list[dict]:
    resp = await get_client().post(
        "/api/retrieval/search",
        json=payload,
        # 這條路徑只有租戶（node/port 拿不到呼叫者 user/role），刻意不帶 X-User-*。
        headers=internal_token_headers() | {"X-Tenant-Id": tenant_id},
    )
    resp.raise_for_status()
    return resp.json()["chunks"]


async def search_chunks_scoped(
    query: str,
    top_k: int,
    tenant_id: str,
    knowledge_sources: list[str],
) -> list[dict]:
    """D3 retrieval contract: scope is server-built and version-pinned."""
    return await _search_chunks(
        {
            "query": query,
            "top_k": top_k,
            "knowledge_sources": knowledge_sources,
            "scope_contract_version": 1,
        },
        tenant_id,
    )
