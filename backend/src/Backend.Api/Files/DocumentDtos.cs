using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Backend.Api.Files;

/// <summary>
/// 文件清單項目。JSON:{ id, title, chunk_count, created_at, status, failure_reason }(snake_case)。
/// <c>failure_reason</c> 只在 status='failed' 且該次失敗有分類時有值,其餘一律 null
/// (含既有的 failed 舊列)—— 前端必須容忍 null,照常顯示「失敗」。
/// </summary>
public sealed record DocumentInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("chunk_count")] int ChunkCount,
    [property: JsonPropertyName("created_at")] DateTime CreatedAt,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("failure_reason")] string? FailureReason = null);

/// <summary>
/// 文件處理失敗原因的**封閉集合**。這個欄位會原樣回給瀏覽器,所以只能是這裡的固定字串:
/// 絕不得放原始例外訊息、stack trace、provider/LLM 回傳內容、檔案內容或連線字串。
/// 新增分類就在這裡加一個常數並在 <see cref="Classify"/> 補一條映射,不要在呼叫端組字串。
/// </summary>
public static class DocumentFailureReasons
{
    public const string Timeout = "處理逾時";
    public const string EmbeddingUnavailable = "產生向量時發生問題，請稍後再試";
    public const string Unexpected = "處理時發生未預期的問題";

    /// <summary>
    /// 例外 → 分類。分類邏輯與 <c>DocumentProcessor.IsTransient</c> 的等價類一致(逾時/傳輸/其他),
    /// 只是這裡走到的都是**已判定為終態**的例外(非暫時性,或暫時性但重試已用盡)。
    /// 落在集合外的一律 <see cref="Unexpected"/> —— fail closed,不外洩任何例外文字。
    /// </summary>
    public static string Classify(Exception ex) => ex switch
    {
        TimeoutException or TaskCanceledException => Timeout,
        HttpRequestException => EmbeddingUnavailable,
        _ => Unexpected,
    };
}

/// <summary>
/// 從 RabbitMQ 消費的文件處理訊息。JSON camelCase:
/// { documentId, tenantId, userId, title, text, correlationId }(與 platform 發佈者共用契約)。
/// <c>correlationId</c> 為選填:滾動部署期間佇列裡會有舊版沒有這個欄位的訊息,缺它必須照常處理完成
/// (只是該則訊息的日誌沒有追蹤編號),絕不能因此 nack 或轉進死信佇列。
/// </summary>
public sealed record DocumentMessage(
    string DocumentId, string TenantId, string UserId, string Title, string Text,
    string? CorrelationId = null);

/// <summary>
/// 文件消費者的 broker 連線狀態,由 <see cref="DocumentConsumerService"/> 自行回報(零額外探測連線),
/// 供 readiness **顯示**用。慣例比照 <c>AgentTriggersState</c>:一個單例狀態物件,不是服務。
///
/// <para><see cref="Active"/> 表示此進程真的啟動了 consumer。沒啟動(Testing、lite 部署)時
/// readiness 完全不出現 document_consumer 元件 —— 「沒有 consumer」不可以看起來像「consumer 壞了」。</para>
///
/// <para>元件以 <c>Required: false</c> 呈報,**不影響** 200/503:broker 短暫抖動若把 backend 判成
/// not ready,編排器會重啟 backend,對停滯中的文件處理毫無幫助,只是多製造一次不穩定。目標是讓監控
/// 看得見,不是讓 backend 死掉。要升級成 gating 是一行:把該元件的 Required 改成 true —— 那是獨立的
/// 維運決策,不在這裡預設。</para>
/// </summary>
public sealed class DocumentConsumerState
{
    private volatile bool _active;
    private volatile bool _connected;

    public bool Active
    {
        get => _active;
        set => _active = value;
    }

    public bool Connected
    {
        get => _connected;
        set => _connected = value;
    }
}

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
