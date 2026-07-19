using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Platform.Service.Validation;

namespace Platform.Service.Dtos;

/// <summary>建立文件的請求 body:{ "title", "text" }。</summary>
public sealed record DocumentCreateRequest(
    [NotBlank(ErrorMessage = "title 不可為空")]
    [StringLength(500, ErrorMessage = "title 長度不可超過 500 字")]
    string? Title,

    [NotBlank(ErrorMessage = "text 不可為空")]
    [StringLength(1_000_000, ErrorMessage = "text 長度不可超過 1000000 字")]
    string? Text);

/// <summary>建立文件已受理的回應(非同步處理)。JSON:{ id, title, status }(camelCase)。</summary>
public sealed record DocumentAccepted(string Id, string Title, string Status);

/// <summary>文件清單項目。JSON:{ id, title, chunk_count, created_at, status }(snake_case);
/// created_at 是字串,status 由 backend GET 回傳,皆原樣轉發下游值。</summary>
public sealed record DocumentInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("chunk_count")] int ChunkCount,
    [property: JsonPropertyName("created_at")] string CreatedAt,
    [property: JsonPropertyName("status")] string Status);

/// <summary>
/// 發佈到 RabbitMQ 的文件處理訊息。JSON camelCase:
/// { documentId, tenantId, userId, title, text }(與 backend 消費者共用契約)。
/// </summary>
public sealed record DocumentMessage(
    string DocumentId, string TenantId, string UserId, string Title, string Text);
