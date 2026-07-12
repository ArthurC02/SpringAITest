using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Backend.Api.Files;

/// <summary>
/// RabbitMQ 文件處理消費者。啟動時以退避重試連線(容忍 broker 尚未就緒),連上後宣告 durable 佇列、
/// prefetch 1、逐則交給 DocumentProcessor;處理完成(含已標 failed)後 ack。每則訊息以獨立 DI scope
/// 解析 scoped 的 DocumentProcessor/儲存庫。應用程式關閉時優雅釋放連線。
/// </summary>
public sealed class DocumentConsumerService : BackgroundService
{
    private const string QueueName = "documents.process";
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    // 與 platform 發佈者共用契約:Web 預設(camelCase、大小寫不敏感)。
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DocumentConsumerService> _logger;
    private readonly string _rabbitUrl;

    private IConnection? _connection;
    private IChannel? _channel;

    public DocumentConsumerService(
        IServiceScopeFactory scopeFactory, ILogger<DocumentConsumerService> logger, string rabbitUrl)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _rabbitUrl = rabbitUrl;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var factory = new ConnectionFactory { Uri = new Uri(_rabbitUrl) };
                _connection = await factory.CreateConnectionAsync(stoppingToken);
                _channel = await _connection.CreateChannelAsync(cancellationToken: stoppingToken);
                await _channel.QueueDeclareAsync(
                    queue: QueueName, durable: true, exclusive: false, autoDelete: false, arguments: null,
                    cancellationToken: stoppingToken);
                await _channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false, stoppingToken);

                var consumer = new AsyncEventingBasicConsumer(_channel);
                consumer.ReceivedAsync += OnReceivedAsync;
                await _channel.BasicConsumeAsync(QueueName, autoAck: false, consumer, stoppingToken);

                _logger.LogInformation("已連上 RabbitMQ,開始消費佇列 {Queue}", QueueName);
                return; // 訂閱成功;之後由自動復原機制維持連線,消費經由回呼進行。
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
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
        try
        {
            var message = JsonSerializer.Deserialize<DocumentMessage>(ea.Body.Span, JsonOpts);
            if (message is not null)
            {
                using var scope = _scopeFactory.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<DocumentProcessor>();
                await processor.ProcessAsync(message, CancellationToken.None);
            }
            else
            {
                _logger.LogWarning("收到無法解析為 DocumentMessage 的訊息,略過");
            }
        }
        catch (Exception ex)
        {
            // DocumentProcessor 自身不拋(失敗會標 failed);此處僅防未預期錯誤,仍 ack 避免無限重投。
            _logger.LogError(ex, "處理佇列訊息時發生未預期錯誤");
        }
        finally
        {
            if (_channel is { IsOpen: true })
            {
                await _channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
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
