using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Platform.Service.Abstractions;
using Platform.Service.Dtos;
using Platform.Service.Options;
using Microsoft.Extensions.Logging;

namespace Platform.Service;

/// <summary>
/// 聊天服務。短期記憶(視窗)與長期記憶(mem0)兩層合成 prompt:
/// 短期提供先前訊息、mem0 提供 system 前言。
/// 阻塞式 chat 與串流 streamChat 共用正規化與 prompt 組裝。
/// 持久化與歷史改走 backend(IConversationStore);mem0、短期記憶、Agent Framework、SSE、業務 span 全不動。
/// </summary>
public sealed class ChatService : IChatService
{
    // 業務 span 的 ActivitySource;名稱要與 OTel 註冊的 source 名一致。
    private static readonly ActivitySource ActivitySource = new("chat.service");

    // mem0 記憶注入的固定前綴(逐字);與 memories 之間以一個換行相接。
    private const string SystemMemoryPrefix =
        "以下是你先前記住、關於這位使用者的長期記憶，回答時可參考（與當前問題無關者請忽略）：";

    // 每輪都注入的固定護欄:抑制模型自行心算/編造文件數字,改走 skill 工具。與 mem0 前言生命週期獨立,是獨立的一則 system。
    // 只用於純聊天兜底路徑(路由路徑改由確定性管線把關,不靠模型自律)。
    private const string ChatGuardPrompt =
        "回答前先判斷問題類型，不要急著搶答。若問題涉及任何數字、金額、比率、年增率（YoY）、統計、排名或跨期間比較，你「必須」先呼叫對應的 skill 工具，並只依工具回傳的結果作答。嚴禁在未呼叫工具的情況下自行給出數字；嚴禁自己做任何算術（加減乘除、百分比、成長率）——這類計算一律交給工具，因為你自行心算常常算錯。若沒有合適的工具、文件未提供該數據、或你無法確定，請直接說「查無此數據」，不要編造或估算。只有純聊天或不涉及數字的問題，才可直接回答。";

    // 路由指令:LLM 只做「選工具」,只輸出工具名稱或 NONE;不帶歷史/mem0,避免污染路由判斷。
    private const string RoutingInstruction =
        "你是一個路由器。以下是可用工具，每行「名稱: 說明」。判斷使用者訊息最適合哪一個工具，只輸出那個工具的名稱（原樣、不加任何其他字）；若只是閒聊、打招呼、或不需要查資料／計算，只輸出 NONE。"
        + "若清單中有『說明明確對應到這個問題主題』的專門工具，優先選它；通用的知識庫檢索工具（例如一般文件問答）只有在沒有更專門的工具時才選。務必只輸出一個工具名稱或 NONE，不要多餘文字。";

    // 摘要指令:LLM 只把工具的確定性結果改寫成自然語言,嚴禁竄改任何數字(gpt-4o-mini 自行心算常算錯)。
    private const string SummaryInstruction =
        "把以下『工具結果』改寫成給使用者的自然、完整中文回覆。數字、金額、比率、百分比一字都不得更改、刪除或新增，只做語言潤飾與說明。若工具結果表示查無資料或發生錯誤，如實轉達，不要編造。";

    private readonly ILlmAgent _agent;
    private readonly IChatMemoryStore _memory;
    private readonly IMem0Client _mem0;
    private readonly IConversationStore _conversations;
    private readonly IWorkflowService _workflows;
    private readonly LlmOptions _llmOptions;
    private readonly ILogger<ChatService> _logger;

    public ChatService(
        ILlmAgent agent,
        IChatMemoryStore memory,
        IMem0Client mem0,
        IConversationStore conversations,
        IWorkflowService workflows,
        LlmOptions llmOptions,
        ILogger<ChatService> logger)
    {
        _agent = agent;
        _memory = memory;
        _mem0 = mem0;
        _conversations = conversations;
        _workflows = workflows;
        _llmOptions = llmOptions;
        _logger = logger;
    }

    public async Task<ChatResponse> ChatAsync(string message, string? userId, string? conversationId, UserContext? userCtx = null, CancellationToken ct = default)
    {
        var uid = NormalizeUser(userId);
        var cid = NormalizeConversation(conversationId, uid);

        using var activity = StartSpan(message);
        try
        {
            // 命中工具 → 用「摘要訊息」讓 LLM 只潤飾確定性結果(不心算);否則走純聊天(護欄 + mem0 + 短期歷史)。
            var summaryMessages = await TryRouteAndExecuteAsync(message, userCtx, ct);
            var messages = summaryMessages ?? await BuildPromptAsync(uid, cid, message, ct);

            // 一律不啟用原生 tool-calling(tools: null):路由/執行已由確定性管線完成。
            var reply = await _agent.CompleteAsync(messages, null, ct);
            activity?.SetTag("completion.length", reply.Length);

            // 阻塞式:持久化失敗照舊往上拋(對外 500),不吞。
            var saved = await _conversations.AddAsync(message, reply, ct);

            // 取得回覆並存 backend 之後,才寫入 mem0 與短期記憶。
            await _mem0.RememberAsync(uid, message, reply, ct);
            AppendExchange(cid, message, reply);

            return saved;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    public async IAsyncEnumerable<string> StreamChatAsync(
        string message, string? userId, string? conversationId, UserContext? userCtx = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var uid = NormalizeUser(userId);
        var cid = NormalizeConversation(conversationId, uid);

        // 刻意手動管理 span 生命週期(對應原 Java streamChat 不是 @Transactional、手動 start/stop):
        // 串流在訂閱時才執行,持久化發生在串流結束後、仍在本請求範圍內。
        var activity = StartSpan(message);
        var accumulated = new StringBuilder();
        try
        {
            // 路由 + 執行在串流開始「前」阻塞完成:命中工具則串流摘要,否則串流純聊天。
            var summaryMessages = await TryRouteAndExecuteAsync(message, userCtx, ct);
            var messages = summaryMessages ?? await BuildPromptAsync(uid, cid, message, ct);

            // 手動列舉以便在串流出錯時替 span 記 error(yield 不能放在 try/catch 內)。tools: null(不用原生 tool-calling)。
            var enumerator = _agent.StreamAsync(messages, null, ct).GetAsyncEnumerator(ct);
            try
            {
                while (true)
                {
                    string chunk;
                    try
                    {
                        if (!await enumerator.MoveNextAsync())
                        {
                            break;
                        }
                        chunk = enumerator.Current;
                    }
                    catch (Exception ex)
                    {
                        activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                        throw;
                    }

                    accumulated.Append(chunk);
                    yield return chunk;
                }
            }
            finally
            {
                await enumerator.DisposeAsync();
            }

            // 串流正常結束:記 completion.length、持久化串接全文、mem0、更新短期記憶。
            var reply = accumulated.ToString();
            activity?.SetTag("completion.length", reply.Length);

            // best-effort:串流已送出,持久化失敗不可讓已送出的串流炸掉,只記 warning。
            try
            {
                await _conversations.AddAsync(message, reply, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "串流聊天持久化失敗（串流已送出,略過）：{訊息}", ex.Message);
            }

            await _mem0.RememberAsync(uid, message, reply, ct);
            AppendExchange(cid, message, reply);
        }
        finally
        {
            // 無論成功或失敗,最後都要停止 span。
            activity?.Dispose();
        }
    }

    public async Task<IReadOnlyList<ChatResponse>> HistoryAsync(CancellationToken ct = default)
    {
        return await _conversations.ListDescAsync(ct);
    }

    // ---- 私有輔助 ----

    private static string NormalizeUser(string? userId) =>
        string.IsNullOrWhiteSpace(userId) ? "default" : userId;

    private static string NormalizeConversation(string? conversationId, string uid) =>
        string.IsNullOrWhiteSpace(conversationId) ? uid : conversationId;

    /// <summary>組裝訊息序列:[system(若有)] + 短期記憶訊息 + 本輪 user 訊息。</summary>
    private async Task<IReadOnlyList<LlmMessage>> BuildPromptAsync(string uid, string cid, string message, CancellationToken ct)
    {
        var messages = new List<LlmMessage>();

        // 1. 固定護欄:永遠是第一則 system(先於 mem0 前言),抑制自行心算/編造文件數字。
        messages.Add(new LlmMessage("system", ChatGuardPrompt));

        // 2. 短期記憶(先前訊息)。
        var recent = _memory.GetRecent(cid);

        // 3. 在呼叫 LLM 之前先取 mem0 長期記憶。
        var memories = await _mem0.RecallAsync(uid, message, ct);

        // 4. memories 非空白時,前置一則 system 訊息(前綴 + 換行 + memories)。
        if (!string.IsNullOrWhiteSpace(memories))
        {
            messages.Add(new LlmMessage("system", SystemMemoryPrefix + "\n" + memories));
        }

        // 5. 短期記憶 + 本輪 user 訊息。
        messages.AddRange(recent);
        messages.Add(new LlmMessage("user", message));

        return messages;
    }

    /// <summary>
    /// 路由 + 執行(確定性管線)。userCtx 為 null / 無工具 / NONE / 任一步失敗 → 回 null,由呼叫端走純聊天兜底;
    /// 命中工具則執行工具並回「摘要訊息」,交由呼叫端做最後一次(阻塞或串流)LLM 潤飾。
    /// 整段以 try/catch 包住:任何例外都吞成 null(退純聊天)並記 warning——一輪聊天絕不可因路由失敗而 500。
    /// </summary>
    private async Task<IReadOnlyList<LlmMessage>?> TryRouteAndExecuteAsync(string message, UserContext? userCtx, CancellationToken ct)
    {
        if (userCtx is null)
        {
            return null;   // 匿名不路由(Skill 需租戶身分),直接純聊天。
        }

        try
        {
            var tools = await BuildToolsAsync(userCtx, ct);
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

        var reply = (await _agent.CompleteAsync(routeMessages, null, ct)).Trim();
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

    /// <summary>聊天工具 → 工作流的對照表。RequiredRole 非 null 時只掛給該角色。</summary>
    private sealed record ChatToolSpec(string ToolName, string Workflow, string InputKey, string Description, string? RequiredRole = null);

    private static readonly ChatToolSpec[] ChatToolSpecs =
    {
        new("search_knowledge_base", "rag_qa", "question",
            "一般文件知識庫問答(通用檢索),當沒有更專門的工具可用時才用;question 放完整問句。"),
        new("verified_knowledge_query", "kb_query", "query",
            "需要可稽核、附證據驗證的知識查詢時才用(有證據不足即棄答的閘門);query 放查詢問句。"),
        new("summarize_text", "summarize", "text",
            "將輸入的原文濃縮成三句以內的摘要。使用者明確要求摘要一段長文字時使用;text 放要摘要的原文全文(不是描述)。"),
        new("triage_question", "triage", "question",
            "依問題複雜度分流後回答(簡單問題快答、複雜問題詳答)。使用者明確要求用分流(triage)方式處理問題時使用;question 放原始問題。"),
        new("generate_analysis_report", "analyze_report", "topic",
            "檢索租戶文件並產出指定主題的分析報告(檢索→分析→綜整多步流程)。使用者需要完整分析報告時使用;topic 放報告主題。",
            RequiredRole: "ADMIN"),
    };

    /// <summary>
    /// 輸出取字串答案時依序嘗試的 key;都沒有就整包序列化回給模型。
    /// business_result 排最前:template_* composed skill 的 nl_logic 節點把「使用者規則套用後」的權威答案
    /// 寫入 business_result(retrieval 型同時有套規則前的 final_answer),須先於 final_answer 命中。
    /// </summary>
    private static readonly string[] OutputKeys = { "business_result", "answer", "final_answer", "report", "summary" };

    /// <summary>
    /// 已登入(userCtx 非 null)時把可用的 Skill 目錄包成聊天工具;匿名聊天不掛工具(Skill 需要租戶身分)。
    /// 工具來源:動態 Skill 目錄(內建+自訂,租戶已由下游過濾)優先,殘留靜態工具依名去重兜底。
    /// 目錄抓取失敗採 best-effort:退回靜態工具(類比 mem0 吞錯),聊天不炸。
    /// 工具失敗回錯誤文字給模型照實轉述,不讓整輪聊天失敗。
    /// </summary>
    internal async Task<IReadOnlyList<LlmTool>?> BuildToolsAsync(UserContext? userCtx, CancellationToken ct)
    {
        if (userCtx is null)
        {
            return null;
        }

        JsonElement catalog;
        try
        {
            catalog = await _workflows.GetSkillCatalogAsync(userCtx, ct);
        }
        catch (Exception ex)
        {
            // best-effort:目錄抓不到不讓聊天炸;退回殘留靜態工具(或空)。
            _logger.LogWarning(ex, "Skill 目錄取得失敗,退回靜態工具:{訊息}", ex.Message);
            return BuildStaticTools(userCtx);
        }

        // ponytail: 每輪 GET /skills 的 N+1;若量到痛 → IMemoryCache per-(tenant,role) 30–60s TTL。
        var skillTools = SkillCatalogToTools(catalog, userCtx);
        var names = new HashSet<string>(skillTools.Select(t => t.Name), StringComparer.Ordinal);

        var result = new List<LlmTool>(skillTools);
        foreach (var t in BuildStaticTools(userCtx))
        {
            // 名稱未被 Skill 佔用才補(Skill 目錄優先)。
            if (names.Add(t.Name))
            {
                result.Add(t);
            }
        }

        return result;
    }

    /// <summary>殘留靜態 ChatToolSpecs → 工具。RequiredRole 非 null 時只掛給該角色(語義零變)。</summary>
    // ponytail: settings-skill-redesign 把這些 workflow 遷成 skill 後,ChatToolSpecs 縮到空即可整段刪。
    private IReadOnlyList<LlmTool> BuildStaticTools(UserContext userCtx)
    {
        var tools = new List<LlmTool>(ChatToolSpecs.Length);
        foreach (var spec in ChatToolSpecs)
        {
            if (spec.RequiredRole is not null && userCtx.Role != spec.RequiredRole)
            {
                continue;
            }

            tools.Add(new LlmTool(spec.ToolName, spec.Description,
                (arg, ct) => InvokeWorkflowToolAsync(spec, arg, userCtx, ct)));
        }

        return tools;
    }

    /// <summary>
    /// Skill 目錄(原樣穿透的 JsonElement 陣列)→ 聊天工具。純函式,可獨立單測。
    /// 過濾:template_* 內建骨架(空殼,不可路由)→ 跳過;角色不符 → 跳過;
    /// 非「恰好一個必填字串輸入」→ 跳過(P1 天花板,多參/非字串待 P3)。
    /// </summary>
    private IReadOnlyList<LlmTool> SkillCatalogToTools(JsonElement catalog, UserContext userCtx)
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

            // template_* 內建骨架是空殼,不可被路由當工具(與前端同一條規則)。
            var source = item.TryGetProperty("source", out var s) ? s.GetString() : null;
            if (source == "builtin" && name.StartsWith("template_", StringComparison.Ordinal))
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
                (arg, ct) => InvokeSkillToolAsync(name, inputKey, arg, userCtx, ct)));
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

    private async Task<string> InvokeWorkflowToolAsync(ChatToolSpec spec, string arg, UserContext userCtx, CancellationToken ct)
    {
        try
        {
            var input = new Dictionary<string, JsonElement>
            {
                [spec.InputKey] = JsonSerializer.SerializeToElement(arg),
            };
            var res = await _workflows.InvokeAsync(spec.Workflow, input, userCtx, ct);

            // kb_query 證據不足棄答時,確定性退回 rag_qa 兜底(不依賴模型自己補打第二刀),並如實註明。
            if (spec.Workflow == "kb_query" && IsAbstain(res.Output))
            {
                var ragInput = new Dictionary<string, JsonElement>
                {
                    ["question"] = JsonSerializer.SerializeToElement(arg),
                };
                var rag = await _workflows.InvokeAsync("rag_qa", ragInput, userCtx, ct);
                return "嚴格稽核查詢因證據不足而棄答;以下是一般知識庫檢索(不含稽核保證)的結果:"
                    + ExtractAnswer(rag.Output);
            }

            return ExtractAnswer(res.Output);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "聊天工具 {工具} 呼叫工作流 {工作流} 失敗:{訊息}", spec.ToolName, spec.Workflow, ex.Message);
            return $"工作流 {spec.Workflow} 呼叫失敗:{ex.Message}";
        }
    }

    /// <summary>
    /// Skill 版工具委派:打 /skills/{name}/invoke,輸入鍵由 §1.3 從 input_schema 挑出。
    /// 失敗回錯誤字串給模型轉述,不炸整輪(對映 InvokeWorkflowToolAsync 的 catch)。
    /// ponytail: P1 不移植 kb_query→rag_qa abstain 兜底(等 kb_query 真的遷成 skill 且量到需要再說)。
    /// </summary>
    private async Task<string> InvokeSkillToolAsync(
        string name, string inputKey, string arg, UserContext userCtx, CancellationToken ct)
    {
        try
        {
            var input = new Dictionary<string, JsonElement>
            {
                [inputKey] = JsonSerializer.SerializeToElement(arg),
            };
            var res = await _workflows.InvokeSkillAsync(name, input, userCtx, ct);
            return ExtractSkillAnswer(res);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "聊天工具 {工具} 呼叫 skill 失敗:{訊息}", name, ex.Message);
            return $"Skill {name} 呼叫失敗:{ex.Message}";
        }
    }

    /// <summary>
    /// skill invoke 回的是整包 JsonElement,形狀 {skill, output:{…}};取外層 output(無則根),
    /// 依 OutputKeys 取字串,都沒有回整包 raw JSON。不改既有 ExtractAnswer(它服務字典路徑)。
    /// </summary>
    private static string ExtractSkillAnswer(JsonElement res)
    {
        var output = res.ValueKind == JsonValueKind.Object && res.TryGetProperty("output", out var o) ? o : res;
        foreach (var key in OutputKeys)
        {
            if (output.ValueKind == JsonValueKind.Object
                && output.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            {
                return v.GetString()!;
            }
        }

        return output.GetRawText();
    }

    private static bool IsAbstain(Dictionary<string, JsonElement> output) =>
        output.TryGetValue("answer_mode", out var mode)
        && mode.ValueKind == JsonValueKind.String
        && mode.GetString() == "ABSTAIN";

    private static string ExtractAnswer(Dictionary<string, JsonElement> output)
    {
        foreach (var key in OutputKeys)
        {
            if (output.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString()!;
            }
        }

        return JsonSerializer.Serialize(output);
    }

    private void AppendExchange(string cid, string message, string reply)
    {
        // 本輪的 user 與 assistant 各追加一則(store 內部裁到 20)。
        _memory.Append(cid, new LlmMessage("user", message));
        _memory.Append(cid, new LlmMessage("assistant", reply));
    }

    private Activity? StartSpan(string message)
    {
        // 沒有任何 OTel listener 時 StartActivity 回 null(例如單元測試),此時 span 為 no-op,不影響流程。
        var activity = ActivitySource.StartActivity("chat-service", ActivityKind.Internal);
        if (activity is not null)
        {
            activity.SetTag("gen_ai.operation.name", "chat");
            activity.SetTag("gen_ai.system", "openai");
            activity.SetTag("gen_ai.request.model", _llmOptions.ChatModel);
            activity.SetTag("prompt.length", message.Length);
        }

        return activity;
    }
}
