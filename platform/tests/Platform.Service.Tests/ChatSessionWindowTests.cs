using System.Text.Json;
using Platform.Service.Dtos;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Extensions.AI;

namespace Platform.Service.Tests;

/// <summary>
/// P2(copilot-shared-core)記憶收斂:短期記憶視窗改由框架的 <see cref="InMemoryChatHistoryProvider"/>
/// + <c>SlidingWindowCompactionStrategy(MessagesExceed(20), minimumPreservedTurns:1)</c> 管理,取代
/// 已刪除的 <c>InMemoryChatMemoryStore</c>。承接 04-acceptance-test.md §4.2(T-P2-1~3、B-P2-03、B-P2-05)
/// 與 <c>InMemoryChatMemoryStoreTests.cs</c>(已刪除)的邊界值語意——20 on-point、21 off-point,
/// 但裁切以「整個 turn」為原子單位(行為改善,不是回歸),不再是舊實作的 RemoveAt(0) 單則裁切。
/// </summary>
public sealed class ChatSessionWindowTests
{
    private static readonly UserContext UserA = new("user-a", "demo-a", "USER");

    // 單一 kb-query 工具目錄,供下方「連續 skill HIT」裁切測試路由命中用。
    private const string SingleSkillCatalog = """
    [ { "name":"kb-query", "description":"x", "required_role":"USER", "source":"builtin",
        "input_schema": { "query": { "type":"str", "required":true } } } ]
    """;

    private static void Seed(InMemoryChatHistoryProvider historyProvider, AgentSession session, int count)
    {
        var seeded = new List<ChatMessage>();
        for (var i = 0; i < count; i++)
        {
            seeded.Add(new ChatMessage(i % 2 == 0 ? ChatRole.User : ChatRole.Assistant, $"hist{i}"));
        }
        historyProvider.SetMessages(session, seeded);
    }

    // ---- T-P2-1:視窗 20 則(on-point)—— 不裁切 ----
    [Fact]
    [Trait("EvidenceGate", "E-03")]
    public async Task Window_At20Messages_NothingCompacted()
    {
        var chatClient = new FakeChatClient();
        var (hostAgent, historyProvider, _) = TestChatAgent.Build(chatClient);

        var session = await hostAgent.GetOrCreateSessionAsync("c1");
        Seed(historyProvider, session, 20);
        await hostAgent.SaveSessionAsync("c1", session);

        var session2 = await hostAgent.GetOrCreateSessionAsync("c1");
        await hostAgent.RunAsync("new query", session2);

        for (var i = 0; i < 20; i++)
        {
            Assert.Contains(chatClient.LastMessages!, m => m.Text == $"hist{i}");
        }
        Assert.Equal("new query", chatClient.LastMessages!.Last().Text);
    }

    // ---- T-P2-2:視窗 21 則(off-point)—— 最舊整個 turn 被裁 ----
    [Fact]
    [Trait("EvidenceGate", "E-03")]
    public async Task Window_At21Messages_OldestTurnDropped()
    {
        var chatClient = new FakeChatClient { Response = "round1-reply" };
        var (hostAgent, historyProvider, _) = TestChatAgent.Build(chatClient);

        var session = await hostAgent.GetOrCreateSessionAsync("c2");
        Seed(historyProvider, session, 20);
        await hostAgent.SaveSessionAsync("c2", session);

        // 第一輪:20(已存)+ 2(這輪)= 22,觸發裁切——裁切發生在「回覆之後」寫回 session 的階段,
        // 這一輪自己送出的訊息列仍是裁切前的完整 20+1(spike 實測)。
        var round1 = await hostAgent.GetOrCreateSessionAsync("c2");
        await hostAgent.RunAsync("round1", round1);
        await hostAgent.SaveSessionAsync("c2", round1);

        chatClient.Response = "round2-reply";
        // 第二輪才看得到裁切後的結果:最舊整個 turn(hist0/hist1)被裁掉,其餘保留。
        var round2 = await hostAgent.GetOrCreateSessionAsync("c2");
        await hostAgent.RunAsync("round2", round2);

        Assert.DoesNotContain(chatClient.LastMessages!, m => m.Text == "hist0");
        Assert.DoesNotContain(chatClient.LastMessages!, m => m.Text == "hist1");
        for (var i = 2; i < 20; i++)
        {
            Assert.Contains(chatClient.LastMessages!, m => m.Text == $"hist{i}");
        }
        Assert.Contains(chatClient.LastMessages!, m => m.Text == "round1");
        Assert.Contains(chatClient.LastMessages!, m => m.Text == "round1-reply");
        Assert.Equal("round2", chatClient.LastMessages!.Last().Text);
    }

    // ---- 未知 key:GetOrCreateSessionAsync 自動建空 session,GetMessages 回空清單而非 null ----
    // (01-plan §2 spike 實測:GetSessionAsync 對未知 key 不回 null,驗收必須驗內容而非 nullity)。
    [Fact]
    public async Task Session_UnknownKey_HasEmptyHistory_NotNull()
    {
        var (hostAgent, historyProvider, _) = TestChatAgent.Build();

        var session = await hostAgent.GetOrCreateSessionAsync("brand-new-conversation-key");
        var messages = historyProvider.GetMessages(session);

        Assert.NotNull(messages);
        Assert.Empty(messages);
    }

    // ---- B-P2-03:tool call / tool result 配對不被拆散,裁切以整個 turn 為原子單位 ----
    [Fact]
    [Trait("EvidenceGate", "E-03")]
    public async Task Window_ToolCallAndResult_NotSplitByCompaction()
    {
        var chatClient = new FakeChatClient { Response = "確認" };
        var (hostAgent, historyProvider, _) = TestChatAgent.Build(chatClient);

        var session = await hostAgent.GetOrCreateSessionAsync("c-tool");
        var seeded = new List<ChatMessage>
        {
            new(ChatRole.User, "toolQ"),
            new(ChatRole.Assistant, new List<AIContent> { new FunctionCallContent("call1", "doThing", new Dictionary<string, object?>()) }),
            new(ChatRole.Tool, new List<AIContent> { new FunctionResultContent("call1", "result1") }),
            new(ChatRole.Assistant, "final answer using tool"),
        };
        for (var i = 0; i < 18; i++)
        {
            seeded.Add(new ChatMessage(i % 2 == 0 ? ChatRole.User : ChatRole.Assistant, $"m{i}"));
        }
        historyProvider.SetMessages(session, seeded); // 22 則(4 個一組的 tool turn + 18 則一般訊息)
        await hostAgent.SaveSessionAsync("c-tool", session);

        // 22(已存)+ 2(這輪)= 24,超過 20,觸發裁切——最舊的整個 turn 是 4 則一組的 tool-call turn。
        var round1 = await hostAgent.GetOrCreateSessionAsync("c-tool");
        await hostAgent.RunAsync("round1", round1);
        await hostAgent.SaveSessionAsync("c-tool", round1);

        var finalSession = await hostAgent.GetOrCreateSessionAsync("c-tool");
        var stored = historyProvider.GetMessages(finalSession);

        // tool-call turn 整組消失,沒有孤兒(不存在無配對的 FunctionCallContent/FunctionResultContent)。
        Assert.DoesNotContain(stored, m => m.Contents.Any(c => c is FunctionCallContent));
        Assert.DoesNotContain(stored, m => m.Contents.Any(c => c is FunctionResultContent));
        Assert.DoesNotContain(stored, m => m.Text == "toolQ");
        Assert.DoesNotContain(stored, m => m.Text == "final answer using tool");
    }

    // ---- 護欄/mem0 走 Instructions,不進 history,故裁切裁不到它,且不隨輪次累積 ----
    [Fact]
    public async Task Window_SystemInstructions_NeverEnterHistory_RegardlessOfWindowSize()
    {
        var chatClient = new FakeChatClient();
        var (hostAgent, historyProvider, _) = TestChatAgent.Build(chatClient);

        for (var i = 0; i < 12; i++)
        {
            var runOptions = new ChatClientAgentRunOptions(new ChatOptions { Instructions = $"GUARD-{i}" });
            var session = await hostAgent.GetOrCreateSessionAsync("c-instr");
            await hostAgent.RunAsync($"Q{i}", session, runOptions);
            await hostAgent.SaveSessionAsync("c-instr", session);
        }

        var finalSession = await hostAgent.GetOrCreateSessionAsync("c-instr");
        var stored = historyProvider.GetMessages(finalSession);

        Assert.DoesNotContain(stored, m => (m.Text ?? string.Empty).StartsWith("GUARD-", StringComparison.Ordinal));
    }

    // ---- B-P2-05:minimumPreservedTurns 陷阱 —— 26 則後裁切確實觸發,不因下限誤設而完全不觸發 ----
    // (01-plan §2 spike 實測:minimumPreservedTurns 設成 20 時,26 則完全不觸發;此案專門抓這個打錯的數字)。
    [Fact]
    public async Task Window_After26Messages_CompactionHasTriggered_NotSuppressedByMinimumPreservedTurns()
    {
        var chatClient = new FakeChatClient();
        var (hostAgent, historyProvider, _) = TestChatAgent.Build(chatClient);

        for (var i = 0; i < 13; i++) // 13 輪 = 累積 26 則(user+assistant 各一)
        {
            var session = await hostAgent.GetOrCreateSessionAsync("c-26");
            await hostAgent.RunAsync($"Q{i}", session);
            await hostAgent.SaveSessionAsync("c-26", session);
        }

        var finalSession = await hostAgent.GetOrCreateSessionAsync("c-26");
        var stored = historyProvider.GetMessages(finalSession);

        Assert.True(stored.Count < 26, $"expected compaction to have triggered by 26 messages, but stored.Count={stored.Count}");
    }

    // ============================================================================
    // P4 code review 發現 1:連續 skill HIT 手動 append 繞過 ChatClientAgent,SlidingWindowCompactionStrategy
    // 只在 ChatClientAgent 的 ChatHistoryProvider 生命週期鉤子(ProvideChatHistoryAsync/StoreChatHistoryAsync)
    // 內觸發,HIT 輪從不流經那裡 → session 無界成長。修法:AppendExchangeToSessionAsync 寫回前呼叫同一顆
    // InMemoryChatHistoryProvider.ChatReducer 執行等價裁切。以下比照本檔 T-P2-1/T-P2-2 的 20 on-point /
    // 21 off-point 慣例,但用「連續 HIT」而非「一般聊天輪」觸發成長。
    // ============================================================================

    private static JsonElement Cat(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>把 agent 的下一輪路由 + 摘要腳本化為「命中 kb-query」。</summary>
    private static void EnqueueHit(FakeLlmAgent agent, string summaryReply)
    {
        agent.Responses.Enqueue("kb-query");
        agent.Responses.Enqueue(summaryReply);
    }

    // ---- 20 on-point(seed 18 + 一輪 HIT 的 2 則 = 20)—— 不裁切 ----
    [Fact]
    public async Task ConsecutiveHits_At20Messages_NothingCompacted()
    {
        var agent = new FakeLlmAgent();
        var wf = new FakeWorkflowService
        {
            Catalog = Cat(SingleSkillCatalog),
            SkillOutput = Cat("""{ "skill":"kb-query", "output": { "business_result":"x" } }"""),
        };
        var identity = new FakeChatIdentityAccessor();
        var (hostAgent, historyProvider, _) = TestChatAgent.Build(identity: identity, llmAgent: agent, workflows: wf);
        identity.SetRequestKeys("u1", "c-hit20", UserA);

        var session = await hostAgent.GetOrCreateSessionAsync("c-hit20");
        Seed(historyProvider, session, 18);
        await hostAgent.SaveSessionAsync("c-hit20", session);

        EnqueueHit(agent, "reply");
        var round = await hostAgent.GetOrCreateSessionAsync("c-hit20");
        await hostAgent.RunAsync("Q", round);
        await hostAgent.SaveSessionAsync("c-hit20", round);

        var finalSession = await hostAgent.GetOrCreateSessionAsync("c-hit20");
        var stored = historyProvider.GetMessages(finalSession);

        Assert.Equal(20, stored.Count);
        Assert.Contains(stored, m => m.Text == "hist0");   // on-point:最舊訊息仍在,沒有觸發裁切
        Assert.Contains(stored, m => m.Text == "Q");
        Assert.Contains(stored, m => m.Text == "reply");
    }

    // ---- 21+ off-point(seed 20 + 一輪 HIT 的 2 則 = 22)—— 最舊整個 turn 被裁 ----
    [Fact]
    public async Task ConsecutiveHits_At21Messages_OldestTurnDropped()
    {
        var agent = new FakeLlmAgent();
        var wf = new FakeWorkflowService
        {
            Catalog = Cat(SingleSkillCatalog),
            SkillOutput = Cat("""{ "skill":"kb-query", "output": { "business_result":"x" } }"""),
        };
        var identity = new FakeChatIdentityAccessor();
        var (hostAgent, historyProvider, _) = TestChatAgent.Build(identity: identity, llmAgent: agent, workflows: wf);
        identity.SetRequestKeys("u1", "c-hit21", UserA);

        var session = await hostAgent.GetOrCreateSessionAsync("c-hit21");
        Seed(historyProvider, session, 20);
        await hostAgent.SaveSessionAsync("c-hit21", session);

        EnqueueHit(agent, "reply");
        var round = await hostAgent.GetOrCreateSessionAsync("c-hit21");
        await hostAgent.RunAsync("Q", round);
        await hostAgent.SaveSessionAsync("c-hit21", round);

        var finalSession = await hostAgent.GetOrCreateSessionAsync("c-hit21");
        var stored = historyProvider.GetMessages(finalSession);

        // 裁切在同一輪 HIT 寫回時立即生效(不必等下一輪),裁掉最舊整個 turn(hist0/hist1)。
        Assert.Equal(20, stored.Count);
        Assert.DoesNotContain(stored, m => m.Text == "hist0");
        Assert.DoesNotContain(stored, m => m.Text == "hist1");
        for (var i = 2; i < 20; i++)
        {
            Assert.Contains(stored, m => m.Text == $"hist{i}");
        }
        Assert.Contains(stored, m => m.Text == "Q");
        Assert.Contains(stored, m => m.Text == "reply");
    }

    // ---- HIT 路徑也必須整 turn 原子裁切:tool-call/result 配對不得被手動 append 的裁切拆散 ----
    // (Window_ToolCallAndResult_NotSplitByCompaction 只證明了 ChatClientAgent 那條路徑;HIT 輪走的是
    //  SkillRoutingAgent.AppendExchangeToSessionAsync 自己呼叫 ChatReducer 的另一段程式碼,先前只用純文字測過。)
    [Fact]
    public async Task ConsecutiveHits_ToolCallAndResult_NotSplitByManualCompaction()
    {
        var agent = new FakeLlmAgent();
        var wf = new FakeWorkflowService
        {
            Catalog = Cat(SingleSkillCatalog),
            SkillOutput = Cat("""{ "skill":"kb-query", "output": { "business_result":"x" } }"""),
        };
        var identity = new FakeChatIdentityAccessor();
        var (hostAgent, historyProvider, _) = TestChatAgent.Build(identity: identity, llmAgent: agent, workflows: wf);
        identity.SetRequestKeys("u1", "c-hit-tool", UserA);

        var session = await hostAgent.GetOrCreateSessionAsync("c-hit-tool");
        var seeded = new List<ChatMessage>
        {
            new(ChatRole.User, "toolQ"),
            new(ChatRole.Assistant, new List<AIContent> { new FunctionCallContent("call1", "doThing", new Dictionary<string, object?>()) }),
            new(ChatRole.Tool, new List<AIContent> { new FunctionResultContent("call1", "result1") }),
            new(ChatRole.Assistant, "final answer using tool"),
        };
        for (var i = 0; i < 18; i++)
        {
            seeded.Add(new ChatMessage(i % 2 == 0 ? ChatRole.User : ChatRole.Assistant, $"m{i}"));
        }
        historyProvider.SetMessages(session, seeded); // 22 則(4 則一組的 tool turn + 18 則一般訊息)
        await hostAgent.SaveSessionAsync("c-hit-tool", session);

        // 一輪 HIT 手動 append 2 則 → 24 則,觸發裁切;最舊的整個 turn 是那 4 則一組的 tool-call turn。
        EnqueueHit(agent, "reply");
        var round = await hostAgent.GetOrCreateSessionAsync("c-hit-tool");
        await hostAgent.RunAsync("Q", round);
        await hostAgent.SaveSessionAsync("c-hit-tool", round);

        var finalSession = await hostAgent.GetOrCreateSessionAsync("c-hit-tool");
        var stored = historyProvider.GetMessages(finalSession);

        Assert.DoesNotContain(stored, m => m.Contents.Any(c => c is FunctionCallContent));
        Assert.DoesNotContain(stored, m => m.Contents.Any(c => c is FunctionResultContent));
        Assert.DoesNotContain(stored, m => m.Text == "toolQ");
        Assert.DoesNotContain(stored, m => m.Text == "final answer using tool");
        Assert.Contains(stored, m => m.Text == "Q");
        Assert.Contains(stored, m => m.Text == "reply");
    }

    // ---- 連續多輪 HIT(本產品主流情境):視窗全程被裁到 ≤20,不會無界成長 ----
    [Fact]
    public async Task ManyConsecutiveHits_WindowStaysBounded_NeverGrowsUnbounded()
    {
        var agent = new FakeLlmAgent();
        var wf = new FakeWorkflowService
        {
            Catalog = Cat(SingleSkillCatalog),
            SkillOutput = Cat("""{ "skill":"kb-query", "output": { "business_result":"x" } }"""),
        };
        var identity = new FakeChatIdentityAccessor();
        var (hostAgent, historyProvider, _) = TestChatAgent.Build(identity: identity, llmAgent: agent, workflows: wf);
        identity.SetRequestKeys("u1", "c-hit-many", UserA);

        for (var i = 0; i < 15; i++)   // 15 輪連續 HIT = 30 則,遠超 20 視窗
        {
            EnqueueHit(agent, $"reply{i}");
            var session = await hostAgent.GetOrCreateSessionAsync("c-hit-many");
            await hostAgent.RunAsync($"Q{i}", session);
            await hostAgent.SaveSessionAsync("c-hit-many", session);
        }

        var finalSession = await hostAgent.GetOrCreateSessionAsync("c-hit-many");
        var stored = historyProvider.GetMessages(finalSession);

        Assert.True(stored.Count <= 20, $"expected window bounded at <=20, but stored.Count={stored.Count}");
        // 最新一輪一定還在(裁的是最舊的,不是最新的)。
        Assert.Contains(stored, m => m.Text == "Q14");
        Assert.Contains(stored, m => m.Text == "reply14");
    }
}
