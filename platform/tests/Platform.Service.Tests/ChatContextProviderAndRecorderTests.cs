using Platform.Service.Dtos;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Platform.Service.Tests;

/// <summary>
/// P3(copilot-shared-core §11 步驟 12,04-acceptance-test.md §4.3)—— <see cref="ChatContextProvider"/> 與
/// <see cref="ChatTurnRecorder"/> 的直接行為驗收。既有 <c>ChatServiceTests</c>/<c>ChatBehaviorBaselineTests</c>
/// 已透過 <c>ChatAsync</c>/<c>StreamChatAsync</c> 間接覆蓋這些行為(重構前後等價的證明);本檔額外提供
/// 明確點名 T-P3-1/T-P3-2/T-P4-4(前半)的直接斷言,避免這些案例只能從其他測試的斷言旁推。
/// </summary>
public sealed class ChatContextProviderAndRecorderTests
{
    private static readonly UserContext UserA = new("user-a", "demo-a", "USER");

    // T-P3-1:ChatContextProvider 產出的 Instructions 含 ChatGuardPrompt;mem0 非空時含
    // SystemMemoryPrefix + 記憶內容;訊息列(AIContext.Messages)不含任何注入文字——protected
    // ProvideAIContextAsync 無法直接呼叫,故經 hostAgent.RunAsync 驅動,斷言送進 chat client 的
    // ChatOptions.Instructions 與 messages。
    [Fact]
    public async Task TP3_1_ProviderInjectsGuardAndMem0IntoInstructions_NotIntoMessages()
    {
        var chatClient = new FakeChatClient();
        var mem0 = new FakeMem0Client { RecallResult = "- 使用者喜歡貓\n" };
        var identity = new FakeChatIdentityAccessor();
        identity.SetRequestKeys("u1", "t-p3-1", UserA);
        var (hostAgent, _, _) = TestChatAgent.Build(chatClient, mem0, identity: identity);

        var session = await hostAgent.GetOrCreateSessionAsync("t-p3-1");
        await hostAgent.RunAsync("問題", session);

        var instructions = chatClient.LastOptions!.Instructions!;
        Assert.StartsWith("回答前先判斷問題類型", instructions);
        Assert.Contains("以下是你先前記住、關於這位使用者的長期記憶", instructions);
        Assert.Contains("使用者喜歡貓", instructions);

        // AIContext.Messages 為空:訊息列裡不應出現任何 system 角色的注入訊息(N3 的修正)。
        Assert.DoesNotContain(chatClient.LastMessages!, m => m.Role == ChatRole.System);
    }

    // T-P3-2:連續兩輪,第二輪送進 chat client 的 messages 不含任何 mem0 前言(N3 不再發生);
    // Instructions 只有一份護欄(不雙重注入)。
    [Fact]
    public async Task TP3_2_SecondRound_MessagesDoNotAccumulateMem0Preamble_InstructionsNotDoubled()
    {
        var chatClient = new FakeChatClient();
        var mem0 = new FakeMem0Client { RecallResult = "- 使用者是租戶 A\n" };
        var identity = new FakeChatIdentityAccessor();
        identity.SetRequestKeys("u1", "t-p3-2", UserA);
        var (hostAgent, _, _) = TestChatAgent.Build(chatClient, mem0, identity: identity);

        var session = await hostAgent.GetOrCreateSessionAsync("t-p3-2");
        await hostAgent.RunAsync("第一問", session);
        await hostAgent.SaveSessionAsync("t-p3-2", session);

        var session2 = await hostAgent.GetOrCreateSessionAsync("t-p3-2");
        await hostAgent.RunAsync("第二問", session2);

        // 第二輪的訊息列(含框架管理的短期歷史)不含任何 mem0 前言字串——不再逐輪累積(N3 修正)。
        Assert.DoesNotContain(chatClient.LastMessages!, m => (m.Text ?? "").Contains("使用者是租戶 A"));

        // Instructions 只出現一份護欄(不是護欄疊護欄)。
        var instructions = chatClient.LastOptions!.Instructions!;
        var guardOccurrences = instructions.Split("回答前先判斷問題類型").Length - 1;
        Assert.Equal(1, guardOccurrences);
    }

    // T-P4-4 前半(先釘住,P4 才會有完整的 middleware 版本):串流中途 IChatClient 拋例外 → ChatTurnRecorder
    // 零副作用(RememberAsync/AddAsync 皆零呼叫)。直接驅動 hostAgent.RunStreamingAsync(繞過 ChatService),
    // 專門隔離驗證 recorder 自身的行為,而非整條鏈路。
    [Fact]
    public async Task TP4_4_StreamingMidFailure_RecorderHasZeroSideEffects()
    {
        var chatClient = new FakeChatClient { Chunks = new[] { "半", "截" }, ThrowAfterChunks = 1 };
        var mem0 = new FakeMem0Client();
        var convos = new FakeConversationStore();
        var identity = new FakeChatIdentityAccessor();
        identity.SetRequestKeys(null, null, UserA); // 已登入身分:證明「本該持久化」但因例外提前中止而完全沒有嘗試。
        var (hostAgent, _, _) = TestChatAgent.Build(chatClient, mem0, convos, identity);

        var session = await hostAgent.GetOrCreateSessionAsync("t-p4-4");

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in hostAgent.RunStreamingAsync("問題", session))
            {
            }
        });

        Assert.Empty(mem0.Remembered);
        Assert.Empty(convos.Saved);
    }

    // ================================================================
    // D6:AgentChatRoutingAgent 短路(canary 命中)那一輪的 recorder 語意(表 4 #12/#13)。
    // recorder 掛在短路層「之外」(Program.cs:218-221/261-263),所以就算本輪完全沒經過 SkillRoutingAgent
    // 與 ChatClientAgent,持久化 + mem0 remember 仍必須各發生恰好一次,且帶上 Root Orchestrator 的 lineage。
    // ================================================================

    private static readonly ChatTurnMetadata RootLineage = new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        2,
        Guid.Parse("22222222-2222-2222-2222-222222222222"),
        3,
        Guid.Parse("33333333-3333-3333-3333-333333333333"));

    private const string RoutableCatalog = """
    [ { "name":"kb-query", "description":"知識庫檢索", "required_role":"USER", "source":"builtin",
        "input_schema": { "query": { "type":"str", "required":true } } } ]
    """;

    private static FakeWorkflowEngineClient RoutableWorkflows() => new()
    {
        Catalog = System.Text.Json.JsonDocument.Parse(RoutableCatalog).RootElement.Clone(),
    };

    [Fact]
    public async Task AgentChatRouted_ShortCircuits_StillPersistsAndRemembersExactlyOnce_WithLineage()
    {
        var chatClient = new FakeChatClient();
        var mem0 = new FakeMem0Client();
        var convos = new FakeConversationStore();
        var workflows = RoutableWorkflows();
        var identity = new FakeChatIdentityAccessor();
        identity.SetRequestKeys("u1", "d6-hit", UserA);
        var agentChat = new FakeAgentChatRuntime
        {
            Reply = "Root Orchestrator 的答案",
            Metadata = RootLineage,
        };
        var (hostAgent, _, _) = TestChatAgent.Build(
            chatClient, mem0, convos, identity, workflows: workflows, agentChat: agentChat);

        var session = await hostAgent.GetOrCreateSessionAsync("d6-hit");
        var response = await hostAgent.RunAsync("這季毛利率多少?", session);

        Assert.Equal("Root Orchestrator 的答案", response.Text);

        // 短路層拿到的是「已推導」的 conversationId(含租戶:使用者前綴),不是 body 原值。
        var call = Assert.Single(agentChat.Calls);
        Assert.Equal("這季毛利率多少?", call.Message);
        Assert.Equal("demo-a:user-a:d6-hit", call.ConversationId);

        // 這一輪完全不經過 skill 路由與 ChatClientAgent(短路的定義)。
        Assert.Empty(workflows.CatalogContexts);
        Assert.Empty(chatClient.Calls);

        // 但 recorder 在外側,持久化與 remember 各恰好一次,且 D6 lineage 有交給 store。
        Assert.Equal(("這季毛利率多少?", "Root Orchestrator 的答案"), Assert.Single(convos.Saved));
        Assert.Same(RootLineage, Assert.Single(convos.SavedMetadata));
        Assert.Equal(
            ("demo-a:user-a", "這季毛利率多少?", "Root Orchestrator 的答案"),
            Assert.Single(mem0.Remembered));
    }

    // 阻塞半:持久化失敗 → 只留訊號(由 ChatService 升級成 500),不 remember —— 與 legacy 路徑同語意。
    [Fact]
    public async Task AgentChatRouted_BlockingPersistFailure_SignalsFailure_DoesNotRemember()
    {
        var mem0 = new FakeMem0Client();
        var convos = new FakeConversationStore { ThrowOnAdd = true };
        var identity = new FakeChatIdentityAccessor();
        identity.SetRequestKeys("u1", "d6-persist-fail", UserA);
        var agentChat = new FakeAgentChatRuntime { Reply = "Root 答案", Metadata = RootLineage };
        var (hostAgent, _, _) = TestChatAgent.Build(
            new FakeChatClient(), mem0, convos, identity, agentChat: agentChat);

        var session = await hostAgent.GetOrCreateSessionAsync("d6-persist-fail");
        var response = await hostAgent.RunAsync("問題", session);

        Assert.Equal("Root 答案", response.Text);
        Assert.IsType<Platform.Service.Exceptions.BackendCallException>(identity.PersistFailure);
        Assert.Empty(mem0.Remembered);
    }

    // 串流半:持久化 best-effort —— 已送出的短路答案照常送達,且仍 remember(與阻塞刻意不同)。
    [Fact]
    public async Task AgentChatRouted_StreamingPersistFailure_IsBestEffort_StillStreamsAndRemembers()
    {
        var mem0 = new FakeMem0Client();
        var convos = new FakeConversationStore { ThrowOnAdd = true };
        var identity = new FakeChatIdentityAccessor();
        identity.SetRequestKeys("u1", "d6-stream-persist-fail", UserA);
        var agentChat = new FakeAgentChatRuntime { Reply = "Root 串流答案", Metadata = RootLineage };
        var (hostAgent, _, _) = TestChatAgent.Build(
            new FakeChatClient(), mem0, convos, identity, agentChat: agentChat);

        var session = await hostAgent.GetOrCreateSessionAsync("d6-stream-persist-fail");
        var collected = new List<string>();
        await foreach (var update in hostAgent.RunStreamingAsync("問題", session))
        {
            collected.Add(update.Text ?? string.Empty);
        }

        Assert.Equal("Root 串流答案", string.Concat(collected));
        Assert.IsType<Platform.Service.Exceptions.BackendCallException>(identity.PersistFailure);
        Assert.Equal(
            ("demo-a:user-a", "問題", "Root 串流答案"),
            Assert.Single(mem0.Remembered));
    }

    // 串流 × 持久化成功(上面兩個短路案例只涵蓋「阻塞×成功」「阻塞×失敗」「串流×失敗」):短路那一輪
    // 串流送出的答案除了 remember,也必須原文寫進 store 並帶上 D6 lineage —— 串流累積的是短路層那唯一
    // 一塊 update,不是 ChatClientAgent 的塊。
    [Fact]
    public async Task AgentChatRouted_StreamingShortCircuit_PersistsStreamedReplyAndRemembers()
    {
        var mem0 = new FakeMem0Client();
        var convos = new FakeConversationStore();
        var identity = new FakeChatIdentityAccessor();
        identity.SetRequestKeys("u1", "d6-stream-hit", UserA);
        var agentChat = new FakeAgentChatRuntime { Reply = "Root 串流答案", Metadata = RootLineage };
        var (hostAgent, _, _) = TestChatAgent.Build(
            new FakeChatClient(), mem0, convos, identity, agentChat: agentChat);

        var session = await hostAgent.GetOrCreateSessionAsync("d6-stream-hit");
        var collected = new List<string>();
        await foreach (var update in hostAgent.RunStreamingAsync("問題", session))
        {
            collected.Add(update.Text ?? string.Empty);
        }

        Assert.Equal("Root 串流答案", string.Concat(collected));
        Assert.Equal(("問題", "Root 串流答案"), Assert.Single(convos.Saved));
        Assert.Same(RootLineage, Assert.Single(convos.SavedMetadata));
        Assert.Equal(
            ("demo-a:user-a", "問題", "Root 串流答案"),
            Assert.Single(mem0.Remembered));
        Assert.Null(identity.PersistFailure);
        // 持久化後的 backend 回應(含 Id)回填給呼叫端的管道,短路輪同樣要有。
        Assert.Equal("Root 串流答案", identity.PersistedResponse!.Reply);
    }

    [Fact]
    public async Task Mem0Recall_CallerRequestedCancellation_Propagates()
    {
        using var cancellation = new CancellationTokenSource();
        var mem0 = new FakeMem0Client
        {
            OnRecall = _ => cancellation.Cancel(),
            ThrowOnRecall = new OperationCanceledException(cancellation.Token),
        };
        var identity = new FakeChatIdentityAccessor();
        identity.SetRequestKeys("u1", "cancel-recall", UserA);
        var (hostAgent, _, _) = TestChatAgent.Build(mem0: mem0, identity: identity);
        var session = await hostAgent.GetOrCreateSessionAsync("cancel-recall");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => hostAgent.RunAsync("問題", session, cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task Mem0Remember_CallerRequestedCancellation_Propagates()
    {
        using var cancellation = new CancellationTokenSource();
        var mem0 = new FakeMem0Client
        {
            OnRemember = _ => cancellation.Cancel(),
            ThrowOnRemember = new OperationCanceledException(cancellation.Token),
        };
        var identity = new FakeChatIdentityAccessor();
        identity.SetRequestKeys("u1", "cancel-remember", UserA);
        var (hostAgent, _, _) = TestChatAgent.Build(mem0: mem0, identity: identity);
        var session = await hostAgent.GetOrCreateSessionAsync("cancel-remember");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => hostAgent.RunAsync("問題", session, cancellationToken: cancellation.Token));
    }

    // 上面兩案的另一半等價類:mem0 擲 OperationCanceledException,但呼叫端自己的 token 從未被取消
    // (例如下游自行設限的逾時借用了同一種例外型別)。`!cancellationToken.IsCancellationRequested`
    // 這個守衛存在的唯一理由就是把這一類跟「呼叫端真的取消」分開——它必須照 best-effort 吞掉:
    // recall 退化成沒有長期記憶、remember 變 no-op,聊天照常完成且持久化不受影響。
    [Fact]
    public async Task Mem0Cancellation_WithoutCallerCancellation_IsSwallowedAsBestEffort()
    {
        var chatClient = new FakeChatClient();
        var mem0 = new FakeMem0Client
        {
            ThrowOnRecall = new OperationCanceledException(),
            ThrowOnRemember = new OperationCanceledException(),
        };
        var convos = new FakeConversationStore();
        var identity = new FakeChatIdentityAccessor();
        identity.SetRequestKeys("u1", "mem0-oce-uncancelled", UserA);
        var (hostAgent, _, _) = TestChatAgent.Build(chatClient, mem0, convos, identity);

        var session = await hostAgent.GetOrCreateSessionAsync("mem0-oce-uncancelled");
        var response = await hostAgent.RunAsync("問題", session);

        // 聊天照常走到 LLM 並完成;recall 失敗只是「這輪沒有長期記憶可注入」,護欄仍在。
        Assert.Equal("測試回覆", response.Text);
        var instructions = chatClient.LastOptions!.Instructions!;
        Assert.StartsWith("回答前先判斷問題類型", instructions);
        Assert.DoesNotContain("以下是你先前記住、關於這位使用者的長期記憶", instructions);

        // remember 的例外同樣被吞掉:沒記住任何東西,但先行的持久化照常成功、無失敗訊號。
        Assert.Empty(mem0.Remembered);
        Assert.Equal(("問題", "測試回覆"), Assert.Single(convos.Saved));
        Assert.Null(identity.PersistFailure);
    }
}
