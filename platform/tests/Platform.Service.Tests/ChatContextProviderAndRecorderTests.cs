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
        var (hostAgent, _, _) = TestChatAgent.Build(chatClient, mem0);

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
        var (hostAgent, _, _) = TestChatAgent.Build(chatClient, mem0);

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
}
