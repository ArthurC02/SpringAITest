"""測試 app.nodes.retrieve：改為呼叫 backend /api/retrieval/search 後的行為。

全程以 monkeypatch 換掉 httpx.AsyncClient.post，不打真的網路；backend 檢索邏輯
本身（向量相似度排序等）的測試屬於 backend/ 的範圍，這裡只驗證 workflow 這端
「怎麼呼叫、怎麼把回應轉成 state["docs"]、失敗時是否照樣往上拋」。

節點函式是 async def，但專案未安裝 pytest-asyncio；用 asyncio.run() 在一般同步
測試函式裡執行，不需要額外相依套件。
"""

import asyncio

import httpx
import pytest

from app.nodes.retrieve import make_retrieve_node
from app.settings import settings


class _FakeResponse:
    def __init__(self, chunks):
        self._chunks = chunks

    def raise_for_status(self) -> None:
        return None

    def json(self) -> dict:
        return {"chunks": self._chunks}


def test_retrieve_sends_expected_request_and_maps_chunks_to_docs(monkeypatch):
    captured = {}

    async def fake_post(self, url, json=None, headers=None, **kwargs):
        captured["url"] = url
        captured["json"] = json
        captured["headers"] = headers
        return _FakeResponse(
            [
                {
                    "document_id": "doc-1",
                    "title": "示例文件",
                    "content": "公司地址在台北市信義區。",
                    "score": 0.9,
                }
            ]
        )

    monkeypatch.setattr(httpx.AsyncClient, "post", fake_post)

    node = make_retrieve_node(query_key="question")
    result = asyncio.run(node({"question": "公司地址在哪？", "tenant_id": "demo-a"}))

    assert captured["url"] == "/api/retrieval/search"
    assert captured["json"] == {"query": "公司地址在哪？", "top_k": settings.retrieval_top_k}
    assert captured["headers"] == {
        "X-Internal-Token": settings.internal_api_token,
        "X-Tenant-Id": "demo-a",
    }
    assert result == {
        "docs": [
            {
                "document_id": "doc-1",
                "title": "示例文件",
                "content": "公司地址在台北市信義區。",
                "score": 0.9,
            }
        ]
    }


def test_retrieve_empty_chunks_returns_empty_docs(monkeypatch):
    async def fake_post(self, url, json=None, headers=None, **kwargs):
        return _FakeResponse([])

    monkeypatch.setattr(httpx.AsyncClient, "post", fake_post)

    node = make_retrieve_node(query_key="question")
    result = asyncio.run(node({"question": "沒有相關資料", "tenant_id": "demo-b"}))

    assert result == {"docs": []}


def test_retrieve_uses_explicit_top_k_override(monkeypatch):
    captured = {}

    async def fake_post(self, url, json=None, headers=None, **kwargs):
        captured["json"] = json
        return _FakeResponse([])

    monkeypatch.setattr(httpx.AsyncClient, "post", fake_post)

    node = make_retrieve_node(query_key="topic", top_k=10)
    asyncio.run(node({"topic": "本季銷售", "tenant_id": "demo-a"}))

    assert captured["json"]["top_k"] == 10


def test_retrieve_propagates_backend_http_error(monkeypatch):
    """backend 呼叫失敗（非 2xx）時，錯誤應直接往上拋，讓 app.main 的
    workflow_execution_failed（500）處理接住，節點本身不吞錯誤。
    """

    class _FailingResponse:
        def raise_for_status(self):
            raise httpx.HTTPStatusError("backend error", request=None, response=None)

    async def fake_post(self, url, json=None, headers=None, **kwargs):
        return _FailingResponse()

    monkeypatch.setattr(httpx.AsyncClient, "post", fake_post)

    node = make_retrieve_node(query_key="question")

    with pytest.raises(httpx.HTTPStatusError):
        asyncio.run(node({"question": "問題", "tenant_id": "demo-a"}))
