namespace Platform.Service.Options;

/// <summary>
/// LLM(經 LiteLLM 閘道)相關設定。值一律由 Program.cs 從 flat 環境變數組裝並提供 fallback
/// (LLM_BASE_URL、LITELLM_KEY、CHAT_MODEL);此處不設字面預設,避免與 Program.cs 的 fallback 漂移。
/// </summary>
public sealed class LlmOptions
{
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string ChatModel { get; set; } = "";

    /// <summary>取樣溫度;實際值由 Program.cs 設定。</summary>
    public float Temperature { get; set; }
}

/// <summary>mem0 長期記憶服務設定。對應環境變數 MEM0_BASE_URL(由 Program.cs 提供 fallback)。</summary>
public sealed class Mem0Options
{
    public string BaseUrl { get; set; } = "";
}

/// <summary>
/// 下游 Python 工作流服務設定。對應環境變數 WORKFLOW_BASE_URL、INTERNAL_API_TOKEN(由 Program.cs 提供 fallback)。
/// </summary>
public sealed class WorkflowOptions
{
    public string BaseUrl { get; set; } = "";

    /// <summary>每個下游請求都會帶的 X-Internal-Token 值。</summary>
    public string InternalToken { get; set; } = "";
}

/// <summary>
/// 核心 backend 服務設定(認證/聊天歷史/文件/檢索/分析/組態)。
/// 對應環境變數 BACKEND_BASE_URL、INTERNAL_API_TOKEN(由 Program.cs 提供 fallback)。
/// </summary>
public sealed class BackendOptions
{
    public string BaseUrl { get; set; } = "";

    /// <summary>每個 backend 請求都會帶的 X-Internal-Token 值。</summary>
    public string InternalToken { get; set; } = "";
}

/// <summary>D6 production-chat canary. Both switches are server-owned; callers cannot opt a
/// tenant into the Root Orchestrator path from a request body.</summary>
public sealed class AgentChatOptions
{
    public bool Enabled { get; init; }
    public IReadOnlySet<string> TenantAllowlist { get; init; } =
        new HashSet<string>(StringComparer.Ordinal);

    public bool IsCanaryTenant(string tenant) =>
        Enabled && TenantAllowlist.Contains(tenant);
}

/// <summary>
/// RabbitMQ 連線設定(文件處理訊息發佈)。對應環境變數 RABBITMQ_URL(由 Program.cs 提供 fallback)。
/// guest 帳號僅允許 loopback,容器間一律用自訂帳號(compose 以 RABBITMQ_DEFAULT_USER/PASS 建立)。
/// </summary>
public sealed class RabbitMqOptions
{
    public string Url { get; set; } = "";
}
