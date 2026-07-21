from pydantic import Field, field_validator
from pydantic_settings import BaseSettings


class Settings(BaseSettings):
    """集中管理環境變數設定；欄位名稱會自動對應到大寫的環境變數（如 LLM_BASE_URL）。"""

    llm_base_url: str = "http://localhost:4000"   # LiteLLM 閘道
    llm_api_key: str = "sk-1234"                  # LiteLLM 虛擬金鑰
    llm_model: str = "gpt-4o-mini"                # 可用 mock-gpt 無金鑰測試
    llm_timeout: float = 60.0                     # 單次 LLM 呼叫逾時（秒）；不設會讓慢/掛的模型無限等待
    llm_max_retries: int = 2                      # LLM 呼叫重試次數；預設全靠 langchain 內建重試上限，顯式收斂
    langfuse_enabled: bool = False

    # 服務間認證：platform 端（.NET，env 驅動）呼叫本服務時必須帶上相同的 X-Internal-Token 標頭；
    # 本服務呼叫 backend 的資料檢索 API 時，同一組 token 也當成出站憑證使用。
    internal_api_token: str = "internal-dev-token"

    @field_validator("internal_api_token")
    @classmethod
    def validate_internal_api_token(cls, value: str) -> str:
        """顯式空值不得關閉服務間信任邊界；未設環境變數仍沿用 dev 預設。"""
        if not value.strip():
            raise ValueError(
                "INTERNAL_API_TOKEN 不可為空字串(留空等於關閉服務間信任邊界);"
                "請設定非空 token 或移除該環境變數以使用 dev 預設。"
            )
        return value

    # 資料檢索：核心商業邏輯（含向量庫）已搬到 backend/，本服務只負責呼叫。
    backend_base_url: str = "http://localhost:8002"
    retrieval_top_k: int = Field(default=4, ge=1, le=50)   # 檢索節點預設取回的片段數；上限對齊 backend 的 [Range(1,50)]，超出會讓 backend 回 400

    # 工作流執行的逾時保護（秒），可由個別工作流的 timeout_seconds 覆蓋。
    workflow_timeout_seconds: int = 120

    # kb_query 工作流的檢索參數。
    kb_query_top_k: int = 8                     # 檢索計畫預設取回筆數；requires_multi_doc 時節點內會加倍
    kb_query_max_retrieval_attempts: int = 2    # 檢索嘗試上限（含首次），防止驗證 RETRY 無限重試


settings = Settings()
