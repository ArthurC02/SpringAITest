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
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IConnection? _connection;
    private IChannel? _channel;

    public RabbitDocumentQueue(RabbitMqOptions options) => _options = options;

    public async Task PublishAsync(DocumentMessage message, CancellationToken ct = default)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message, InternalRequest.Web);

        await _gate.WaitAsync(ct);
        try
        {
            var channel = await EnsureChannelAsync(ct);
            var props = new BasicProperties { Persistent = true };
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
