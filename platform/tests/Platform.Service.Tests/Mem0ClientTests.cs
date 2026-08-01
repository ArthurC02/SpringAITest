using System.Net;
using System.Text.Json;
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

    // 集合大小邊界:恰好 1 筆(另一半 0 筆見 Recall_EmptyOrNullResults_ReturnsEmpty)。
    // 迴圈每筆都補一個 "\n",單筆時不得少一個換行、也不得多出分隔符。
    [Fact]
    public async Task Recall_SingleResult_FormatsOneBullet()
    {
        var stub = new StubHttpMessageHandler(_ =>
            TestHttp.Json(HttpStatusCode.OK, "{\"results\":[{\"memory\":\"喜歡貓\"}]}"));
        var client = Build(stub);

        Assert.Equal("- 喜歡貓\n", await client.RecallAsync("u1", "查詢"));
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

    // RememberAsync 的成功那一半:200 OK 正常結束,且請求形狀符合 mem0 /memories 契約
    // (user_id + user/assistant 兩則訊息);只驗失敗被吞掉等於沒驗這個 client 到底送了什麼。
    [Fact]
    public async Task Remember_Success_PostsUserAndAssistantMessages()
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.OK, "{}"));
        var client = Build(stub);

        await client.RememberAsync("u1", "使用者訊息", "AI 回覆");

        Assert.Equal("http://mem0/memories", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, stub.LastRequest!.Method);

        using var doc = JsonDocument.Parse(stub.LastBody!);
        Assert.Equal("u1", doc.RootElement.GetProperty("user_id").GetString());
        var messages = doc.RootElement.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("使用者訊息", messages[0].GetProperty("content").GetString());
        Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
        Assert.Equal("AI 回覆", messages[1].GetProperty("content").GetString());
    }

    // RememberAsync 是獨立方法、獨立 catch,三種失敗都要跟 Recall 一樣被吞掉:
    // "transport" 是「完全沒有回應」那個等價類(HttpRequestException,不是狀態碼錯誤),
    // "timeout" 則釘住「非呼叫端取消要吞掉」那一半。
    [Theory]
    [InlineData("server-error")]
    [InlineData("transport")]
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
