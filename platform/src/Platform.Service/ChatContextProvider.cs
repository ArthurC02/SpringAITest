using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
/// mem0 recall 是 pipeline 不變式的 best-effort:無論 <see cref="IMem0Client"/> 的具體實作是否自行
/// 容錯,例外都只記 warning 並退化成沒有長期記憶。匿名聊天完全不讀 mem0,避免 caller-controlled
/// anonymous uid 造成跨訪客資料污染。
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
    private readonly ILogger<ChatContextProvider> _logger;

    public ChatContextProvider(IServiceScopeFactory scopeFactory, ILogger<ChatContextProvider> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var identity = scope.ServiceProvider.GetRequiredService<IChatIdentityAccessor>();

        var lastUser = context.AIContext.Messages?.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";

        var instructions = ChatGuardPrompt;

        // 匿名仍保留短期 session continuity，但不讀寫長期記憶。
        if (identity.CurrentUser is not null)
        {
            var mem0 = scope.ServiceProvider.GetRequiredService<IMem0Client>();
            var (uid, _) = identity.DeriveMemoryKeys();
            try
            {
                var memories = await mem0.RecallAsync(uid, lastUser, cancellationToken);
                if (!string.IsNullOrWhiteSpace(memories))
                {
                    instructions += "\n" + SystemMemoryPrefix + "\n" + memories;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "mem0 recall 失敗，略過長期記憶：{Message}", ex.Message);
            }
        }

        return new AIContext { Instructions = instructions };

        // 刻意不覆寫 StoreAIContextAsync——remember/持久化在 ChatTurnRecorder(§3.3,兩個接縫拆分)。
    }
}
