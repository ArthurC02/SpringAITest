using System.Globalization;
using System.Text;
using System.Text.Json;
using Backend.Api.Common;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace Backend.Api.Files;

/// <summary>
/// RabbitMQ 文件處理消費者。啟動時以退避重試連線(容忍 broker 尚未就緒),連上後宣告 durable 佇列、
/// prefetch 1、逐則交給 DocumentProcessor。依處理結果決定:成功 ack;暫時性失敗且未達重試上限,
/// 以 message header 記錄的重試次數重新發佈回原佇列(ack 舊訊息 —— 已被等價複本取代,非遺失);
/// 終態失敗(含 payload 無法解析、重試已達上限)一律搬進獨立死信佇列 <see cref="DeadLetterQueueName"/>
/// 再 ack,絕不無條件 ack 讓失敗訊息憑空消失。每則訊息以獨立 DI scope 解析 scoped 的
/// DocumentProcessor/儲存庫。應用程式關閉時優雅釋放連線。
///
/// ponytail: 死信佇列採「終態時手動 publish 到獨立佇列」而非 RabbitMQ 原生
/// x-dead-letter-exchange 佇列參數 —— documents.process 是既有 durable 佇列(見 infra 的
/// rabbitmq_data volume,跨重啟存活),且發佈端(platform RabbitDocumentQueue)以 arguments:null
/// 宣告、不可變動;對已存在佇列改宣告參數會導致 PRECONDITION_FAILED(406)炸掉 channel。
/// 手動 publish 到全新獨立佇列不改動 documents.process 的宣告,零相容性風險。
/// 同理,bounded retry 也不依賴原生 nack+requeue(無法附帶遞增計數),改用 header 手動追蹤。
/// </summary>
public sealed class DocumentConsumerService : BackgroundService
{
    internal const string QueueName = "documents.process";
    internal const string DeadLetterQueueName = "documents.process.dlq";
    internal const string RetryCountHeader = "x-retry-count";

    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    // 與 platform 發佈者共用契約:Web 預設(camelCase、大小寫不敏感)。
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DocumentConsumerService> _logger;
    private readonly string _rabbitUrl;
    private readonly DocumentConsumerState _state;

    private IConnection? _connection;
    private IChannel? _channel;
    // Retry backoff (RequeueWithRetryAsync) waits on this, not CancellationToken.None: without it a
    // shutdown blocks channel/connection dispose for up to MaxRetries*RetryDelay (15s), past Docker's
    // default 10s SIGKILL grace.
    private CancellationToken _stoppingToken;

    public DocumentConsumerService(
        IServiceScopeFactory scopeFactory,
        ILogger<DocumentConsumerService> logger,
        string rabbitUrl,
        DocumentConsumerState? state = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _rabbitUrl = rabbitUrl;
        _state = state ?? new DocumentConsumerState();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        // 這個進程真的有 consumer,readiness 從現在起才會呈報 document_consumer 元件。
        _state.Active = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var factory = new ConnectionFactory { Uri = new Uri(_rabbitUrl) };
                _connection = await factory.CreateConnectionAsync(stoppingToken);
                // Publisher confirms: requeue/DLQ publishes below await broker ack, so a
                // broker-crash window can never silently lose a message that already left the
                // socket (mirrors platform's RabbitDocumentQueue publisher).
                _channel = await _connection.CreateChannelAsync(
                    new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
                    stoppingToken);
                await _channel.QueueDeclareAsync(
                    queue: QueueName, durable: true, exclusive: false, autoDelete: false, arguments: null,
                    cancellationToken: stoppingToken);
                await _channel.QueueDeclareAsync(
                    queue: DeadLetterQueueName, durable: true, exclusive: false, autoDelete: false, arguments: null,
                    cancellationToken: stoppingToken);
                await _channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false, stoppingToken);

                var consumer = new AsyncEventingBasicConsumer(_channel);
                consumer.ReceivedAsync += OnReceivedAsync;
                await _channel.BasicConsumeAsync(QueueName, autoAck: false, consumer, stoppingToken);

                // 連線狀態由既有連線自己回報,不另開探測連線。之後由自動復原機制維持連線,
                // 斷開/復原都靠下面兩個回呼翻轉旗標。
                _connection.ConnectionShutdownAsync += OnConnectionShutdownAsync;
                _connection.RecoverySucceededAsync += OnRecoverySucceededAsync;
                _state.Connected = true;

                _logger.LogInformation("已連上 RabbitMQ,開始消費佇列 {Queue}", QueueName);
                return; // 訂閱成功;之後由自動復原機制維持連線,消費經由回呼進行。
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _state.Connected = false;
                return;
            }
            catch (Exception ex)
            {
                _state.Connected = false;
                _logger.LogWarning(ex, "連線 RabbitMQ 失敗,{Seconds} 秒後重試", RetryDelay.TotalSeconds);
                try
                {
                    await Task.Delay(RetryDelay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task OnReceivedAsync(object sender, BasicDeliverEventArgs ea)
    {
        DocumentMessage? message = null;
        JsonException? parseError = null;
        try
        {
            message = JsonSerializer.Deserialize<DocumentMessage>(ea.Body.Span, JsonOpts);
        }
        catch (JsonException ex)
        {
            parseError = ex;
        }

        // 追蹤編號的 scope 包住這則訊息的**整段**處理(含死信/重試分支),否則失敗那一行日誌正好是
        // operator 拿著編號來找的那一行。解析失敗時仍有 AMQP 屬性可用,所以 scope 開在解析之後、
        // 所有分支之前。
        using var correlationScope = BeginCorrelationScope(
            _logger, ResolveCorrelationId(message, ea.BasicProperties));

        if (parseError is not null)
        {
            // poison payload:無法解析就無從得知 documentId,不重試 —— 搬進死信佇列供人工檢視。
            _logger.LogError(parseError, "收到無法解析的訊息(poison payload),轉入死信佇列 {Queue}", DeadLetterQueueName);
            await DeadLetterAsync(ea);
            await AckAsync(ea);
            return;
        }

        if (message is null)
        {
            _logger.LogWarning("收到無法解析為 DocumentMessage 的訊息(內容為 null),轉入死信佇列 {Queue}", DeadLetterQueueName);
            await DeadLetterAsync(ea);
            await AckAsync(ea);
            return;
        }

        try
        {
            var retryCount = GetRetryCount(ea.BasicProperties, _logger);
            using var scope = _scopeFactory.CreateScope();
            var processor = scope.ServiceProvider.GetRequiredService<DocumentProcessor>();
            var outcome = await processor.ProcessAsync(message, retryCount, CancellationToken.None);

            switch (outcome)
            {
                case DocumentProcessingOutcome.Success:
                    await AckAsync(ea);
                    break;
                case DocumentProcessingOutcome.RetryableFailure:
                    if (await RequeueWithRetryAsync(ea, retryCount + 1))
                    {
                        await AckAsync(ea); // 原訊息已被等價複本取代並重新入列,非遺失。
                    }
                    break;
                default: // TerminalFailure
                    await DeadLetterAsync(ea);
                    await AckAsync(ea);
                    break;
            }
        }
        catch (Exception ex) when (ex is AlreadyClosedException || _channel is not { IsOpen: true })
        {
            // 服務關機中(channel 已關閉或正在關閉):底下的 DeadLetter/Ack 發布動作只會靜默 no-op,
            // 絕不能宣稱已轉入死信佇列。原訊息維持未確認,broker 會在重新連線後重新投遞給下一個
            // 消費者,不是遺失。
            _logger.LogWarning(ex, "處理佇列訊息時 channel 已關閉(服務關機中),本則訊息未確認,將由 broker 重新投遞;若死信轉發已先完成,DLQ 可能出現重複複本");
        }
        catch (Exception ex)
        {
            // DocumentProcessor 自身不拋(結果一律回傳 outcome);此處僅防未預期錯誤(如 DI 解析失敗)。
            // 即便如此仍不得無條件 ack 吞掉 —— 轉入死信佇列保留稽核痕跡。
            _logger.LogError(ex, "處理佇列訊息時發生未預期錯誤,轉入死信佇列 {Queue}", DeadLetterQueueName);
            await DeadLetterAsync(ea);
            await AckAsync(ea);
        }
    }

    private Task OnConnectionShutdownAsync(object? sender, ShutdownEventArgs args)
    {
        _state.Connected = false;
        return Task.CompletedTask;
    }

    private Task OnRecoverySucceededAsync(object? sender, AsyncEventArgs args)
    {
        _state.Connected = true;
        return Task.CompletedTask;
    }

    /// <summary>
    /// 這則訊息的追蹤編號:優先 body 欄位(跨版本相容的權威來源),缺才退到 AMQP 內建的
    /// CorrelationId 屬性。邊界檢查沿用 header 那條路徑的同一組規則
    /// (<see cref="IdentityHeaders.BoundedValue"/>:trim 後 1..128 字元、無控制字元),
    /// 未通過一律當作沒帶 —— 未消毒的字串絕不進日誌。
    /// </summary>
    internal static string? ResolveCorrelationId(DocumentMessage? message, IReadOnlyBasicProperties props)
        => IdentityHeaders.BoundedValue(message?.CorrelationId)
           ?? IdentityHeaders.BoundedValue(props.CorrelationId);

    /// <summary>沒有(或不合格的)追蹤編號就不開 scope —— 降級,不是錯誤。</summary>
    internal static IDisposable? BeginCorrelationScope(ILogger logger, string? correlationId)
        => correlationId is null
            ? null
            : logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });

    private async Task AckAsync(BasicDeliverEventArgs ea)
    {
        if (_channel is { IsOpen: true })
        {
            await _channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
        }
    }

    /// <summary>把原始訊息(位元組與 header 原樣保留)手動搬到死信佇列。</summary>
    private async Task DeadLetterAsync(BasicDeliverEventArgs ea)
    {
        if (_channel is not { IsOpen: true })
        {
            return;
        }

        await _channel.BasicPublishAsync(
            exchange: string.Empty,
            routingKey: DeadLetterQueueName,
            mandatory: false,
            basicProperties: new BasicProperties(ea.BasicProperties),
            body: ea.Body,
            cancellationToken: CancellationToken.None);
    }

    /// <summary>重新發佈同一則訊息回原佇列,header 帶上遞增後的重試次數。回傳是否真的已發佈 ——
    /// 呼叫端只在 true 時才 ack 原訊息;退避等待途中若服務關機(見 <see cref="_stoppingToken"/>)則
    /// 直接回 false、不發佈也不 ack,原訊息保持未確認,由 broker 重新投遞,不遺失。</summary>
    private async Task<bool> RequeueWithRetryAsync(BasicDeliverEventArgs ea, int nextRetryCount)
    {
        if (_channel is not { IsOpen: true })
        {
            return false;
        }

        var props = new BasicProperties(ea.BasicProperties) { Persistent = true };
        var headers = new Dictionary<string, object?>();
        if (props.Headers is not null)
        {
            foreach (var kv in props.Headers)
            {
                headers[kv.Key] = kv.Value;
            }
        }

        headers[RetryCountHeader] = nextRetryCount;
        props.Headers = headers;

        // ponytail: linear backoff (retryCount * 5s), blocking this single-message consumer
        // (prefetch=1) while it waits -- acceptable ceiling for a 3-attempt cap. Upgrade path if a
        // real outage needs longer backoff without blocking the consumer: native TTL+DLX requeue
        // (a per-retry delay queue) instead of hand-rolled Task.Delay. Waits on _stoppingToken so
        // shutdown does not block channel/connection dispose for up to MaxRetries*RetryDelay.
        try
        {
            await Task.Delay(RetryDelay * nextRetryCount, _stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        await _channel.BasicPublishAsync(
            exchange: string.Empty,
            routingKey: QueueName,
            mandatory: false,
            basicProperties: props,
            body: ea.Body,
            cancellationToken: CancellationToken.None);
        return true;
    }

    /// <summary>從訊息 header 讀取目前已重試次數(未帶 header 視為首次投遞,回 0)。無法辨識的
    /// header 型別 fail closed 視為已達重試上限(轉 DLQ),不是 fail open 回 0(可能無限重試)。</summary>
    internal static int GetRetryCount(IReadOnlyBasicProperties props, ILogger? logger = null)
    {
        if (props.Headers is not { } headers || !headers.TryGetValue(RetryCountHeader, out var raw))
        {
            return 0;
        }

        return raw switch
        {
            int i => i,
            long l => (int)l,
            byte[] b when int.TryParse(Encoding.UTF8.GetString(b), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
            _ => LogAndFailClosed(logger, raw),
        };

        static int LogAndFailClosed(ILogger? logger, object? raw)
        {
            logger?.LogWarning("x-retry-count header 型別無法解析（{Type}），視為已達重試上限", raw?.GetType().Name ?? "null");
            return DocumentProcessor.MaxRetries;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        _state.Connected = false;
        if (_channel is not null)
        {
            await _channel.DisposeAsync();
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }
    }
}
