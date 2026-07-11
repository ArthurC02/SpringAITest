from functools import lru_cache

from langchain_core.embeddings import DeterministicFakeEmbedding, Embeddings
from langchain_openai import OpenAIEmbeddings

from app.settings import settings


@lru_cache(maxsize=1)
def get_embeddings() -> Embeddings:
    """取得共用的嵌入模型客戶端（單例快取）。

    - "fake"：使用 langchain_core 內建的 DeterministicFakeEmbedding，
      依文字內容的雜湊決定亂數種子，因此同一段文字永遠得到相同向量，
      不需連網、不需金鑰，適合本機開發與測試。
    - "openai"：透過既有的 LiteLLM 閘道呼叫真正的嵌入模型。
    """
    if settings.embeddings_provider == "fake":
        return DeterministicFakeEmbedding(size=settings.embedding_dim)

    return OpenAIEmbeddings(
        base_url=settings.llm_base_url,
        api_key=settings.llm_api_key,
        model=settings.embedding_model,
    )
