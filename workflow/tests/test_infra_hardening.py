"""基礎設施加固的護欄：共用 HTTP client 單例、LLM 逾時/重試、tracing 安全降級。

對應修復項：
- backend_http 的 module 級共用 AsyncClient（get_client 單例、aclose 後重建）。
- build_llm 顯式帶 timeout / max_retries（可經 settings 覆寫）。
- tracing.runnable_config 在 handler 初始化失敗時安全降級並保留低噪訊號。
"""

import asyncio
import logging

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


def test_get_client_reuses_within_one_loop_but_rebuilds_across_loops():
    """事件圈守衛：同圈沿用、換圈重建（TestClient 每請求換短命圈的情境）。

    上面的單例測試都在事件圈外呼叫（current 為 None，守衛整段跳過），守衛本身沒被走過。
    這裡用 asyncio.run 起兩個各自獨立、跑完即關的事件圈，逼出重建那一支。
    """
    try:
        asyncio.run(backend_http.aclose_client())

        async def _pair():
            return backend_http.get_client(), backend_http.get_client()

        a1, a2 = asyncio.run(_pair())
        assert a1 is a2  # 同一圈內：綁定圈＝當前圈，沿用同一顆

        b1, _ = asyncio.run(_pair())
        assert b1 is not a1  # 換圈（且舊圈已關）：丟棄舊 client 重建

        # 圈外呼叫 current 為 None，守衛不觸發：即使綁定圈已關也照樣沿用（現行行為）
        assert backend_http.get_client() is b1
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
# tracing：handler 初始化失敗時安全降級、每 process 僅一則無內容警告
# ---------------------------------------------------------------------------


@pytest.fixture
def reset_tracing_failure_state(monkeypatch):
    """隔離 process 級降級計數與一次性警告狀態，避免測試依賴執行順序。"""
    monkeypatch.setattr(tracing, "_handler_failure_count", 0)
    monkeypatch.setattr(tracing, "_handler_failure_warning_emitted", False)


def test_runnable_config_failure_degrades_counts_and_logs_once_without_exception_content(
    monkeypatch, caplog, reset_tracing_failure_state
):
    """不同的敏感例外皆降級；只留固定警告，失敗數仍準確累加。"""
    monkeypatch.setattr(settings, "langfuse_enabled", True)
    first_secret = "langfuse-secret-one"
    second_secret = "langfuse-secret-two"
    failures = iter((RuntimeError(first_secret), ValueError(second_secret)))

    def _boom():
        raise next(failures)

    monkeypatch.setattr(tracing, "_handler", _boom)
    with caplog.at_level(logging.WARNING, logger=tracing.__name__):
        assert tracing.runnable_config() == {}
        assert tracing.runnable_config() == {}

    warnings = [
        record
        for record in caplog.records
        if record.name == tracing.__name__ and record.levelno == logging.WARNING
    ]
    assert len(warnings) == 1
    assert warnings[0].getMessage() == "Langfuse tracing unavailable; continuing without tracing."
    assert first_secret not in caplog.text
    assert second_secret not in caplog.text
    assert tracing.handler_failure_count() == 2


def test_runnable_config_logging_handler_can_read_counter_and_reenter(
    monkeypatch, reset_tracing_failure_state
):
    """logging 在鎖外執行：handler 可讀 counter 並重入，而不會死鎖或重複警告。"""
    monkeypatch.setattr(settings, "langfuse_enabled", True)

    def _boom():
        raise RuntimeError("handler initialization failed")

    class _ReentrantHandler(logging.Handler):
        def __init__(self):
            super().__init__()
            self.observed_counts: list[int] = []
            self.reentrant_configs: list[dict] = []

        def emit(self, record):
            self.observed_counts.append(tracing.handler_failure_count())
            self.reentrant_configs.append(tracing.runnable_config())

    monkeypatch.setattr(tracing, "_handler", _boom)
    logger = logging.getLogger(tracing.__name__)
    handler = _ReentrantHandler()
    logger.addHandler(handler)
    try:
        assert tracing.runnable_config() == {}
    finally:
        logger.removeHandler(handler)

    assert handler.observed_counts == [1]
    assert handler.reentrant_configs == [{}]
    assert tracing.handler_failure_count() == 2


def test_runnable_config_ignores_failing_logging_handler(
    monkeypatch, reset_tracing_failure_state
):
    """logging.Handler.handleError 不會替 emit 例外提供保證，故 tracing 必須自行隔離。"""
    monkeypatch.setattr(settings, "langfuse_enabled", True)

    def _boom():
        raise RuntimeError("handler initialization failed")

    class _FailingHandler(logging.Handler):
        def emit(self, record):
            raise RuntimeError("logging handler failure")

    monkeypatch.setattr(tracing, "_handler", _boom)
    logger = logging.getLogger(tracing.__name__)
    handler = _FailingHandler()
    logger.addHandler(handler)
    try:
        assert tracing.runnable_config() == {}
    finally:
        logger.removeHandler(handler)

    assert tracing.handler_failure_count() == 1


def test_runnable_config_enabled_returns_callbacks_with_handler(
    monkeypatch, reset_tracing_failure_state
):
    """決策表最後一格：開啟且 _handler 正常 → 唯一回非空 dict 的那支。"""
    monkeypatch.setattr(settings, "langfuse_enabled", True)
    handler = object()
    monkeypatch.setattr(tracing, "_handler", lambda: handler)
    assert tracing.runnable_config() == {"callbacks": [handler]}
    assert tracing.handler_failure_count() == 0


def test_runnable_config_disabled_skips_handler_logging_and_failure_count(
    monkeypatch, caplog, reset_tracing_failure_state
):
    """關閉時不載入 handler、不警告，也不累計降級失敗。"""
    monkeypatch.setattr(settings, "langfuse_enabled", False)

    def _must_not_run():
        raise AssertionError("disabled path must not initialize Langfuse")

    monkeypatch.setattr(tracing, "_handler", _must_not_run)
    with caplog.at_level(logging.WARNING, logger=tracing.__name__):
        assert tracing.runnable_config() == {}

    assert caplog.records == []
    assert tracing.handler_failure_count() == 0


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
