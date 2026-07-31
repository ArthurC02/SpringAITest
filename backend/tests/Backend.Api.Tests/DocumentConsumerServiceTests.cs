using System.Text;
using Backend.Api.Files;
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
}
