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

    # Skill script 的 execution boundary（Phase S1）。false（預設）＝既有 in-process
    # path；true ＝短生命週期子行程 + OS 級 CPU/記憶體上限 + 空環境。平台若給不出可信
    # 的 OS 上限，開旗標後 script 步驟直接 fail closed，不會退回 in-process。
    isolated_skill_scripts_enabled: bool = False

    # kb_query 工作流的檢索參數。
    kb_query_top_k: int = 8                     # 檢索計畫預設取回筆數；requires_multi_doc 時節點內會加倍
    kb_query_max_retrieval_attempts: int = 2    # 檢索嘗試上限（含首次），防止驗證 RETRY 無限重試

    # D3 direct-Agent runtime is independently feature gated. PostgreSQL is
    # the only production checkpoint source; tests inject an in-memory saver.
    agent_test_run_enabled: bool = False
    # D7 is deliberately independent from the D3 direct-run switch.  Until it
    # is enabled, policy approval decisions remain fail-closed and no write
    # tool ever enters the effective runtime authority.
    agent_write_tools_enabled: bool = False
    agent_write_tools_allowlist: str = ""
    agent_write_tools_tenant_allowlist: str = ""
    multi_agent_dispatch_enabled: bool = False
    # P1 prompt composition artifacts。off（預設）＝ runtime 沿用 constants 組裝，輸出
    # byte-for-byte 不變；on 且 snapshot 帶 prompt_manifest pin ＝ SYSTEM GOVERNANCE 段
    # 改用 Backend pinned 的 governance_frame component（取不到／不符 pin 一律 fail closed）。
    # shadow 是 on 之下的 observe-only 子模式：兩種組成都算、只比 SHA 並記錄差異，實際
    # 送進 provider 的仍是 constants 版；shadow 關掉才真正切換。
    prompt_artifacts_enabled: bool = False
    prompt_artifacts_shadow: bool = False
    # E1 is independently fail-closed.  The Root composition also requires
    # multi_agent_dispatch_enabled, so this flag can never activate a second
    # runtime path on its own.
    context_enrichment_enabled: bool = False
    # Phase E2 versioned eval runner (POST /evals/run): fail-closed 404 while
    # disabled, matching the other internal runtime routes. Workflow is
    # stateless for eval — no suite catalog, no release state; suites arrive
    # per-request from Backend.
    run_eval_enabled: bool = False
    multi_agent_poll_interval_seconds: float = Field(default=0.25, gt=0, le=10)
    multi_agent_root_lease_seconds: int = Field(default=300, ge=30, le=300)
    multi_agent_heartbeat_seconds: float = Field(default=30, ge=5, le=120)
    multi_agent_context_top_k: int = Field(default=4, ge=1, le=20)
    checkpoint_database_url: str | None = None
    checkpoint_hmac_key: str = "agent-run-checkpoint-dev-key"
    runtime_lease_seconds: int = Field(default=30, ge=5, le=300)
    runtime_recovery_interval_seconds: float = Field(default=10.0, ge=1, le=300)
    runtime_recovery_batch_size: int = Field(default=20, ge=1, le=100)
    runtime_cancel_grace_seconds: float = Field(default=2.0, gt=0, le=30)
    runtime_default_timeout_seconds: int = Field(default=60, ge=1, le=600)
    runtime_default_step_budget: int = Field(default=24, ge=1, le=200)
    runtime_default_tool_rounds: int = Field(default=8, ge=1, le=50)
    runtime_default_context_rounds: int = Field(default=3, ge=1, le=20)
    runtime_default_token_budget: int = Field(default=16_000, ge=256, le=1_000_000)
    runtime_max_message_chars: int = Field(default=32_000, ge=256, le=200_000)
    runtime_model_context_tokens: int = Field(
        default=128_000, ge=1_024, le=2_000_000
    )
    runtime_model_output_reserve_tokens: int = Field(
        default=4_096, ge=1, le=1_000_000
    )

    @field_validator("checkpoint_hmac_key")
    @classmethod
    def validate_checkpoint_hmac_key(cls, value: str) -> str:
        if not value.strip():
            raise ValueError("CHECKPOINT_HMAC_KEY must not be blank")
        return value


settings = Settings()
