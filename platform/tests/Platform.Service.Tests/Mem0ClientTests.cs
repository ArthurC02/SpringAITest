using System.Net;
using Platform.Service.Options;
using Microsoft.Extensions.Logging.Abstractions;

namespace Platform.Service.Tests;

/// <summary>
/// Mem0Client 契約:recall 把 results 組成 "- memory\n" 串接;所有錯誤都吞掉(記憶最佳努力),
/// recall 出錯回空字串、remember 出錯不擲例外 —— 唯一例外是呼叫端主動取消,必須原樣往上拋。
/// </summary>
public sealed class Mem0ClientTests
{
    private static Mem0Client Build(StubHttpMessageHandler stub) =>
        new(new HttpClient(stub), new Mem0Options { BaseUrl = "http://mem0" }, NullLogger<Mem0Client>.Instance);

    [Fact]
    public async Task Recall_TwoResults_FormatsBulletList()
    {
        var stub = new StubHttpMessageHandler(_ =>
            TestHttp.Json(HttpStatusCode.OK, "{\"results\":[{\"memory\":\"喜歡貓\"},{\"memory\":\"住台北\"}]}"));
        var client = Build(stub);

        var result = await client.RecallAsync("u1", "查詢");

        Assert.Equal("- 喜歡貓\n- 住台北\n", result);
    }

    /// <summary>依 kind 造出失敗的下游:狀態碼錯誤 / 完全沒有回應 / HttpClient 自身逾時。</summary>
    private static StubHttpMessageHandler Failing(string kind) => new(_ => kind switch
    {
        "server-error" => new HttpResponseMessage(HttpStatusCode.InternalServerError),
        "transport" => throw new HttpRequestException("mem0 不可達"),
        // HttpClient 逾時:拋 TaskCanceledException,但呼叫端的 ct 並未取消。
        _ => throw new TaskCanceledException("逾時"),
    });

    // 三種失敗都命中同一個 best-effort 分支。"timeout" 是 `when (ct.IsCancellationRequested)` 的另一半:
    // 例外型別雖是 OperationCanceledException,但不是呼叫端取消,仍必須被吞掉(不可一律往上拋)。
    [Theory]
    [InlineData("server-error")]
    [InlineData("transport")]
    [InlineData("timeout")]
    public async Task Recall_Failure_ReturnsEmpty_DoesNotThrow(string kind)
    {
        var client = Build(Failing(kind));

        Assert.Equal(string.Empty, await client.RecallAsync("u1", "查詢"));
    }

    [Theory]
    [InlineData("{\"results\":[]}")]
    [InlineData("{\"results\":null}")]
    public async Task Recall_EmptyOrNullResults_ReturnsEmpty(string body)
    {
        var client = Build(new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.OK, body)));

        Assert.Equal(string.Empty, await client.RecallAsync("u1", "查詢"));
    }

    // RememberAsync 是獨立方法、獨立 catch;"timeout" 同樣釘住「非呼叫端取消要吞掉」那一半。
    [Theory]
    [InlineData("server-error")]
    [InlineData("timeout")]
    public async Task Remember_Failure_DoesNotThrow(string kind)
    {
        var client = Build(Failing(kind));

        // 不得擲例外(best-effort;記憶失敗不可讓聊天中斷)。
        await client.RememberAsync("u1", "使用者訊息", "AI 回覆");
    }

    // best-effort 的例外:呼叫端主動取消(例如客戶端中斷串流)必須原樣往上拋,
    // 不得被「吞掉所有錯誤」的分支吃成正常結束 —— 否則協作式取消會在 mem0 這層靜默失效。
    [Fact]
    public async Task Recall_CallerCancellation_Propagates()
    {
        var client = Build(new StubHttpMessageHandler(_ => throw new OperationCanceledException()));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.RecallAsync("u1", "查詢", cts.Token));
    }

    [Fact]
    public async Task Remember_CallerCancellation_Propagates()
    {
        var client = Build(new StubHttpMessageHandler(_ => throw new OperationCanceledException()));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.RememberAsync("u1", "使用者訊息", "AI 回覆", cts.Token));
    }
}
