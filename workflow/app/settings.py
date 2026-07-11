from pydantic_settings import BaseSettings


class Settings(BaseSettings):
    """集中管理環境變數設定；欄位名稱會自動對應到大寫的環境變數（如 LLM_BASE_URL）。"""

    llm_base_url: str = "http://localhost:4000"   # LiteLLM 閘道
    llm_api_key: str = "sk-1234"                  # LiteLLM 虛擬金鑰
    llm_model: str = "gpt-4o-mini"                # 可用 mock-gpt 無金鑰測試
    langfuse_enabled: bool = False

    # 服務間認證：Spring 端呼叫本服務時必須帶上相同的 X-Internal-Token 標頭。
    internal_api_token: str = "internal-dev-token"

    # RAG 向量檢索相關設定。
    database_url: str = ""                        # 空字串＝使用行程記憶體向量庫（本機開發／測試）
    embeddings_provider: str = "fake"              # "fake"（無需金鑰）或 "openai"
    embedding_model: str = "text-embedding-3-small"
    embedding_dim: int = 1536
    retrieval_top_k: int = 4                       # 檢索節點預設取回的片段數

    # 工作流執行的逾時保護（秒），可由個別工作流的 timeout_seconds 覆蓋。
    workflow_timeout_seconds: int = 120

    # /documents 端點（新增／列出／刪除）的逾時保護（秒）：
    # 嵌入呼叫或 DB pool 卡住時，避免 handler 無限阻塞。
    document_timeout_seconds: int = 60


settings = Settings()
