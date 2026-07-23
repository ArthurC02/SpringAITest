using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Platform.Service.Abstractions;
using Platform.Service.Dtos;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Platform.Service;

/// <summary>
/// 兩條聊天鏈路共用的確定性 skill 路由 middleware(copilot-shared-core P4,plans/copilot-shared-core/
/// 03-design.md §3.2/§4.2)。掛在 <see cref="ChatTurnRecorder"/> 之內、<c>ChatClientAgent</c> 之外
/// ——<see cref="ChatTurnRecorder"/> 的 mem0 remember + 持久化因此同時涵蓋「命中而短路」與「委派」兩條路徑
/// (該類別掛在本類外側,見其 XML doc)。
///
/// ponytail: skill 走確定性路由(本類),不註冊成 <see cref="AITool"/>。
/// 代價:命中 skill 的那一輪 client tools 不會被呼叫(01-plan §5.2/02-spec §5.2,B-P4-13 釘住此天花板)。
/// 好處:server 端呼叫不可能洩漏成 AG-UI 的 TOOL_CALL_* 事件——不靠 AGUI 那支 internal 的
///       FilterServerToolsFromMixedToolInvocationsAsync(preview、我們測不到),而是型別系統層級的保證:
///       skill 從未被包成 AITool,模型根本沒看過這個工具。
/// 升級路徑:量到「查數字 + 切視圖」混合指令的真實需求 → 改注入 messages 後正常 run,
///          但那會把受約束的 summary 併回自由主 run,須先解決 gpt-4o-mini 竄改數字的問題。
///
/// 路由/摘要呼叫刻意用「裸」<see cref="ILlmAgent"/>(<see cref="_bareLlm"/>),不經過 <c>innerAgent</c>
/// ——經 innerAgent 會被 ChatContextProvider 追加護欄+mem0、被 ChatHistoryProvider 前綴歷史,
/// 直接違反 02-spec §5.1「路由決策/摘要刻意不帶短期歷史與 mem0」。命中時短路直接回傳,委派邏輯
/// (未命中)原封走 <c>base.RunCoreAsync</c>/<c>base.RunCoreStreamingAsync</c>,不動 options 一個 byte
/// (client tools 照常流到內層 ChatClientAgent/FunctionInvokingChatClient)。
///
/// §9.3 鐵律:下方 <c>catch (Exception) → null 退純聊天</c> 只包在 <see cref="TryRouteAndExecuteAsync"/>
/// 內部(路由/工具那一段),絕不可包住 <c>base.RunCoreStreamingAsync</c> 或摘要的 <c>StreamAsync</c>
/// ——否則串流中途例外會被吞成靜默空話,而不是冒泡到 <see cref="ChatTurnRecorder"/>/
/// <c>ChatController</c> 產生 <c>event:error</c> 終止幀(T-P4-4/B-P4-13 的姊妹保證)。
///
/// 生命週期:兩顆 hosted agent 是啟動期建立的 Singleton,本類因此不可在建構時捕捉 Scoped 服務
/// (<see cref="IWorkflowService"/>、<see cref="IChatIdentityAccessor"/>)——改持
/// <see cref="IServiceScopeFactory"/>,每次呼叫時開一個新 scope、用完即棄(照 <see cref="ChatContextProvider"/>/
/// <see cref="ChatTurnRecorder"/> 的既有模式)。<see cref="ILlmAgent"/> 與
/// <see cref="InMemoryChatHistoryProvider"/> 皆為 Singleton,可直接持有。
/// </summary>
public sealed class SkillRoutingAgent : DelegatingAIAgent
{
    // 路由指令:LLM 只做「選工具」,只輸出工具名稱或 NONE;不帶歷史/mem0,避免污染路由判斷。
    // 逐字沿用 ChatService.cs 的 RoutingInstruction,一字不改(既有 ChatSkillRoutingTests 以此斷言)。
    private const string RoutingInstruction =
        "你是一個路由器。以下是可用工具，每行「名稱: 說明」。判斷使用者訊息最適合哪一個工具，只輸出那個工具的名稱（原樣、不加任何其他字）；若只是閒聊、打招呼、或不需要查資料／計算，只輸出 NONE。"
        + "若清單中有『說明明確對應到這個問題主題』的專門工具，優先選它；通用的知識庫檢索工具（例如一般文件問答）只有在沒有更專門的工具時才選。"
        // 與 ChatContextProvider 的護欄同一組數字語義:純聊天兜底會把這類問題判成「查無此數據」,所以路由這一關就必須把它們導向工具,否則等於沒答。
        + "特別注意：若使用者問題涉及數字、金額、比率、年增率（YoY）、統計、排名或跨期間比較，幾乎都需要專門工具查證,只要清單中有說明相符的工具就選它,不要因為題目像在算數學就輸出 NONE。"
        + "務必只輸出一個工具名稱或 NONE，不要多餘文字。";

    // 摘要指令:LLM 只把工具的確定性結果改寫成自然語言,嚴禁竄改任何數字(gpt-4o-mini 自行心算常算錯)。
    // 逐字沿用 ChatService.cs 的 SummaryInstruction,一字不改(02-spec §5.1 明令,ChatSkillRoutingTests 有字串相等斷言)。
    private const string SummaryInstruction =
        "把以下『工具結果』改寫成給使用者的自然、完整中文回覆。數字、金額、比率、百分比一字都不得更改、刪除或新增，只做語言潤飾與說明。若工具結果表示查無資料或發生錯誤，如實轉達，不要編造。";

    private readonly ILlmAgent _bareLlm;
    private readonly InMemoryChatHistoryProvider _historyProvider;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SkillRoutingAgent> _logger;

    public SkillRoutingAgent(
        AIAgent innerAgent,
        ILlmAgent bareLlm,
        InMemoryChatHistoryProvider historyProvider,
        IServiceScopeFactory scopeFactory,
        ILogger<SkillRoutingAgent> logger)
        : base(innerAgent)
    {
        _bareLlm = bareLlm;
        _historyProvider = historyProvider;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options,
        CancellationToken cancellationToken)
    {
        var messageList = messages.ToList();
        var lastUserMessage = messageList.LastOrDefault(m => m.Role == ChatRole.User);
        var lastUser = lastUserMessage?.Text ?? "";

        using var scope = _scopeFactory.CreateScope();
        var identity = scope.ServiceProvider.GetRequiredService<IChatIdentityAccessor>();
        var workflows = scope.ServiceProvider.GetRequiredService<IWorkflowService>();

        var summaryMessages = await TryRouteAndExecuteAsync(lastUser, identity.CurrentUser, workflows, cancellationToken);

        if (summaryMessages is null)
        {
            // 未命中:原樣委派,client tools / 短期記憶 / mem0 recall / 護欄全部照常(ChatClientAgent 內層)。
            return await base.RunCoreAsync(messageList, session, options, cancellationToken);
        }

        // 命中:用「裸」LLM 潤飾摘要,完全不經過 innerAgent(見類別頂端文件)。
        var reply = await _bareLlm.CompleteAsync(summaryMessages, cancellationToken);
        await AppendExchangeToSessionAsync(session, lastUserMessage, lastUser, reply, cancellationToken);

        return new AgentResponse(new ChatMessage(ChatRole.Assistant, reply));
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var messageList = messages.ToList();
        var lastUserMessage = messageList.LastOrDefault(m => m.Role == ChatRole.User);
        var lastUser = lastUserMessage?.Text ?? "";

        using var scope = _scopeFactory.CreateScope();
        var identity = scope.ServiceProvider.GetRequiredService<IChatIdentityAccessor>();
        var workflows = scope.ServiceProvider.GetRequiredService<IWorkflowService>();

        var summaryMessages = await TryRouteAndExecuteAsync(lastUser, identity.CurrentUser, workflows, cancellationToken);

        if (summaryMessages is null)
        {
            // §9.3 鐵律:委派呼叫本身絕不可包在路由的 try/catch 內——串流中途下游例外必須原樣冒泡到
            // ChatTurnRecorder/ChatController,補一個 event:error 終止幀,不可被本類吞成靜默空話。
            await foreach (var update in base.RunCoreStreamingAsync(messageList, session, options, cancellationToken))
            {
                yield return update;
            }

            yield break;
        }

        // 命中:用「裸」LLM 串流摘要,不經 innerAgent(理由同阻塞版)。
        var accumulated = new StringBuilder();
        await foreach (var chunk in _bareLlm.StreamAsync(summaryMessages, cancellationToken))
        {
            accumulated.Append(chunk);
            yield return new AgentResponseUpdate(ChatRole.Assistant, chunk);
        }

        await AppendExchangeToSessionAsync(session, lastUserMessage, lastUser, accumulated.ToString(), cancellationToken);
    }

    /// <summary>
    /// 路由命中那一輪走「裸」LLM,框架的 ChatHistoryProvider 沒機會自動記錄,手動把這輪交換寫回 session
    /// (承接 ChatService.AppendExchangeToSession 的短期記憶連續性)。session 理論上不會是 null
    /// (兩條鏈路皆先 GetOrCreateSession 才呼叫本 agent),但簽章上是 nullable,穩妥起見略過而非炸掉。
    ///
    /// P4 code review 修復(兩處連動,反編譯 Microsoft.Agents.AI(.Abstractions) 1.13.0 證實):
    /// 1) 裁切:本方法繞過 ChatClientAgent,SlidingWindowCompactionStrategy 只在
    ///    <c>InMemoryChatHistoryProvider.ProvideChatHistoryAsync</c>/<c>StoreChatHistoryAsync</c>(皆只被
    ///    ChatClientAgent 呼叫)內才會觸發——連續 skill HIT 時 session 因此無界成長。寫回前改呼叫
    ///    <see cref="InMemoryChatHistoryProvider.ChatReducer"/> 同一顆 reducer 實例(Program.cs 設定的
    ///    <c>SlidingWindowCompactionStrategy(MessagesExceed(20), minimumPreservedTurns:1).AsChatReducer()</c>)
    ///    手動裁切——<see cref="IChatReducer.ReduceAsync"/> 是純函式(反編譯
    ///    <c>ChatStrategyExtensions.CompactionStrategyChatReducer.ReduceAsync</c> 證實:每次呼叫都從完整
    ///    輸入重建 CompactionMessageIndex 再裁,不依賴呼叫序),與框架自動路徑等價,不必手寫裁切邏輯。
    /// 2) MessageId:HIT 輪寫回的訊息若無 MessageId,<see cref="Platform.Web.Infrastructure.AguiWireDedupAgent"/>
    ///    的去重(比對 stored.MessageId)就認不出這則已入史,AG-UI client 下一輪原樣重送整包 messages 時
    ///    會被誤判為新訊息而重複疊加。user 端沿用 wire 傳入的原始 ChatMessage(含其 MessageId);
    ///    assistant 端(裸 LLM 產出、原本沒有 id)生成新 Guid。
    /// </summary>
    private async Task AppendExchangeToSessionAsync(
        AgentSession? session, ChatMessage? userMessage, string message, string reply, CancellationToken cancellationToken)
    {
        if (session is null)
        {
            return;
        }

        var history = _historyProvider.GetMessages(session);
        history.Add(userMessage ?? new ChatMessage(ChatRole.User, message));
        history.Add(new ChatMessage(ChatRole.Assistant, reply) { MessageId = Guid.NewGuid().ToString() });

        if (_historyProvider.ChatReducer is { } reducer)
        {
            history = (await reducer.ReduceAsync(history, cancellationToken).ConfigureAwait(false)).ToList();
        }

        _historyProvider.SetMessages(session, history);
    }

    // ============================================================================
    // 以下逐字搬自 ChatService(02-spec §5:TryRouteAndExecuteAsync + 5 個私有方法 + 3 個 prompt 常數)。
    // 語意逐項保留,不放寬:匿名不路由 / 角色過濾 / builtin template-* 跳過 / SingleRequiredStringKey
    // 靜默跳過 / 最多兩次路由 / MatchTool 全等再寬鬆取最長名 / 路由決策不帶歷史與 mem0(裸 ILlmAgent)/
    // kb-query ABSTAIN → rag-qa 兜底 / 目錄失敗 best-effort 退純聊天 / 單一工具失敗回錯誤字串。
    // ============================================================================

    /// <summary>
    /// 測試專用進入點:直接取工具清單(不含路由決策),供既有 ChatSkillRoutingTests/ChatServiceTests 直接呼叫
    /// tool.InvokeAsync(...) 斷言(斷言面不變,只搬構造)。生產路徑從不呼叫此 overload——RunCoreAsync/
    /// RunCoreStreamingAsync 內的 TryRouteAndExecuteAsync 用同一個 scope 完成「路由 + 執行」。
    /// ponytail: 刻意不 Dispose 這個 scope,讓回傳的 LlmTool 委派在測試裡稍後呼叫 InvokeAsync 時仍能解析到
    /// 同一個 IWorkflowService 實例;僅測試進入點如此,天花板是每次呼叫多留一個小 scope(測試行程結束即回收)。
    /// </summary>
    internal Task<IReadOnlyList<LlmTool>?> BuildToolsAsync(UserContext? userCtx, CancellationToken ct)
    {
        var workflows = _scopeFactory.CreateScope().ServiceProvider.GetRequiredService<IWorkflowService>();
        return BuildToolsAsync(userCtx, workflows, ct);
    }

    /// <summary>
    /// 路由 + 執行(確定性管線)。userCtx 為 null / 無工具 / NONE / 任一步失敗 → 回 null,由呼叫端走純聊天兜底;
    /// 命中工具則執行工具並回「摘要訊息」,交由呼叫端做最後一次(阻塞或串流)LLM 潤飾。
    /// 整段以 try/catch 包住:任何例外都吞成 null(退純聊天)並記 warning——一輪聊天絕不可因路由失敗而 500。
    /// </summary>
    private async Task<IReadOnlyList<LlmMessage>?> TryRouteAndExecuteAsync(
        string message, UserContext? userCtx, IWorkflowService workflows, CancellationToken ct)
    {
        if (userCtx is null)
        {
            return null;   // 匿名不路由(Skill 需租戶身分),直接純聊天。
        }

        try
        {
            var tools = await BuildToolsAsync(userCtx, workflows, ct);
            if (tools is null || tools.Count == 0)
            {
                return null;
            }

            // 路由最多兩次:第一次 NONE/無命中就再試一次(路由 LLM 偶爾漏選專門工具),第二次仍不中才退純聊天。
            LlmTool? selected = null;
            string lastReply = "";
            var attempt = 0;
            for (attempt = 1; attempt <= 2; attempt++)
            {
                var (tool, reply) = await RouteAsync(tools, message, ct);
                lastReply = reply;
                if (tool is not null)
                {
                    selected = tool;
                    break;
                }
            }

            LogRouteDecision(selected, lastReply, attempt);

            if (selected is null)
            {
                return null;   // 兩次皆 NONE 或無法解析 → 純聊天。
            }

            // 執行確定性工具:以使用者原訊息為輸入;回傳已是抽取後的答案字串(business_result 等)或工具自身的錯誤/查無字串。
            var toolResult = await selected.InvokeAsync(message, ct);
            return BuildSummaryMessages(message, toolResult);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "聊天路由/執行失敗，退回純聊天：{訊息}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 路由:以工具目錄(每行「名稱: 說明」)+ 使用者訊息做一次「無工具」的路由呼叫,回選中的工具或 null(NONE/無法解析)。
    /// 刻意不帶短期歷史與 mem0,避免污染路由判斷。
    /// </summary>
    private async Task<(LlmTool? Tool, string Reply)> RouteAsync(IReadOnlyList<LlmTool> tools, string message, CancellationToken ct)
    {
        var catalog = string.Join("\n", tools.Select(t => $"{t.Name}: {t.Description}"));
        var routeMessages = new List<LlmMessage>
        {
            new("system", RoutingInstruction + "\n\n" + catalog),
            new("user", message),
        };

        var reply = (await _bareLlm.CompleteAsync(routeMessages, ct)).Trim();
        return (MatchTool(reply, tools), reply);
    }

    /// <summary>
    /// 記一行路由決策供 `docker compose logs platform` 觀察路由漏選:命中工具名 / NONE / no-match,以及第幾次嘗試命中。
    /// 只帶工具名 + 短原始回覆片段,不記完整使用者訊息。
    /// </summary>
    private void LogRouteDecision(LlmTool? selected, string lastReply, int attempt)
    {
        string outcome;
        if (selected is not null)
        {
            outcome = selected.Name;
        }
        else if (string.IsNullOrWhiteSpace(lastReply) || lastReply.Equals("NONE", StringComparison.OrdinalIgnoreCase))
        {
            outcome = "NONE";
        }
        else
        {
            outcome = "no-match";
        }

        var snippet = lastReply.Length > 40 ? lastReply[..40] : lastReply;
        _logger.LogInformation(
            "聊天路由決策：{結果}（第 {嘗試} 次嘗試；原始回覆片段：{片段}）",
            outcome, Math.Min(attempt, 2), snippet);
    }

    /// <summary>
    /// 把路由回覆對回工具:先大小寫無關全等,再寬鬆包含比對(取最長名稱避免前綴誤命中);空白 / NONE / 無命中 → null。
    /// </summary>
    private static LlmTool? MatchTool(string reply, IReadOnlyList<LlmTool> tools)
    {
        if (string.IsNullOrWhiteSpace(reply) || reply.Equals("NONE", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        foreach (var t in tools)
        {
            if (reply.Equals(t.Name, StringComparison.OrdinalIgnoreCase))
            {
                return t;
            }
        }

        return tools
            .Where(t => reply.Contains(t.Name, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(t => t.Name.Length)
            .FirstOrDefault();
    }

    /// <summary>摘要訊息:system(禁改數字)+ user(原問題 + 工具結果)。LLM 只潤飾,不計算。</summary>
    private static IReadOnlyList<LlmMessage> BuildSummaryMessages(string message, string toolResult) =>
        new List<LlmMessage>
        {
            new("system", SummaryInstruction),
            new("user", $"使用者問題：{message}\n\n工具結果：\n{toolResult}"),
        };

    /// <summary>
    /// 輸出取字串答案時依序嘗試的 key;都沒有就整包序列化回給模型。
    /// business_result 排最前:template-* composed skill 的 nl_logic 節點把「使用者規則套用後」的權威答案
    /// 寫入 business_result(retrieval 型同時有套規則前的 final_answer),須先於 final_answer 命中。
    /// </summary>
    private static readonly string[] OutputKeys = { "business_result", "answer", "final_answer", "report", "summary" };

    /// <summary>
    /// 已登入(userCtx 非 null)時把可用的 Skill 目錄包成聊天工具;匿名聊天不掛工具(Skill 需要租戶身分)。
    /// 工具來源單軌:動態 Skill 目錄(內建+自訂,租戶已由下游過濾)。
    /// 目錄抓取失敗採 best-effort:這輪回空工具清單(類比 mem0 吞錯),聊天照常走純聊天兜底,不炸。
    /// 工具失敗回錯誤文字給模型照實轉述,不讓整輪聊天失敗。
    /// </summary>
    private async Task<IReadOnlyList<LlmTool>?> BuildToolsAsync(UserContext? userCtx, IWorkflowService workflows, CancellationToken ct)
    {
        if (userCtx is null)
        {
            return null;
        }

        JsonElement catalog;
        try
        {
            catalog = await workflows.GetSkillCatalogAsync(userCtx, ct);
        }
        catch (Exception ex)
        {
            // best-effort:目錄抓不到不讓聊天炸;沒有靜態工具可退了,這輪就是空清單(純聊天兜底接手)。
            _logger.LogWarning(ex, "Skill 目錄取得失敗,本輪無可用工具:{訊息}", ex.Message);
            return Array.Empty<LlmTool>();
        }

        // ponytail: 每輪 GET /skills 的 N+1;若量到痛 → IMemoryCache per-(tenant,role) 30–60s TTL。
        return SkillCatalogToTools(catalog, userCtx, workflows);
    }

    /// <summary>
    /// Skill 目錄(原樣穿透的 JsonElement 陣列)→ 聊天工具。純函式,可獨立單測。
    /// 過濾:template-* 內建骨架(空殼,不可路由)→ 跳過;角色不符 → 跳過;
    /// 非「恰好一個必填字串輸入」→ 跳過(P1 天花板,多參/非字串待 P3)。
    /// </summary>
    private IReadOnlyList<LlmTool> SkillCatalogToTools(JsonElement catalog, UserContext userCtx, IWorkflowService workflows)
    {
        var tools = new List<LlmTool>();
        if (catalog.ValueKind != JsonValueKind.Array)
        {
            return tools;
        }

        foreach (var item in catalog.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("name", out var nameEl)
                || nameEl.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var name = nameEl.GetString()!;

            // template-* 內建骨架是空殼,不可被路由當工具(與前端同一條規則)。
            var source = item.TryGetProperty("source", out var s) ? s.GetString() : null;
            if (source == "builtin" && name.StartsWith("template-", StringComparison.Ordinal))
            {
                continue;
            }

            // 角色過濾:required_role 空或 =="USER" 視為無限制,否則要求完全相符。
            var requiredRole = item.TryGetProperty("required_role", out var r) ? r.GetString() : "USER";
            if (!IsRoleAllowed(requiredRole, userCtx.Role))
            {
                continue;
            }

            // 挑輸入鍵:input_schema 中唯一的必填 str 欄位;挑不出 → P1 跳過。
            var inputKey = SingleRequiredStringKey(item);
            if (inputKey is null)
            {
                continue;
            }

            var description = item.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
            var desc = $"{description}（輸入 {inputKey}:一段自然語言）";

            // name / inputKey 是本輪迭代的區域變數,lambda 直接捕捉即安全。
            tools.Add(new LlmTool(name, desc,
                (arg, ct) => InvokeSkillToolAsync(name, inputKey, arg, userCtx, workflows, ct)));
        }

        return tools;
    }

    private static bool IsRoleAllowed(string? requiredRole, string userRole)
        => string.IsNullOrEmpty(requiredRole) || requiredRole == "USER" || requiredRole == userRole;

    /// <summary>
    /// input_schema({ key: {type, required, min_length, default} } 或 null)中挑出唯一的必填欄位,
    /// 且該欄位型別為 str → 回其鍵名;否則回 null。
    /// 「唯一必填」才路由:多於一個必填欄位(即使其中一個是 str)會導致只填一參的錯參呼叫,故一律跳過。
    /// 零個必填、唯一必填但非 str、或 input_schema 非物件 → 回 null(P1 天花板,多參待 P3)。
    /// </summary>
    private static string? SingleRequiredStringKey(JsonElement item)
    {
        if (!item.TryGetProperty("input_schema", out var schema) || schema.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? found = null;
        foreach (var field in schema.EnumerateObject())
        {
            var f = field.Value;
            if (f.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var required = f.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.True;
            if (!required)
            {
                continue;   // optional 欄位不影響單參資格。
            }

            if (found is not null)
            {
                return null; // 第二個必填欄位(不論型別)→ 多參,跳過。
            }

            var isStr = f.TryGetProperty("type", out var t)
                && t.ValueKind == JsonValueKind.String && t.GetString() == "str";
            if (!isStr)
            {
                return null; // 唯一必填但非字串 → 跳過。
            }

            found = field.Name;
        }

        return found;
    }

    /// <summary>
    /// Skill 版工具委派:打 /skills/{name}/invoke,輸入鍵由 SingleRequiredStringKey 從 input_schema 挑出。
    /// 失敗回錯誤字串給模型轉述,不炸整輪聊天。
    /// kb-query 證據不足棄答時,確定性退回 rag-qa 兜底(不依賴模型自己補打第二刀),並如實註明。
    /// ponytail: kb-query→rag-qa 是寫死的專屬特判,等有第二顆需要同類兜底的 skill 再抽象成表驅動。
    /// </summary>
    private async Task<string> InvokeSkillToolAsync(
        string name, string inputKey, string arg, UserContext userCtx, IWorkflowService workflows, CancellationToken ct)
    {
        try
        {
            var input = new Dictionary<string, JsonElement>
            {
                [inputKey] = JsonSerializer.SerializeToElement(arg),
            };
            var res = await workflows.InvokeSkillAsync(name, input, userCtx, ct);

            if (name == "kb-query" && IsAbstain(res))
            {
                var ragInput = new Dictionary<string, JsonElement>
                {
                    ["question"] = JsonSerializer.SerializeToElement(arg),
                };
                var rag = await workflows.InvokeSkillAsync("rag-qa", ragInput, userCtx, ct);
                return "嚴格稽核查詢因證據不足而棄答;以下是一般知識庫檢索(不含稽核保證)的結果:"
                    + ExtractSkillAnswer(rag);
            }

            return ExtractSkillAnswer(res);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "聊天工具 {工具} 呼叫 skill 失敗:{訊息}", name, ex.Message);
            return $"Skill {name} 呼叫失敗:{ex.Message}";
        }
    }

    /// <summary>skill invoke 回應形狀 {skill, output:{…}} 取外層 output(無則回根本身),供取答案字串與棄答判定共用。</summary>
    private static JsonElement SkillOutputElement(JsonElement res) =>
        res.ValueKind == JsonValueKind.Object && res.TryGetProperty("output", out var o) ? o : res;

    // ponytail: 與 ChatController.StreamErrorMessage 同字面(Web→Service 單向依賴,Service 取不到 Web 常數,只能同步一份)。
    private const string FatalRunFallback = "回覆過程發生錯誤，請稍後再試";

    /// <summary>依 OutputKeys 從 output 取字串答案;無答案鍵時,致命/錯誤 run 回友善訊息,否則回整包 raw JSON。</summary>
    private static string ExtractSkillAnswer(JsonElement res)
    {
        var output = SkillOutputElement(res);
        foreach (var key in OutputKeys)
        {
            if (output.ValueKind == JsonValueKind.Object
                && output.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            {
                return v.GetString()!;
            }
        }

        // 無答案鍵 + fatal run(agentic 遞迴/逾時/套件讀取錯誤):輸出只剩 trace/fatal_error/errors 等內部欄位,
        // 不可整包丟給聊天模型改寫給非技術使用者;回固定友善訊息。純 flow(無 fatal_error/errors)維持原 raw fallback。
        if (IsFatalRun(output))
        {
            return FatalRunFallback;
        }

        return output.GetRawText();
    }

    /// <summary>output 帶 fatal_error 或非空 errors → agentic run 致命失敗(無 answer 鍵時據此改回友善訊息)。</summary>
    private static bool IsFatalRun(JsonElement output)
    {
        if (output.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (output.TryGetProperty("fatal_error", out var fatal)
            && fatal.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            return true;
        }

        return output.TryGetProperty("errors", out var errors)
            && errors.ValueKind == JsonValueKind.Array
            && errors.GetArrayLength() > 0;
    }

    /// <summary>output.answer_mode == "ABSTAIN" → kb-query 稽核閘門判定證據不足而棄答。</summary>
    private static bool IsAbstain(JsonElement res)
    {
        var output = SkillOutputElement(res);
        return output.ValueKind == JsonValueKind.Object
            && output.TryGetProperty("answer_mode", out var mode)
            && mode.ValueKind == JsonValueKind.String
            && mode.GetString() == "ABSTAIN";
    }
}
