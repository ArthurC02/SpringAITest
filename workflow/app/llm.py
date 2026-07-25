from functools import lru_cache

from langchain_openai import ChatOpenAI

from app.settings import settings


# 全域路徑的預設溫度（歷史寫死值，P4c 促升為 per-config 可覆寫；全域路徑仍用這個常數）。
DEFAULT_TEMPERATURE = 0.7


def build_llm(model: str, temperature: float) -> ChatOpenAI:
    """建立一顆 LLM 客戶端（不經 lru_cache 單例）。

    P4c apply-at-execution 需要以租戶有效設定的 model / temperature 各建一顆，
    不能共用 get_llm 的單例（那顆鎖死在啟動時的全域 model 與 0.7）。全域路徑仍走 get_llm。

    timeout / max_retries 顯式帶上（可經 settings 覆寫）：不設 timeout 時 httpx 端會無限
    等待掛住的 LiteLLM，單一慢請求就能拖垮整個 invoke 逾時預算；max_retries 收斂重試次數，
    避免故障時對閘道連環重打。
    """
    return ChatOpenAI(
        base_url=settings.llm_base_url,
        api_key=settings.llm_api_key,
        model=model,
        temperature=temperature,
        timeout=settings.llm_timeout,
        max_retries=settings.llm_max_retries,
    )


@lru_cache(maxsize=1)
def get_llm() -> ChatOpenAI:
    """取得共用的 LLM 客戶端（單例快取，避免每次呼叫都重新建立連線設定）。

    一律透過既有的 LiteLLM 閘道存取模型，因此 base_url / api_key 皆指向 LiteLLM，
    實際使用的模型名稱由 LLM_MODEL 環境變數決定（測試時可指向 mock-gpt）。
    """
    return build_llm(settings.llm_model, DEFAULT_TEMPERATURE)


@lru_cache(maxsize=1)
def get_direct_agent_runtime_llm() -> ChatOpenAI:
    """D3 billable turns are single-attempt; retries belong to the run graph."""
    return ChatOpenAI(
        base_url=settings.llm_base_url,
        api_key=settings.llm_api_key,
        model=settings.llm_model,
        temperature=DEFAULT_TEMPERATURE,
        timeout=settings.llm_timeout,
        max_retries=0,
    )
