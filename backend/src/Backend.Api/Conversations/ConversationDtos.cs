using Backend.Api.Common;

namespace Backend.Api.Conversations;

/// <summary>建立對話紀錄的請求 body:{ prompt, reply }。</summary>
public sealed record ConversationCreateRequest(
    [NotBlank(ErrorMessage = "prompt 不可為空")]
    string? Prompt,

    [NotBlank(ErrorMessage = "reply 不可為空")]
    string? Reply);

/// <summary>建立成功的回應。JSON:{ id, createdAt(UTC ISO-8601 Z) }。</summary>
public sealed record ConversationCreated(long Id, DateTime CreatedAt);

/// <summary>歷史清單項目。JSON:{ id, reply, createdAt };刻意沒有 prompt。</summary>
public sealed record ConversationItem(long Id, string Reply, DateTime CreatedAt);

/// <summary>Keyset page response. nextCursor is opaque and bound to the requesting identity.</summary>
public sealed record ConversationPage(
    IReadOnlyList<ConversationItem> Items,
    string? NextCursor,
    bool HasMore);

/// <summary>Repository-only keyset position; caller identity is supplied separately.</summary>
public sealed record ConversationPosition(DateTime CreatedAt, long Id);
