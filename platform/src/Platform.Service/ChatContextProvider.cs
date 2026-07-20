using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Platform.Service.Abstractions;

namespace Platform.Service;

/// <summary>
/// 兩條聊天鏈路共用的 per-run context:固定護欄 prompt + mem0 長期記憶 recall。兩者都注入
/// <see cref="AIContext.Instructions"/>(不是 <see cref="AIContext.Messages"/>)——Messages 會被寫進持久化的
/// chat history 並每輪重複累積(1.13.0 實測,copilot-shared-core 03-design.md N3),Instructions 則每輪重組、
/// 不入 history,且與 agent 自身 instructions 換行共存(N4)。
///
/// 掛在 ChatClientAgent 內層(<c>ChatClientAgentOptions.AIContextProviders</c>)——路由命中而短路的那一輪
/// (<see cref="SkillRoutingAgent"/> middleware,P4)不會跑到這裡(§3.3),護欄/mem0 因此天然不會污染 skill
/// 摘要呼叫(02-spec §5.1):命中時走裸 <see cref="ILlmAgent"/>,完全不經過 ChatClientAgent(A-13)。
///
/// mem0 recall 全程 best-effort 只到 <c>Mem0Client</c> 內部這一層(其實作把下游錯誤吞掉回空字串);
/// 若 <see cref="IMem0Client"/> 違反契約而擲例外,本類不再包一層防禦性 try/catch,例外直接往上傳播(A-14)。
///
/// 兩顆 hosted agent("OperationsAssistant"/"ChatAssistant")是啟動期建立的 Singleton,本類因此不可在
/// 建構時捕捉 Scoped 服務(<see cref="IMem0Client"/>、<see cref="IChatIdentityAccessor"/>)——改持
/// <see cref="IServiceScopeFactory"/>,每次呼叫時開一個新 scope、用完即棄。
/// </summary>
public sealed class ChatContextProvider : AIContextProvider
{
    // mem0 記憶注入的固定前綴(逐字沿用 ChatService.cs 的 SystemMemoryPrefix,一字不改)。
    private const string SystemMemoryPrefix =
        "以下是你先前記住、關於這位使用者的長期記憶，回答時可參考（與當前問題無關者請忽略）：";

    // 每輪都注入的固定護欄(逐字沿用 ChatService.cs 的 ChatGuardPrompt,一字不改)。
    private const string ChatGuardPrompt =
        "回答前先判斷問題類型，不要急著搶答。若問題涉及任何數字、金額、比率、年增率（YoY）、統計、排名或跨期間比較，你「必須」先呼叫對應的 skill 工具，並只依工具回傳的結果作答。嚴禁在未呼叫工具的情況下自行給出數字；嚴禁自己做任何算術（加減乘除、百分比、成長率）——這類計算一律交給工具，因為你自行心算常常算錯。若沒有合適的工具、文件未提供該數據、或你無法確定，請直接說「查無此數據」，不要編造或估算。只有純聊天或不涉及數字的問題，才可直接回答。";

    private readonly IServiceScopeFactory _scopeFactory;

    public ChatContextProvider(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var identity = scope.ServiceProvider.GetRequiredService<IChatIdentityAccessor>();
        var mem0 = scope.ServiceProvider.GetRequiredService<IMem0Client>();

        var (uid, _) = identity.DeriveMemoryKeys();
        var lastUser = context.AIContext.Messages?.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";

        var instructions = ChatGuardPrompt;

        // best-effort 只到 IMem0Client 契約這一層(Mem0Client 內部吞錯);違反契約擲出的例外不吞,直接傳播(A-14)。
        var memories = await mem0.RecallAsync(uid, lastUser, cancellationToken);
        if (!string.IsNullOrWhiteSpace(memories))
        {
            instructions += "\n" + SystemMemoryPrefix + "\n" + memories;
        }

        return new AIContext { Instructions = instructions };

        // 刻意不覆寫 StoreAIContextAsync——remember/持久化在 ChatTurnRecorder(§3.3,兩個接縫拆分)。
    }
}
