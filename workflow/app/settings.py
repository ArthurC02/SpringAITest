from pydantic import Field
from pydantic_settings import BaseSettings


class Settings(BaseSettings):
    """集中管理環境變數設定；欄位名稱會自動對應到大寫的環境變數（如 LLM_BASE_URL）。"""

    llm_base_url: str = "http://localhost:4000"   # LiteLLM 閘道
    llm_api_key: str = "sk-1234"                  # LiteLLM 虛擬金鑰
    llm_model: str = "gpt-4o-mini"                # 可用 mock-gpt 無金鑰測試
    langfuse_enabled: bool = False

    # 服務間認證：Spring 端呼叫本服務時必須帶上相同的 X-Internal-Token 標頭；
    # 本服務呼叫 backend 的資料檢索 API 時，同一組 token 也當成出站憑證使用。
    internal_api_token: str = "internal-dev-token"

    # 資料檢索：核心商業邏輯（含向量庫）已搬到 backend/，本服務只負責呼叫。
    backend_base_url: str = "http://localhost:8002"
    retrieval_top_k: int = Field(default=4, ge=1, le=50)   # 檢索節點預設取回的片段數；上限對齊 backend 的 [Range(1,50)]，超出會讓 backend 回 400

    # 工作流執行的逾時保護（秒），可由個別工作流的 timeout_seconds 覆蓋。
    workflow_timeout_seconds: int = 120


settings = Settings()
