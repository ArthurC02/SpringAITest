using Platform.Service;
using Platform.Service.Abstractions;

namespace Platform.Service.Tests;

/// <summary>短期記憶滑動視窗邊界值(MaxMessages=20):恰滿、溢出裁切、未知對話。</summary>
public sealed class InMemoryChatMemoryStoreTests
{
    private static void AppendN(InMemoryChatMemoryStore store, string cid, int n)
    {
        for (var i = 0; i < n; i++)
        {
            store.Append(cid, new LlmMessage("user", "訊息" + i));
        }
    }

    [Fact]
    public void Append20_GetRecent_ReturnsAll_InInsertionOrder()
    {
        var store = new InMemoryChatMemoryStore();
        AppendN(store, "c1", 20);

        var recent = store.GetRecent("c1");

        Assert.Equal(20, recent.Count);
        Assert.Equal("訊息0", recent[0].Content);
        Assert.Equal("訊息19", recent[19].Content);
    }

    [Fact]
    public void Append21_TrimsOldest_FirstIsSecondInserted()
    {
        var store = new InMemoryChatMemoryStore();
        AppendN(store, "c1", 21);

        var recent = store.GetRecent("c1");

        Assert.Equal(20, recent.Count);
        // 最舊(訊息0)被裁掉,首元素為原第 2 則。
        Assert.Equal("訊息1", recent[0].Content);
        Assert.Equal("訊息20", recent[19].Content);
    }

    [Fact]
    public void GetRecent_UnknownConversation_ReturnsEmpty()
    {
        var store = new InMemoryChatMemoryStore();

        Assert.Empty(store.GetRecent("沒見過的對話"));
    }
}
