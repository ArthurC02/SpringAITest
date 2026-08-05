using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
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

/// <summary>Platform 在 publish 前配置文件 identity 的內部請求。</summary>
public sealed record DocumentIngestIntentRequest(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("text")] string? Text);

/// <summary>內部配置結果；pending_publish 對 producer 呈現為 processing。</summary>
public sealed record DocumentIngestIntentResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("status")] string Status);

public enum DocumentIngestAllocationStatus
{
    Created,
    Replay,
    PayloadConflict,
    DeletedConflict,
}

public sealed record DocumentIngestAllocation(
    DocumentIngestAllocationStatus Status,
    string DocumentId,
    string Title);

/// <summary>
/// Idempotency identity is fixed-width SHA-256 only. The length-prefixed UTF-8 request frame is
/// unambiguous without persisting either the raw key or document text in document_ingest.
/// </summary>
public static class DocumentIngestIdentity
{
    public static string HashKey(string key) => Sha256(Encoding.UTF8.GetBytes(key));

    public static string HashRequest(string title, string text)
    {
        var titleBytes = Encoding.UTF8.GetBytes(title);
        var textBytes = Encoding.UTF8.GetBytes(text);
        var frame = new byte[sizeof(int) + titleBytes.Length + sizeof(int) + textBytes.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, titleBytes.Length);
        titleBytes.CopyTo(frame.AsSpan(sizeof(int)));
        var textLengthOffset = sizeof(int) + titleBytes.Length;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(textLengthOffset), textBytes.Length);
        textBytes.CopyTo(frame.AsSpan(textLengthOffset + sizeof(int)));
        return Sha256(frame);
    }

    private static string Sha256(ReadOnlySpan<byte> value) => Convert.ToHexStringLower(SHA256.HashData(value));
}
