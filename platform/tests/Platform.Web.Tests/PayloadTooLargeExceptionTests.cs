using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Service.Exceptions;
using Platform.Web.Errors;

namespace Platform.Web.Tests;

public sealed class PayloadTooLargeExceptionTests
{
    [Fact]
    public async Task WorkflowPayloadTooLarge_Returns413WithControlledMessage()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var handler = new GlobalExceptionHandler(NullLogger<GlobalExceptionHandler>.Instance);

        var handled = await handler.TryHandleAsync(
            context,
            new WorkflowPayloadTooLargeException("Business Rule request is too large"),
            CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        var body = JsonNode.Parse(context.Response.Body)!;
        Assert.Equal("Business Rule request is too large", body["message"]!.GetValue<string>());
    }
}
