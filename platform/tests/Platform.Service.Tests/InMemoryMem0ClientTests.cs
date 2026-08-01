using Platform.Service;

namespace Platform.Service.Tests;

/// <summary>
/// InMemoryMem0Client(start-lite MEM0_MODE=inmemory)契約:
/// per-user 訊息對、最多 100 對、關鍵字 case-insensitive、最新優先、錯誤全吞(不 throw)。
/// 04-acceptance-test B-M-02~08。
/// </summary>
public sealed class InMemoryMem0ClientTests
{
    // B-M-02:未知 user / 空 userId / 空 query 皆回 "" 且不 throw。
    [Theory]
    [InlineData("u1", "任何")] // 未知 user
    [InlineData("", "q")]      // 空 userId
    [InlineData("u1", "")]     // 空 query
    [InlineData(null, "q")]
    [InlineData("u1", null)]
    [InlineData("   ", "q")]   // 空白 userId 也算 IsNullOrWhiteSpace
    [InlineData("u1", "   ")]  // 空白 query 同理
    public async Task Recall_MissingOrUnknown_ReturnsEmpty(string? userId, string? query)
    {
        var client = new InMemoryMem0Client();

        var result = await client.RecallAsync(userId!, query!);

        Assert.Equal("", result);
    }

    // B-M-03:remember 後 recall 到,case-insensitive,格式為 "- userMsg\n- aiReply"。
    [Fact]
    public async Task Remember_ThenRecall_ReturnsPair()
    {
        var client = new InMemoryMem0Client();
        await client.RememberAsync("u1", "我住台北", "好的記住了");

        var recalled = await client.RecallAsync("u1", "台北");

        Assert.Equal("- 我住台北\n- 好的記住了", recalled);
    }

    [Fact]
    public async Task Recall_IsCaseInsensitive()
    {
        var client = new InMemoryMem0Client();
        await client.RememberAsync("u1", "HELLO WORLD", "Hi there");

        var recalled = await client.RecallAsync("u1", "hello");

        Assert.Contains("HELLO WORLD", recalled);
    }

    // 比對條件是 userMsg || aiReply:query 只命中 aiReply 時,整對仍要回傳。
    [Fact]
    public async Task Recall_QueryMatchesAiReplyOnly_ReturnsPair()
    {
        var client = new InMemoryMem0Client();
        await client.RememberAsync("u1", "你好", "ALPHA 機密");

        var recalled = await client.RecallAsync("u1", "ALPHA");

        Assert.Equal("- 你好\n- ALPHA 機密", recalled);
    }

    // B-M-04:多筆符合時最新優先(第一行是最新那對的 userMsg)。
    [Fact]
    public async Task Recall_MultipleMatches_LatestFirst()
    {
        var client = new InMemoryMem0Client();
        await client.RememberAsync("u1", "第一次問", "第一次答");
        await client.RememberAsync("u1", "第二次問", "第二次答");

        var recalled = await client.RecallAsync("u1", "問");
        var lines = recalled.Split('\n');

        Assert.Equal("- 第二次問", lines[0]);
        Assert.Equal("- 第二次答", lines[1]);
        Assert.Equal("- 第一次問", lines[2]);
    }

    // B-M-05:on/off-point。100 對:第 1 對仍在;101 對:最舊(第 1 對)被擠掉。
    [Theory]
    [InlineData(100, false)]
    [InlineData(101, true)]
    public async Task Remember_CapacityBoundary_EvictsOldest(int total, bool oldestEvicted)
    {
        var client = new InMemoryMem0Client();
        await client.RememberAsync("u1", "UNIQUEKEY 第一對", "reply-0");
        for (var i = 1; i < total; i++)
        {
            await client.RememberAsync("u1", $"filler {i}", $"r{i}");
        }

        var recalled = await client.RecallAsync("u1", "UNIQUEKEY");

        if (oldestEvicted)
        {
            Assert.Equal("", recalled);
        }
        else
        {
            Assert.Contains("UNIQUEKEY 第一對", recalled);
        }
    }

    // B-M-06:內容隔離——u2 的 recall 不含 u1 寫入的字串。
    [Fact]
    public async Task Recall_IsolatedPerUser()
    {
        var client = new InMemoryMem0Client();
        await client.RememberAsync("u1", "u1 的密語 ALPHA", "回覆");
        await client.RememberAsync("u2", "u2 的密語 BETA", "回覆");

        var u2Recall = await client.RecallAsync("u2", "ALPHA");

        Assert.Equal("", u2Recall);
        Assert.DoesNotContain("ALPHA", u2Recall);
    }

    // B-M-07:無效輸入不 throw、字典不長出垃圾鍵(隨後 recall 該鍵仍為 "")。
    [Theory]
    [InlineData(null, "msg", "reply")]
    [InlineData("", "msg", "reply")]
    [InlineData("   ", "msg", "reply")]
    [InlineData("u1", null, null)]
    [InlineData("u1", "", "")]
    [InlineData("u1", "  ", "  ")]
    public async Task Remember_InvalidInput_NoOpDoesNotThrow(string? userId, string? userMsg, string? aiReply)
    {
        var client = new InMemoryMem0Client();

        await client.RememberAsync(userId!, userMsg!, aiReply!);

        // 雙空 message 的 u1 應無記錄。
        Assert.Equal("", await client.RecallAsync("u1", "msg"));
    }

    // no-op 條件是「兩者皆空白」(AND):單邊有內容仍要記錄,空白那側原樣輸出。
    [Fact]
    public async Task Remember_OnlyOneSideHasContent_StillRecorded()
    {
        var client = new InMemoryMem0Client();

        await client.RememberAsync("u1", "", "只有 AI 側有內容");

        Assert.Equal("- \n- 只有 AI 側有內容", await client.RecallAsync("u1", "只有 AI 側"));
    }

    // B-M-08:併發 remember+recall 不 throw(集合被併發修改)。
    [Fact]
    public async Task Concurrent_RememberAndRecall_DoesNotThrow()
    {
        var client = new InMemoryMem0Client();

        var tasks = Enumerable.Range(0, 16).Select(t => Task.Run(async () =>
        {
            for (var i = 0; i < 100; i++)
            {
                await client.RememberAsync("u1", $"訊息 {t}-{i}", $"回覆 {t}-{i}");
                await client.RecallAsync("u1", "訊息");
            }
        }));

        await Task.WhenAll(tasks); // 不得擲 InvalidOperationException 等併發例外
    }
}
