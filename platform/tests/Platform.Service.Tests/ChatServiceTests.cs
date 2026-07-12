using Platform.Service;
using Platform.Service.Dtos;
using Platform.Service.Options;
using Microsoft.Extensions.Logging.Abstractions;

namespace Platform.Service.Tests;

public sealed class ChatServiceTests
{
    private static ChatService Build(
        FakeLlmAgent agent, FakeMem0Client mem0, FakeConversationStore convos, InMemoryChatMemoryStore? memory = null)
        => new(agent, memory ?? new InMemoryChatMemoryStore(), mem0, convos, new LlmOptions(), NullLogger<ChatService>.Instance);

    [Fact]
    public async Task Chat_CallsLlm_AndPersistsPromptAndReply()
    {
        var agent = new FakeLlmAgent { Response = "AI 答覆" };
        var mem0 = new FakeMem0Client();
        var convos = new FakeConversationStore();
        var svc = Build(agent, mem0, convos);

        var response = await svc.ChatAsync("你好嗎", "u1", "c1");

        Assert.Equal("AI 答覆", response.Reply);
        Assert.True(response.Id > 0);

        var saved = Assert.Single(convos.Saved);
        Assert.Equal("你好嗎", saved.Prompt);
        Assert.Equal("AI 答覆", saved.Reply);

        // 最後一則送進 LLM 的必須是本輪 user 訊息。
        Assert.Equal("user", agent.LastMessages!.Last().Role);
        Assert.Equal("你好嗎", agent.LastMessages!.Last().Content);

        // 取得回覆後才寫 mem0。
        Assert.Single(mem0.Remembered);
        Assert.Equal(("u1", "你好嗎", "AI 答覆"), mem0.Remembered[0]);
    }

    [Fact]
    public async Task Chat_InjectsSystemMemory_WhenMem0HasResults()
    {
        var agent = new FakeLlmAgent();
        var mem0 = new FakeMem0Client { RecallResult = "- 使用者喜歡貓\n" };
        var svc = Build(agent, mem0, new FakeConversationStore());

        await svc.ChatAsync("問題", "u1", "c1");

        var first = agent.LastMessages![0];
        Assert.Equal("system", first.Role);
        Assert.Contains("以下是你先前記住、關於這位使用者的長期記憶", first.Content);
        Assert.Contains("使用者喜歡貓", first.Content);
    }

    [Fact]
    public async Task Chat_NoSystemMessage_WhenMem0Empty()
    {
        var agent = new FakeLlmAgent();
        var svc = Build(agent, new FakeMem0Client { RecallResult = string.Empty }, new FakeConversationStore());

        await svc.ChatAsync("問題", "u1", "c1");

        Assert.DoesNotContain(agent.LastMessages!, m => m.Role == "system");
    }

    [Fact]
    public async Task Chat_ShortTermMemory_CarriesPriorExchange()
    {
        var agent = new FakeLlmAgent { Response = "第一答" };
        var memory = new InMemoryChatMemoryStore();
        var svc = Build(agent, new FakeMem0Client(), new FakeConversationStore(), memory);

        await svc.ChatAsync("第一問", "u1", "c1");
        agent.Response = "第二答";
        await svc.ChatAsync("第二問", "u1", "c1");

        // 第二輪的訊息列應包含上一輪的 user 與 assistant。
        Assert.Contains(agent.LastMessages!, m => m is { Role: "user", Content: "第一問" });
        Assert.Contains(agent.LastMessages!, m => m is { Role: "assistant", Content: "第一答" });
        Assert.Equal("第二問", agent.LastMessages!.Last().Content);
    }

    [Fact]
    public async Task Chat_PersistFailure_Propagates()
    {
        var convos = new FakeConversationStore { ThrowOnAdd = true };
        var svc = Build(new FakeLlmAgent(), new FakeMem0Client(), convos);

        // 阻塞式:持久化失敗照舊往上拋(對外 500)。
        await Assert.ThrowsAsync<Platform.Service.Exceptions.BackendCallException>(() => svc.ChatAsync("嗨", "u1", "c1"));
    }

    [Fact]
    public async Task StreamChat_YieldsChunksInOrder_AndPersistsConcatenated()
    {
        var agent = new FakeLlmAgent { Chunks = new[] { "甲", "乙", "丙" } };
        var mem0 = new FakeMem0Client();
        var convos = new FakeConversationStore();
        var svc = Build(agent, mem0, convos);

        var collected = new List<string>();
        await foreach (var chunk in svc.StreamChatAsync("嗨", "u1", "c1"))
        {
            collected.Add(chunk);
        }

        Assert.Equal(new[] { "甲", "乙", "丙" }, collected);

        // 串流完成時才持久化串接後的全文。
        var saved = Assert.Single(convos.Saved);
        Assert.Equal("嗨", saved.Prompt);
        Assert.Equal("甲乙丙", saved.Reply);
        Assert.Single(mem0.Remembered);
    }

    [Fact]
    public async Task StreamChat_PersistFailure_IsBestEffort_StillYieldsAndRemembers()
    {
        var agent = new FakeLlmAgent { Chunks = new[] { "甲", "乙" } };
        var mem0 = new FakeMem0Client();
        var convos = new FakeConversationStore { ThrowOnAdd = true };
        var svc = Build(agent, mem0, convos);

        var collected = new List<string>();
        // 持久化失敗不可讓已送出的串流炸掉。
        await foreach (var chunk in svc.StreamChatAsync("嗨", "u1", "c1"))
        {
            collected.Add(chunk);
        }

        Assert.Equal(new[] { "甲", "乙" }, collected);
        // 持久化失敗仍照常寫 mem0。
        Assert.Single(mem0.Remembered);
    }

    [Fact]
    public async Task History_ReturnsStoreListDesc()
    {
        var convos = new FakeConversationStore();
        var baseTime = new DateTime(2026, 7, 11, 8, 0, 0, DateTimeKind.Utc);
        convos.Items.Add(new ChatResponse(1, "r1", baseTime));
        convos.Items.Add(new ChatResponse(2, "r2", baseTime.AddMinutes(1)));
        var svc = Build(new FakeLlmAgent(), new FakeMem0Client(), convos);

        var history = await svc.HistoryAsync();

        Assert.Equal(2, history.Count);
        Assert.Equal(2, history[0].Id);
        Assert.Equal("r2", history[0].Reply);
        Assert.Equal("r1", history[1].Reply);
    }
}
