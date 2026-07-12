using System.Text.Json.Serialization;

namespace Backend.Api.Files;

/// <summary>文件清單項目。JSON:{ id, title, chunk_count, created_at, status }(snake_case)。</summary>
public sealed record DocumentInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("chunk_count")] int ChunkCount,
    [property: JsonPropertyName("created_at")] DateTime CreatedAt,
    [property: JsonPropertyName("status")] string Status);

/// <summary>
/// 從 RabbitMQ 消費的文件處理訊息。JSON camelCase:
/// { documentId, tenantId, userId, title, text }(與 platform 發佈者共用契約)。
/// </summary>
public sealed record DocumentMessage(
    string DocumentId, string TenantId, string UserId, string Title, string Text);
