using System.Net;
using Platform.Service;
using Platform.Service.Options;
using Microsoft.Extensions.Logging.Abstractions;

namespace Platform.Service.Tests;

/// <summary>
/// Mem0Client 契約:recall 把 results 組成 "- memory\n" 串接;所有錯誤都吞掉(記憶最佳努力),
/// recall 出錯回空字串、remember 出錯不擲例外。
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

    [Fact]
    public async Task Recall_ServerError_ReturnsEmpty_DoesNotThrow()
    {
        var client = Build(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        Assert.Equal(string.Empty, await client.RecallAsync("u1", "查詢"));
    }

    [Fact]
    public async Task Recall_TransportError_ReturnsEmpty_DoesNotThrow()
    {
        var client = Build(new StubHttpMessageHandler(_ => throw new HttpRequestException("mem0 不可達")));

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

    [Fact]
    public async Task Remember_ServerError_DoesNotThrow()
    {
        var client = Build(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        // 不得擲例外(best-effort;記憶失敗不可讓聊天中斷)。
        await client.RememberAsync("u1", "使用者訊息", "AI 回覆");
    }
}
