from functools import lru_cache

from langchain_openai import ChatOpenAI

from app.settings import settings


@lru_cache(maxsize=1)
def get_llm() -> ChatOpenAI:
    """取得共用的 LLM 客戶端（單例快取，避免每次呼叫都重新建立連線設定）。

    一律透過既有的 LiteLLM 閘道存取模型，因此 base_url / api_key 皆指向 LiteLLM，
    實際使用的模型名稱由 LLM_MODEL 環境變數決定（測試時可指向 mock-gpt）。
    """
    return ChatOpenAI(
        base_url=settings.llm_base_url,
        api_key=settings.llm_api_key,
        model=settings.llm_model,
        temperature=0.7,
    )
