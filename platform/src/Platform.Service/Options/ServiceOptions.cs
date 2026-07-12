namespace Platform.Service.Options;

/// <summary>
/// LLM(經 LiteLLM 閘道)相關設定。值在 Program.cs 由 flat 環境變數組裝,
/// 環境變數名稱(LLM_BASE_URL、LITELLM_KEY、CHAT_MODEL)不可變。
/// </summary>
public sealed class LlmOptions
{
    public string BaseUrl { get; set; } = "http://localhost:4000";
    public string ApiKey { get; set; } = "sk-1234";
    public string ChatModel { get; set; } = "gpt-4o-mini";

    /// <summary>取樣溫度,固定 0.7(與原 Java 一致)。</summary>
    public float Temperature { get; set; } = 0.7f;
}

/// <summary>mem0 長期記憶服務設定。對應環境變數 MEM0_BASE_URL。</summary>
public sealed class Mem0Options
{
    public string BaseUrl { get; set; } = "http://localhost:8000";
}

/// <summary>
/// 下游 Python 工作流服務設定。對應環境變數 WORKFLOW_BASE_URL、INTERNAL_API_TOKEN。
/// </summary>
public sealed class WorkflowOptions
{
    public string BaseUrl { get; set; } = "http://localhost:8001";

    /// <summary>每個下游請求都會帶的 X-Internal-Token 值。</summary>
    public string InternalToken { get; set; } = "internal-dev-token";
}

/// <summary>
/// 核心 backend 服務設定(認證/聊天歷史/文件/檢索/分析/組態)。
/// 對應環境變數 BACKEND_BASE_URL、INTERNAL_API_TOKEN。
/// </summary>
public sealed class BackendOptions
{
    public string BaseUrl { get; set; } = "http://localhost:8002";

    /// <summary>每個 backend 請求都會帶的 X-Internal-Token 值。</summary>
    public string InternalToken { get; set; } = "internal-dev-token";
}

/// <summary>
/// RabbitMQ 連線設定(文件處理訊息發佈)。對應環境變數 RABBITMQ_URL。
/// guest 帳號僅允許 loopback,容器間一律用自訂帳號(compose 以 RABBITMQ_DEFAULT_USER/PASS 建立)。
/// </summary>
public sealed class RabbitMqOptions
{
    public string Url { get; set; } = "amqp://app:app-dev-password@localhost:5672";
}
