using System.ComponentModel.DataAnnotations;
using Platform.Service.Validation;

namespace Platform.Service.Dtos;

/// <summary>聊天請求(阻塞與串流共用同一驗證)。</summary>
public sealed record ChatRequest(
    [NotBlank(ErrorMessage = "message 不可為空")]
    [StringLength(4000, ErrorMessage = "message 長度不可超過 4000 字")]
    string? Message,

    // 選填:缺少或空白時由 Platform 產生 UUID。
    [StringLength(128, ErrorMessage = "conversationId 長度不可超過 128 字")]
    [RegularExpression(@"^(?:\s*|[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12})$",
        ErrorMessage = "conversationId 必須是有效 UUID")]
    string? ConversationId,

    Guid? OrchestratorId = null);

/// <summary>聊天回應。JSON:{ id, reply, createdAt };刻意沒有 prompt 欄位。
/// CreatedAt 為 UTC(Kind=Utc),序列化自然帶結尾 Z。</summary>
public sealed record ChatResponse(long Id, string Reply, DateTime CreatedAt);

/// <summary>Additive keyset-paginated history response.</summary>
public sealed record ChatHistoryPage(
    IReadOnlyList<ChatResponse> Items,
    string? NextCursor,
    bool HasMore);

/// <summary>Server-derived D6 lineage attached to the one existing conversation write.</summary>
public sealed record ChatTurnMetadata(
    Guid OrchestratorId,
    int OrchestratorRevision,
    Guid WorkflowId,
    int WorkflowRevision,
    Guid RootRunId);
