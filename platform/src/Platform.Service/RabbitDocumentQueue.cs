using System.Text.Json;
using Platform.Service.Abstractions;
using Platform.Service.Dtos;
using Platform.Service.Options;
using RabbitMQ.Client;

namespace Platform.Service;

/// <summary>
/// RabbitMQ 文件處理訊息發佈者。lazy 單例連線(執行緒安全):首次發佈時建立連線+channel、
/// 宣告 durable 佇列;之後重用。訊息 persistent。發佈以 SemaphoreSlim 串行化
/// (RabbitMQ.Client 的單一 channel 不支援併發發佈;文件上傳吞吐量低,足夠)。
/// 應用程式關閉時由 DI 呼叫 DisposeAsync 優雅釋放。
///
/// ponytail: 全域鎖串行發佈,若吞吐量成為瓶頸再改成 channel pool。
/// </summary>
public sealed class RabbitDocumentQueue : IDocumentQueue, IAsyncDisposable
{
    /// <summary>文件處理佇列名稱(durable)。</summary>
    public const string QueueName = "documents.process";

    private readonly RabbitMqOptions _options;
    private readonly Func<string?> _correlationId;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IConnection? _connection;
    private IChannel? _channel;

    /// <param name="correlationId">
    /// 目前請求的追蹤編號來源。Web 層以 <c>HttpContext.TraceIdentifier</c> 供應(與
    /// <c>X-Correlation-Id</c> 轉發的是同一個值);Platform.Service 因此不必相依 ASP.NET Core。
    /// 省略時一律無編號(背景工作/測試),不造假值。
    /// </param>
    public RabbitDocumentQueue(RabbitMqOptions options, Func<string?>? correlationId = null)
    {
        _options = options;
        _correlationId = correlationId ?? (() => null);
    }

    /// <summary>
    /// 訊息本體與 AMQP 屬性。追蹤編號**兩處都寫**是刻意的:body 欄位供跨版本相容(消費端優先讀它),
    /// AMQP 內建的 CorrelationId 屬性則讓 broker 管理介面與現成工具不必解析 payload 就看得到。
    /// </summary>
    internal static (byte[] Body, BasicProperties Props) Frame(DocumentMessage message, string? correlationId)
    {
        var id = string.IsNullOrWhiteSpace(correlationId) ? null : correlationId;
        return (
            JsonSerializer.SerializeToUtf8Bytes(message with { CorrelationId = id }, InternalRequest.Web),
            new BasicProperties { Persistent = true, CorrelationId = id });
    }

    public async Task PublishAsync(DocumentMessage message, CancellationToken ct = default)
    {
        var (body, props) = Frame(message, _correlationId());

        await _gate.WaitAsync(ct);
        try
        {
            var channel = await EnsureChannelAsync(ct);
            await channel.BasicPublishAsync(
                exchange: string.Empty,
                routingKey: QueueName,
                mandatory: false,
                basicProperties: props,
                body: body,
                cancellationToken: ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IChannel> EnsureChannelAsync(CancellationToken ct)
    {
        if (_channel is { IsOpen: true })
        {
            return _channel;
        }

        try
        {
            var factory = new ConnectionFactory { Uri = new Uri(_options.Url) };
            _connection = await factory.CreateConnectionAsync(ct);
            // publisher confirmations 開啟後,BasicPublishAsync 會等 broker ack;被 nack 即拋例外
            // (走 DocumentService 的 502 映射),訊息不會在 client 崩潰/斷線時靜默遺失。
            _channel = await _connection.CreateChannelAsync(
                new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
                ct);
            await _channel.QueueDeclareAsync(
                queue: QueueName, durable: true, exclusive: false, autoDelete: false, arguments: null,
                cancellationToken: ct);
            return _channel;
        }
        catch
        {
            // 部分連上(如 CreateConnection 成功、CreateChannel 失敗)時,dispose 並清空避免下次重入洩漏舊連線。
            if (_channel is not null)
            {
                await _channel.DisposeAsync();
                _channel = null;
            }

            if (_connection is not null)
            {
                await _connection.DisposeAsync();
                _connection = null;
            }

            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
        {
            await _channel.DisposeAsync();
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        _gate.Dispose();
    }
}
