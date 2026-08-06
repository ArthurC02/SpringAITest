import logging
from functools import lru_cache
from threading import Lock
from typing import Any


_logger = logging.getLogger(__name__)
_handler_failure_lock = Lock()
_handler_failure_count = 0
_handler_failure_warning_emitted = False


@lru_cache(maxsize=1)
def _handler() -> Any:
    """延遲載入並快取單一 Langfuse CallbackHandler 實例。

    延遲載入的原因：只有在 LANGFUSE_ENABLED=true 時才需要用到 langfuse 套件，
    避免未安裝或環境未設定好時，一開機就整個服務起不來。
    """
    from langfuse.langchain import CallbackHandler

    return CallbackHandler()


def handler_failure_count() -> int:
    """回傳本 process 已降級的 Langfuse handler 初始化失敗次數。"""
    with _handler_failure_lock:
        return _handler_failure_count


def _record_handler_failure() -> None:
    """記錄一次初始化失敗，且每個 process 最多寫一則不含內容的警告。"""
    global _handler_failure_count, _handler_failure_warning_emitted

    should_warn = False
    with _handler_failure_lock:
        _handler_failure_count += 1
        if not _handler_failure_warning_emitted:
            _handler_failure_warning_emitted = True
            should_warn = True

    if should_warn:
        try:
            _logger.warning("Langfuse tracing unavailable; continuing without tracing.")
        except Exception:
            # Python logging 不保證會吃掉 handler/filter 例外；可觀測性警告本身不能影響主流程。
            pass


def runnable_config() -> dict:
    """組出可傳給 LangGraph .ainvoke(..., config=...) 的設定。

    預設（LANGFUSE_ENABLED=false）回傳空 dict，完全不影響圖的執行；
    開啟後才會附上 Langfuse callback，讓每次呼叫都送出追蹤資料。
    任何載入失敗（例如缺少對應套件）一律降級為 {}，不讓觀測性問題影響主流程。
    降級會在每個 process 第一次發出固定、無內容的警告，並累計失敗次數供低噪監測。
    """
    from app.settings import settings

    if not settings.langfuse_enabled:
        return {}

    try:
        return {"callbacks": [_handler()]}
    except Exception:
        # 例外內容可能夾帶憑證或請求資料；只記錄固定訊息，絕不把例外傳給日誌。
        _record_handler_failure()
        return {}
