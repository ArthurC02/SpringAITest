using System.Net;
using System.Net.Http.Json;

namespace Backend.Api.Tests;

/// <summary>聊天歷史端點(全域,不分租戶):建立回 201 {id, createdAt};清單 created_at DESC;prompt/reply 空白 400。</summary>
public sealed class ConversationsApiTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _factory;

    public ConversationsApiTests(TestWebAppFactory factory) => _factory = factory;

    [Fact]
    public async Task Create_Returns201_WithIdAndCreatedAt()
    {
        var client = _factory.CreateInternalClient();

        var resp = await client.PostAsJsonAsync("/api/conversations", new { prompt = "問句", reply = "答句" });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.True(body["id"]!.GetValue<long>() > 0);
        Assert.NotNull(body["createdAt"]);
    }

    [Fact]
    public async Task List_ReturnsNewestFirst()
    {
        var client = _factory.CreateInternalClient();
        await client.PostAsJsonAsync("/api/conversations", new { prompt = "P1", reply = "較早" });
        await client.PostAsJsonAsync("/api/conversations", new { prompt = "P2", reply = "較晚" });

        var arr = (await (await client.GetAsync("/api/conversations")).ReadJsonAsync()).AsArray();

        // created_at DESC 契約:相鄰項時間非遞增。
        for (var i = 1; i < arr.Count; i++)
        {
            var prev = arr[i - 1]!["createdAt"]!.GetValue<DateTime>();
            var cur = arr[i]!["createdAt"]!.GetValue<DateTime>();
            Assert.True(prev >= cur);
        }

        // 較晚插入者排在較早之前(時間相同時以 id DESC 打破平手)。
        var replies = arr.Select(n => n!["reply"]!.GetValue<string>()).ToList();
        Assert.True(replies.IndexOf("較晚") < replies.IndexOf("較早"));
    }

    [Theory]
    [InlineData("", "答", "prompt", "prompt 不可為空")]
    [InlineData("問", "", "reply", "reply 不可為空")]
    public async Task Create_BlankField_Returns400(string prompt, string reply, string field, string message)
    {
        var client = _factory.CreateInternalClient();

        var resp = await client.PostAsJsonAsync("/api/conversations", new { prompt, reply });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(message, (await resp.ReadJsonAsync())["fieldErrors"]![field]!.GetValue<string>());
    }
}
