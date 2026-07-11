from functools import lru_cache
from typing import Any


@lru_cache(maxsize=1)
def _handler() -> Any:
    """延遲載入並快取單一 Langfuse CallbackHandler 實例。

    延遲載入的原因：只有在 LANGFUSE_ENABLED=true 時才需要用到 langfuse 套件，
    避免未安裝或環境未設定好時，一開機就整個服務起不來。
    """
    from langfuse.langchain import CallbackHandler

    return CallbackHandler()


def runnable_config() -> dict:
    """組出可傳給 LangGraph .ainvoke(..., config=...) 的設定。

    預設（LANGFUSE_ENABLED=false）回傳空 dict，完全不影響圖的執行；
    開啟後才會附上 Langfuse callback，讓每次呼叫都送出追蹤資料。
    任何載入失敗（例如缺少對應套件）一律靜默降級為 {}，不讓觀測性問題影響主流程。
    """
    from app.settings import settings

    if not settings.langfuse_enabled:
        return {}

    try:
        return {"callbacks": [_handler()]}
    except ImportError:
        return {}
