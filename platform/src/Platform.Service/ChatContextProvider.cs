using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Platform.Service.Abstractions;

namespace Platform.Service;

/// <summary>
/// 兩條聊天鏈路共用的 per-run context:persona + 固定護欄 prompt + mem0 長期記憶 recall。三者都注入
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
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ChatContextProvider> _logger;
    private readonly string? _transportPersona;

    /// <param name="transportPersona">
    /// 本鏈路的 persona(P1:AG-UI 傳操作助理 persona,<c>/api/chat*</c> 傳 null —— 該鏈路刻意沒有 persona)。
    /// 改由本類逐輪組進 Instructions(而非 <c>ChatOptions.Instructions</c>),persona 才能跟著 manifest
    /// 逐租戶替換;組出的位元與框架原本「agent instructions 換行接 context instructions」完全相同。
    /// </param>
    public ChatContextProvider(
        IServiceScopeFactory scopeFactory,
        ILogger<ChatContextProvider> logger,
        string? transportPersona = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _transportPersona = transportPersona;
    }

    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var identity = scope.ServiceProvider.GetRequiredService<IChatIdentityAccessor>();

        var lastUser = context.AIContext.Messages?.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";

        // P1:manifest-aware 組成。PROMPT_ARTIFACTS_ENABLED 關閉時 resolver 根本沒註冊(GetService 回 null),
        // 直接用 constants —— 旗標關閉時逐位元不變是結構保證(plans/…/03-prompt-model-runtime-plan.md §7)。
        var resolver = scope.ServiceProvider.GetService<PromptCompositionResolver>();
        var prompts = resolver is null
            ? PromptComposition.Defaults(_transportPersona)
            : await resolver.ResolveAsync(
                identity.CurrentUser?.TenantCode, _transportPersona, identity, cancellationToken);

        string? memories = null;

        // 匿名仍保留短期 session continuity，但不讀寫長期記憶。
        if (identity.CurrentUser is not null)
        {
            var mem0 = scope.ServiceProvider.GetRequiredService<IMem0Client>();
            var (uid, _) = identity.DeriveMemoryKeys();
            try
            {
                memories = await mem0.RecallAsync(uid, lastUser, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "mem0 recall 失敗，略過長期記憶：{Message}", ex.Message);
            }
        }

        return new AIContext { Instructions = prompts.Instructions(memories) };

        // 刻意不覆寫 StoreAIContextAsync——remember/持久化在 ChatTurnRecorder(§3.3,兩個接縫拆分)。
    }
}
