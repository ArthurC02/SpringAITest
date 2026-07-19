"""跨測試檔共用的假物件與 helper（各測試檔原本各有一份，上移去重）。"""

import asyncio

import httpx

from app import skills
from app.engine import compiler

# backend 內部信任邊界的預設 token（＝settings.internal_api_token 的預設值）；多檔散布 import。
INTERNAL_TOKEN = "internal-dev-token"


def auth_headers(
    tenant_id="demo-a", user_id="alice", role="USER", token=INTERNAL_TOKEN
) -> dict:
    """組出符合服務間契約的標頭；預設是 demo-a 租戶的一般使用者。

    任一參數傳 None 即拔掉對應標頭（token 拔除是「缺 internal token → 401」等測試要的擴充版，
    test_config_apply / test_skills_api / test_skills_custom 原本各自帶一份逐字相同的 `_headers`，
    收斂於此）。四參數皆用預設時輸出與舊版固定四鍵完全一致。
    """
    headers = {}
    if token is not None:
        headers["X-Internal-Token"] = token
    if tenant_id is not None:
        headers["X-Tenant-Id"] = tenant_id
    if user_id is not None:
        headers["X-User-Id"] = user_id
    if role is not None:
        headers["X-User-Role"] = role
    return headers


class FakeBackendResponse:
    """假的 httpx.Response：只提供 retrieve 節點用得到的兩個方法。"""

    def __init__(self, chunks):
        self._chunks = chunks

    def raise_for_status(self) -> None:
        return None

    def json(self) -> dict:
        return {"chunks": self._chunks}


def patch_retrieve(monkeypatch, chunks) -> None:
    """monkeypatch httpx.AsyncClient.post → retrieve 節點收到 chunks（無額外斷言/記錄）。

    fake_post 內另有斷言或記錄行為（不只回 chunks）的測試各自保留 inline 版，不硬換。
    """

    async def fake_post(self, url, json=None, headers=None, **kwargs):
        return FakeBackendResponse(chunks)

    monkeypatch.setattr(httpx.AsyncClient, "post", fake_post)


def install_fake_get(monkeypatch, handler) -> None:
    """monkeypatch httpx.AsyncClient.get → handler(url, headers)；各 Fake 只留自己的 handle。"""

    async def fake_get(self, url, headers=None, **kwargs):  # noqa: ANN001
        return handler(url, headers or {})

    monkeypatch.setattr(httpx.AsyncClient, "get", fake_get)


def invoke_builtin(name: str, deps, **state) -> dict:
    """編譯指定 builtin skill 的圖、執行一次、回 public_output（三個逐字 `_invoke` 共用）。"""
    skill = skills.get(name).skill
    graph = compiler.compile(skill, deps)
    out = asyncio.run(graph.ainvoke({"tenant_id": "t", **state}))
    return compiler.public_output(out)
