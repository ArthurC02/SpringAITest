using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Service.Exceptions;
using Platform.Web.Errors;

namespace Platform.Web.Tests;

/// <summary>
/// 例外 → 對外 ApiError 的決策表收尾:5xx 一律回固定通用訊息(下游/例外細節不外洩,只進 log);
/// 4xx 沿用 backend 的可控 message 不被通用化。
/// </summary>
public sealed class GlobalExceptionHandlerTests
{
    private static async Task<(int Status, JsonNode Body)> Handle(Exception ex)
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        var handler = new GlobalExceptionHandler(NullLogger<GlobalExceptionHandler>.Instance);

        var handled = await handler.TryHandleAsync(ctx, ex, CancellationToken.None);
        Assert.True(handled);

        ctx.Response.Body.Position = 0;
        var body = JsonNode.Parse(ctx.Response.Body)!;
        return (ctx.Response.StatusCode, body);
    }

    // 未映射的例外 → 500 + 固定繁中通用訊息;原始細節不得出現在對外 body。
    [Fact]
    public async Task Unmapped_ServerError_ReturnsGenericMessage_NoLeak()
    {
        var (status, body) = await Handle(new InvalidOperationException("內部堆疊與拓撲細節"));

        Assert.Equal(500, status);
        Assert.Equal("伺服器發生錯誤，請稍後再試", body["message"]!.GetValue<string>());
        Assert.DoesNotContain("內部堆疊", body.ToJsonString());
    }

    // 下游呼叫失敗 → 502 + 固定繁中通用訊息;下游位址/狀態碼細節不得外洩。
    [Fact]
    public async Task WorkflowInvocation_502_ReturnsGenericMessage_NoLeak()
    {
        var (status, body) = await Handle(
            new WorkflowInvocationException("工作流服務呼叫失敗：HTTP 500 http://backend 連線細節"));

        Assert.Equal(502, status);
        Assert.Equal("上游服務暫時無法使用，請稍後再試", body["message"]!.GetValue<string>());
        var json = body.ToJsonString();
        Assert.DoesNotContain("backend", json);
        Assert.DoesNotContain("HTTP 500", json);
    }

    // 決策表另一半:4xx 可控 message 維持現狀(這裡是下游 404 → 對外 404,訊息原樣)。
    [Fact]
    public async Task ClientError_4xx_PreservesControlledMessage()
    {
        var (status, body) = await Handle(new DocumentNotFoundException("找不到文件：d1"));

        Assert.Equal(404, status);
        Assert.Equal("找不到文件：d1", body["message"]!.GetValue<string>());
    }
}
