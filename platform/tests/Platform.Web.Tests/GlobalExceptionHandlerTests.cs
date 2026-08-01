using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
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

    // 決策表同一列:下游 payload 過大 → 413,訊息同屬 4xx 可控範圍不被通用化。
    [Fact]
    public async Task PayloadTooLarge_413_PreservesControlledMessage()
    {
        var (status, body) = await Handle(
            new WorkflowPayloadTooLargeException("Business Rule request is too large"));

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, status);
        Assert.Equal("Business Rule request is too large", body["message"]!.GetValue<string>());
    }

    // FieldErrorsOf 的非 default 那一臂:只有這兩種例外帶得動欄位級錯誤,必須原樣穿到 body —
    // 錯誤碼/欄位名被吞掉的話,前端編輯器就指不出是哪一條規則、哪個欄位出錯。
    // 兩者同屬「fieldErrors 有值」等價類,只是各自映射到不同的 4xx。
    public static IEnumerable<object[]> FieldErrorCarryingExceptions() => new[]
    {
        new object[]
        {
            new WorkflowBadInputException("輸入不符規範")
            {
                FieldErrors = new Dictionary<string, string> { ["title"] = "不得空白" },
            },
            400, "title", "不得空白",
        },
        new object[]
        {
            new SkillValidationFailedException("Skill 定義未通過驗證")
            {
                FieldErrors = new Dictionary<string, string> { ["unknown_node"] = "節點 x 未註冊" },
            },
            422, "unknown_node", "節點 x 未註冊",
        },
    };

    [Theory]
    [MemberData(nameof(FieldErrorCarryingExceptions))]
    public async Task FieldErrorCarrying_4xx_PassesThroughFieldErrors(
        Exception ex, int expectedStatus, string fieldKey, string fieldMessage)
    {
        var (status, body) = await Handle(ex);

        Assert.Equal(expectedStatus, status);
        Assert.Equal(ex.Message, body["message"]!.GetValue<string>());
        Assert.Equal(fieldMessage, body["fieldErrors"]![fieldKey]!.GetValue<string>());
    }

    // 防禦分支:回應已開始寫出(串流途中)時不得再改寫 body —— 回 false 交還給呼叫端,
    // 已送出的位元組原封不動(改寫會產生「半段 SSE + 一段 JSON」的畸形回應)。
    [Fact]
    public async Task ResponseAlreadyStarted_ReturnsFalse_AndDoesNotRewriteBody()
    {
        var body = new MemoryStream();
        var ctx = new DefaultHttpContext();
        ctx.Features.Set<IHttpResponseFeature>(new StartedResponseFeature { Body = body, StatusCode = 200 });
        ctx.Response.Body = body;
        await body.WriteAsync("data:已送出的 token\n\n"u8.ToArray());
        var sentBytes = body.Length;
        var handler = new GlobalExceptionHandler(NullLogger<GlobalExceptionHandler>.Instance);

        var handled = await handler.TryHandleAsync(
            ctx, new InvalidOperationException("串流中途失敗"), CancellationToken.None);

        Assert.False(handled);
        Assert.Equal(200, ctx.Response.StatusCode);
        Assert.Equal(sentBytes, body.Length);
    }

    /// <summary>HasStarted=true 的回應功能(DefaultHttpContext 內建的實作永遠回 false,無法觸發早退分支)。</summary>
    private sealed class StartedResponseFeature : IHttpResponseFeature
    {
        public int StatusCode { get; set; } = 200;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = Stream.Null;
        public bool HasStarted => true;

        public void OnStarting(Func<object, Task> callback, object state)
        {
        }

        public void OnCompleted(Func<object, Task> callback, object state)
        {
        }
    }
}
