"""基礎設施加固的護欄：共用 HTTP client 單例、LLM 逾時/重試、tracing 靜默降級。

對應修復項：
- backend_http 的 module 級共用 AsyncClient（get_client 單例、aclose 後重建）。
- build_llm 顯式帶 timeout / max_retries（可經 settings 覆寫）。
- tracing.runnable_config 任何載入失敗（不限 ImportError）一律降級為 {}。
"""

import asyncio

import httpx
import pytest
from fastapi import HTTPException
from pydantic import ValidationError

from app import backend_http, tracing
from app.llm import build_llm
from app.security import require_internal
from app.settings import Settings, settings


# ---------------------------------------------------------------------------
# backend_http 共用 client：單例 + aclose 重建
# ---------------------------------------------------------------------------


def test_get_client_returns_same_singleton_and_aclose_resets():
    # 收尾一律把單例還原成「未建立」(_client=None) —— 不還原舊 instance：那顆可能已被本測試
    # aclose 掉，還回去會讓後續請求打到已關閉的 client。下個呼叫端 get_client() 會延遲重建。
    try:
        c1 = backend_http.get_client()
        c2 = backend_http.get_client()
        assert c1 is c2  # 每次呼叫不新建
        assert isinstance(c1, httpx.AsyncClient)

        asyncio.run(backend_http.aclose_client())
        c3 = backend_http.get_client()
        assert c3 is not c1  # 關閉後 get 會重建新的一顆
    finally:
        asyncio.run(backend_http.aclose_client())


def test_shared_client_base_url_points_at_backend():
    try:
        asyncio.run(backend_http.aclose_client())
        client = backend_http.get_client()
        assert str(client.base_url) == settings.backend_base_url
    finally:
        asyncio.run(backend_http.aclose_client())


# ---------------------------------------------------------------------------
# build_llm：timeout / max_retries 從 settings 帶入 ChatOpenAI
# ---------------------------------------------------------------------------


def test_build_llm_wires_timeout_and_max_retries_from_settings():
    llm = build_llm("mock-gpt", 0.3)
    # langchain_openai 把 timeout 存進 request_timeout、重試存進 max_retries
    assert llm.request_timeout == settings.llm_timeout
    assert llm.max_retries == settings.llm_max_retries


def test_build_llm_timeout_override_via_settings(monkeypatch):
    monkeypatch.setattr(settings, "llm_timeout", 12.5)
    monkeypatch.setattr(settings, "llm_max_retries", 0)
    llm = build_llm("mock-gpt", 0.0)
    assert llm.request_timeout == 12.5
    assert llm.max_retries == 0


# ---------------------------------------------------------------------------
# tracing：非 ImportError 的失敗也要靜默降級（不只 ImportError）
# ---------------------------------------------------------------------------


def test_runnable_config_swallows_non_import_errors(monkeypatch):
    """LANGFUSE_ENABLED 時 _handler 拋任意例外（如金鑰未設 RuntimeError）→ 仍回 {}。"""
    monkeypatch.setattr(settings, "langfuse_enabled", True)

    def _boom():
        raise RuntimeError("langfuse 金鑰未設")

    monkeypatch.setattr(tracing, "_handler", _boom)
    assert tracing.runnable_config() == {}


def test_runnable_config_disabled_returns_empty():
    """決策表另一半：關閉時本來就回 {}（不觸發任何 langfuse 載入）。"""
    # settings.langfuse_enabled 預設 False；不 monkeypatch，直接驗預設路徑
    assert tracing.runnable_config() == {}


# ---------------------------------------------------------------------------
# security：內部 token 定時比較（compare_digest）在畸形標頭下仍收斂成 401
# ---------------------------------------------------------------------------


def test_require_internal_accepts_correct_token():
    """決策表一半：正確 token 通過（不 raise）。"""
    assert asyncio.run(require_internal(settings.internal_api_token)) is None


def test_require_internal_wrong_token_raises_401():
    with pytest.raises(HTTPException) as exc:
        asyncio.run(require_internal("wrong-token"))
    assert exc.value.status_code == 401


def test_require_internal_non_ascii_token_is_401_not_typeerror():
    """畸形（latin-1 非 ASCII，如 HTTP 標頭實際可承載的 0x80-0xFF）token。

    HTTP 標頭以 latin-1 解碼，端點會收到含非 ASCII 字元的 str；compare_digest 直接比 str
    會拋 TypeError（→ 未捕捉例外 500）。改比 bytes 後穩定收斂成 401，而非 500。
    """
    with pytest.raises(HTTPException) as exc:
        asyncio.run(require_internal("內部密鑰-café"))
    assert exc.value.status_code == 401
    assert exc.value.detail["error"] == "unauthorized"


def test_require_internal_missing_token_raises_401():
    with pytest.raises(HTTPException) as exc:
        asyncio.run(require_internal(None))
    assert exc.value.status_code == 401


@pytest.mark.parametrize("configured_token", ["", "   "])
def test_settings_rejects_empty_or_whitespace_internal_token_from_env(
    monkeypatch, configured_token
):
    """pydantic-settings 會讀取顯式空 env；新建實例驗證 fail-fast，不污染模組全域設定。"""
    monkeypatch.setenv("INTERNAL_API_TOKEN", configured_token)

    with pytest.raises(ValidationError, match="INTERNAL_API_TOKEN"):
        Settings(_env_file=None)


@pytest.mark.parametrize("configured_token", ["", "   "])
def test_require_internal_rejects_empty_or_whitespace_config_even_if_header_matches(
    monkeypatch, configured_token
):
    """防禦縱深：即使設定物件被測試或執行期誤改，空 token 也絕不可能驗證成功。"""
    monkeypatch.setattr(settings, "internal_api_token", configured_token)

    with pytest.raises(HTTPException) as exc:
        asyncio.run(require_internal(configured_token))
    assert exc.value.status_code == 401
