using System.Text;
using System.Text.Json;
using Backend.Api.Files;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;

namespace Backend.Api.Tests;

/// <summary>
/// DocumentConsumerService 的 message-header 重試次數解析(純函式,internal + InternalsVisibleTo)。
/// 其餘廣播/佇列機制(ack/nack/DLQ publish)需要真實 broker,交由 e2e 全鏈路驗證。
/// </summary>
public sealed class DocumentConsumerServiceTests
{
    [Fact]
    public void GetRetryCount_NoHeader_ReturnsZero()
    {
        var props = new BasicProperties();

        Assert.Equal(0, DocumentConsumerService.GetRetryCount(props));
    }

    [Fact]
    public void GetRetryCount_HeaderPresent_ReturnsStoredValue()
    {
        var props = new BasicProperties
        {
            Headers = new Dictionary<string, object?> { [DocumentConsumerService.RetryCountHeader] = 2 },
        };

        Assert.Equal(2, DocumentConsumerService.GetRetryCount(props));
    }

    [Fact]
    public void GetRetryCount_LongHeader_ReturnsStoredValue()
    {
        // An AMQP table can hand the same integer back as long instead of the int we write on requeue.
        var props = new BasicProperties
        {
            Headers = new Dictionary<string, object?> { [DocumentConsumerService.RetryCountHeader] = 2L },
        };

        Assert.Equal(2, DocumentConsumerService.GetRetryCount(props));
    }

    [Fact]
    public void GetRetryCount_ByteArrayHeader_ParsesUtf8IntegerText()
    {
        // RabbitMQ.Client commonly surfaces AMQP table string values as byte[] on redelivery.
        var props = new BasicProperties
        {
            Headers = new Dictionary<string, object?>
            {
                [DocumentConsumerService.RetryCountHeader] = Encoding.UTF8.GetBytes("2"),
            },
        };

        Assert.Equal(2, DocumentConsumerService.GetRetryCount(props));
    }

    [Fact]
    public void GetRetryCount_UnparseableByteArrayHeader_FailsClosedToMaxRetries()
    {
        // byte[] that is not integer text falls through the TryParse guard into the same fail-closed
        // default as an unrecognized CLR type -- still DLQ, never "first delivery".
        var props = new BasicProperties
        {
            Headers = new Dictionary<string, object?>
            {
                [DocumentConsumerService.RetryCountHeader] = Encoding.UTF8.GetBytes("not-a-number"),
            },
        };

        Assert.Equal(DocumentProcessor.MaxRetries, DocumentConsumerService.GetRetryCount(props));
    }

    [Fact]
    public void GetRetryCount_UnrecognizedHeaderType_FailsClosedToMaxRetries()
    {
        // An unparseable header type must never be treated as "first delivery" (0) -- that would
        // let a poison message retry forever instead of hitting the DLQ.
        var props = new BasicProperties
        {
            Headers = new Dictionary<string, object?> { [DocumentConsumerService.RetryCountHeader] = 3.14 },
        };

        Assert.Equal(DocumentProcessor.MaxRetries, DocumentConsumerService.GetRetryCount(props));
    }

    // 與 platform 發佈者共用的解析器(Web 預設:camelCase、大小寫不敏感)。
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private static DocumentMessage Parse(string json)
        => JsonSerializer.Deserialize<DocumentMessage>(json, JsonOpts)!;

    [Fact]
    public void ResolveCorrelationId_PrefersTheBodyField_OverTheAmqpProperty()
    {
        var message = Parse(
            """{"documentId":"d1","tenantId":"t","userId":"u","title":"標題","text":"內容","correlationId":"from-body"}""");
        var props = new BasicProperties { CorrelationId = "from-amqp" };

        Assert.Equal("from-body", DocumentConsumerService.ResolveCorrelationId(message, props));
    }

    [Fact]
    public void ResolveCorrelationId_FallsBackToTheAmqpProperty_WhenTheBodyFieldIsAbsent()
    {
        var props = new BasicProperties { CorrelationId = "from-amqp" };

        Assert.Equal("from-amqp", DocumentConsumerService.ResolveCorrelationId(LegacyMessage, props));
    }

    /// <summary>
    /// 滾動部署硬需求:佇列裡的舊格式訊息(沒有 correlationId 欄位)必須照常解析成完整的
    /// DocumentMessage —— 缺編號只是少一個 log scope,不是 poison payload。
    /// </summary>
    [Fact]
    public void LegacyMessageWithoutTheField_StillParses_AndHasNoCorrelationId()
    {
        Assert.Equal("d1", LegacyMessage.DocumentId);
        Assert.Equal("內容", LegacyMessage.Text);
        Assert.Null(LegacyMessage.CorrelationId);
        Assert.Null(DocumentConsumerService.ResolveCorrelationId(LegacyMessage, new BasicProperties()));
    }

    // 未消毒的字串一律丟棄(邊界與 X-Correlation-Id header 同一組:trim 後 1..128 字元、無控制字元)。
    // 129 是上限的 off-point,128 在下面的 on-point 案例。
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\ninjected: header")]
    [InlineData("bad\u0000value")]
    public void ResolveCorrelationId_RejectsMalformedValues(string raw)
    {
        var props = new BasicProperties { CorrelationId = raw };

        Assert.Null(DocumentConsumerService.ResolveCorrelationId(LegacyMessage, props));
        Assert.Null(DocumentConsumerService.ResolveCorrelationId(WithCorrelationId(raw), props));
    }

    [Fact]
    public void ResolveCorrelationId_BoundaryLength_AcceptsExactly128_RejectsOneMore()
    {
        Assert.Equal(
            new string('a', 128),
            DocumentConsumerService.ResolveCorrelationId(WithCorrelationId(new string('a', 128)), new BasicProperties()));
        Assert.Null(
            DocumentConsumerService.ResolveCorrelationId(WithCorrelationId(new string('a', 129)), new BasicProperties()));
    }

    // 「只有合格的編號才開 scope」的另一半:缺席/畸形時 scope 為 null,處理照常往下走。
    [Fact]
    public void BeginCorrelationScope_OpensOnlyForAResolvedId()
    {
        var logger = new RecordingLogger<DocumentConsumerService>();

        Assert.Null(DocumentConsumerService.BeginCorrelationScope(logger, null));
        Assert.Empty(logger.Scopes);

        using (DocumentConsumerService.BeginCorrelationScope(logger, "corr-42"))
        {
            Assert.Single(logger.Scopes);
        }
    }

    [Fact]
    public void BeginCorrelationScope_CarriesTheIdUnderTheCorrelationIdKey()
    {
        var logger = new RecordingLogger<DocumentConsumerService>();

        using var scope = DocumentConsumerService.BeginCorrelationScope(
            logger, DocumentConsumerService.ResolveCorrelationId(WithCorrelationId("corr-42"), new BasicProperties()));

        var state = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object>>(Assert.Single(logger.Scopes));
        Assert.Equal("corr-42", state["CorrelationId"]);
    }

    private static readonly DocumentMessage LegacyMessage = Parse(
        """{"documentId":"d1","tenantId":"t","userId":"u","title":"標題","text":"內容"}""");

    private static DocumentMessage WithCorrelationId(string? correlationId)
        => LegacyMessage with { CorrelationId = correlationId };

    // NullLogger 的 BeginScope 永不回 null,所以「未解析出編號就不開 scope」必須靠上面那顆
    // RecordingLogger 斷言,不是靠這顆;此處只釘住 helper 對 null 的短路不依賴 logger 實作。
    [Fact]
    public void BeginCorrelationScope_NullId_ShortCircuitsBeforeTouchingTheLogger()
        => Assert.Null(DocumentConsumerService.BeginCorrelationScope(NullLogger.Instance, null));
}
