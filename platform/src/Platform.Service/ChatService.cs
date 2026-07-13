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
            var messages = await BuildPromptAsync(uid, cid, message, ct);

            var reply = await _agent.CompleteAsync(messages, BuildTools(userCtx), ct);
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
            var messages = await BuildPromptAsync(uid, cid, message, ct);

            // 手動列舉以便在串流出錯時替 span 記 error(yield 不能放在 try/catch 內)。
            var enumerator = _agent.StreamAsync(messages, BuildTools(userCtx), ct).GetAsyncEnumerator(ct);
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

        // 1. 短期記憶(先前訊息)。
        var recent = _memory.GetRecent(cid);

        // 2. 在呼叫 LLM 之前先取 mem0 長期記憶。
        var memories = await _mem0.RecallAsync(uid, message, ct);

        // 3. memories 非空白時,前置一則 system 訊息(前綴 + 換行 + memories)。
        if (!string.IsNullOrWhiteSpace(memories))
        {
            messages.Add(new LlmMessage("system", SystemMemoryPrefix + "\n" + memories));
        }

        // 4. 短期記憶 + 本輪 user 訊息。
        messages.AddRange(recent);
        messages.Add(new LlmMessage("user", message));

        return messages;
    }

    /// <summary>聊天工具 → 工作流的對照表。RequiredRole 非 null 時只掛給該角色。</summary>
    private sealed record ChatToolSpec(string ToolName, string Workflow, string InputKey, string Description, string? RequiredRole = null);

    private static readonly ChatToolSpec[] ChatToolSpecs =
    {
        new("search_knowledge_base", "rag_qa", "question",
            "檢索使用者所屬租戶的文件知識庫並回答問題,附引用。凡是問文件內容、內部資料或平台內知識,一律預設用這個工具;question 放完整的自然語言問句。"),
        new("verified_knowledge_query", "kb_query", "query",
            "嚴格稽核版知識查詢,專為財務/數據類問題設計:有確定性證據驗證閘門,證據不足時棄答並自動改用一般檢索兜底(結果會註明)。僅在使用者明確要求「可稽核」「有憑據」時使用;query 放查詢問句。"),
        new("summarize_text", "summarize", "text",
            "將輸入的原文濃縮成三句以內的摘要。使用者明確要求摘要一段長文字時使用;text 放要摘要的原文全文(不是描述)。"),
        new("triage_question", "triage", "question",
            "依問題複雜度分流後回答(簡單問題快答、複雜問題詳答)。使用者明確要求用分流(triage)方式處理問題時使用;question 放原始問題。"),
        new("generate_analysis_report", "analyze_report", "topic",
            "檢索租戶文件並產出指定主題的分析報告(檢索→分析→綜整多步流程)。使用者需要完整分析報告時使用;topic 放報告主題。",
            RequiredRole: "ADMIN"),
    };

    /// <summary>工作流輸出取字串答案時依序嘗試的 key;都沒有就整包序列化回給模型。</summary>
    private static readonly string[] OutputKeys = { "answer", "final_answer", "report", "summary" };

    /// <summary>
    /// 已登入(userCtx 非 null)時把工作流包成聊天工具;匿名聊天不掛工具(工作流需要租戶身分)。
    /// 工具失敗回錯誤文字給模型照實轉述,不讓整輪聊天失敗。
    /// </summary>
    private IReadOnlyList<LlmTool>? BuildTools(UserContext? userCtx)
    {
        if (userCtx is null)
        {
            return null;
        }

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
