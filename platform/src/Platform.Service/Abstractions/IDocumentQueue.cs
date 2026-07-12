using Platform.Service.Dtos;

namespace Platform.Service.Abstractions;

/// <summary>文件處理訊息佇列(發佈端)。實作發佈到 RabbitMQ;測試以 fake 取代。</summary>
public interface IDocumentQueue
{
    /// <summary>發佈一則文件處理訊息(durable 佇列、persistent 訊息)。失敗直接拋出,由呼叫端映射對外錯誤。</summary>
    Task PublishAsync(DocumentMessage message, CancellationToken ct = default);
}
