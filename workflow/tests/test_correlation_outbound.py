"""出站方向的 correlation 鏈路：workflow 打回 backend 的呼叫必須帶同一個 X-Correlation-Id。

test_correlation_id.py 驗的是入站與回應那半邊（收下、白名單、回寫、進日誌）。缺了這半邊，
platform → backend → workflow 對得起來，但 workflow → backend 的那一跳（D3/D5 執行期的
retrieval、run transition、evidence 寫入、prompt manifest resolve —— 正是最需要對號的流量）
在 backend 日誌裡會變成另一串 ID，鏈路等於斷在最後一哩。

兩半邊都測：請求範圍內必須帶且值相同；請求範圍外（背景 retention／recovery 掃描）必須
不帶這個鍵 —— 造一個對不到任何請求的假值比不帶更糟。
"""

import asyncio

import httpx
from fastapi.testclient import TestClient

from app import backend_http
from app.correlation import HEADER_NAME
from app.main import app
from app.security import RequestContext
from app.settings import settings
from tests.conftest import FakeBackendResponse, auth_headers, install_fake_get

client = TestClient(app)


class _NotFound:
    """backend 回 404：invoke 走到「不存在的 skill」→ HTTP 404，不必準備真的 artifact。"""

    status_code = 404

    def raise_for_status(self) -> None:
        return None

    def json(self):
        return None


def _capture_outbound_gets(monkeypatch) -> list[dict]:
    """攔下 invoke 期間所有出站 GET（config 解析 + 自訂 skill 取回），回傳各自的標頭。"""
    seen: list[dict] = []

    def handler(url: str, headers: dict):
        seen.append(headers)
        return _NotFound()

    install_fake_get(monkeypatch, handler)
    return seen


def _invoke_unknown_skill(headers: dict):
    return client.post(
        "/skills/__correlation-outbound-probe__/invoke",
        json={"input": {}},
        headers=headers,
    )


def test_outbound_backend_calls_carry_the_caller_supplied_correlation_id(monkeypatch):
    supplied = "0198f0e2-1b3c-4d5e-8f90-a1b2c3d4e5f6"
    seen = _capture_outbound_gets(monkeypatch)

    resp = _invoke_unknown_skill({**auth_headers(), HEADER_NAME: supplied})

    assert resp.status_code == 404
    assert seen, "這條路徑本來就會打 backend；沒打到代表測試沒驗到東西"
    assert all(headers.get(HEADER_NAME) == supplied for headers in seen)


def test_outbound_correlation_id_matches_the_generated_response_id(monkeypatch):
    """呼叫端沒帶時 middleware 會生一個 —— 出站帶的必須就是回應那一個，不是另生一串。"""
    seen = _capture_outbound_gets(monkeypatch)

    resp = _invoke_unknown_skill(auth_headers())

    generated = resp.headers[HEADER_NAME]
    assert generated
    assert seen
    assert all(headers.get(HEADER_NAME) == generated for headers in seen)


def test_retrieval_search_carries_the_correlation_id_without_leaking_identity(monkeypatch):
    """檢索路徑的標頭改由共用基底組成後的逐鍵對照：多了 correlation，其餘一鍵不增不減。"""
    captured: dict = {}

    async def fake_post(self, url, json=None, headers=None, **kwargs):
        captured["headers"] = headers
        return FakeBackendResponse([])

    monkeypatch.setattr(httpx.AsyncClient, "post", fake_post)
    monkeypatch.setattr(
        backend_http.correlation, "current_correlation_id", lambda: "cid-outbound-1"
    )

    asyncio.run(backend_http.search_chunks("q", 3, "demo-a"))

    assert captured["headers"] == {
        "X-Internal-Token": settings.internal_api_token,
        "X-Tenant-Id": "demo-a",
        HEADER_NAME: "cid-outbound-1",
    }


def test_headers_omit_correlation_id_outside_a_request_scope():
    """決策表另一半：背景掃描沒有 correlation ID，就不帶這個鍵（不造假值）。"""
    assert backend_http.internal_token_headers() == {
        "X-Internal-Token": settings.internal_api_token
    }
    assert backend_http.internal_headers(
        RequestContext(tenant_id="demo-a", user_id="alice", role="USER")
    ) == {
        "X-Internal-Token": settings.internal_api_token,
        "X-Tenant-Id": "demo-a",
        "X-User-Id": "alice",
        "X-User-Role": "USER",
    }
