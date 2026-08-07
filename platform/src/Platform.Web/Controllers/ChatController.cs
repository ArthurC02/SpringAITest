using Platform.Service.Abstractions;
using Platform.Service.Dtos;
using Platform.Service.Exceptions;
using Platform.Web.Auth;
using Microsoft.AspNetCore.Authorization;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace Platform.Web.Controllers;

/// <summary>
/// 聊天端點。所有操作都要求具備完整租戶與使用者 claims 的有效 JWT。
/// </summary>
[ApiController]
[Route("api/chat")]
[Authorize]
public sealed class ChatController : ControllerBase
{
    // 串流中途失敗的終止 frame 訊息(通用文字,不含例外細節)。前端以 event:error frame 偵測異常收尾。
    private const string StreamErrorMessage = "回覆過程發生錯誤，請稍後再試";

    private readonly IChatService _chat;

    public ChatController(IChatService chat) => _chat = chat;

    /// <summary>阻塞式聊天 — 回 ChatResponse(id/reply/createdAt)。</summary>
    [HttpPost]
    public async Task<ActionResult<ChatResponse>> Chat([FromBody] ChatRequest request, CancellationToken ct)
    {
        var user = RequireUserContext();
        var conversationId = GetConversationId(request.ConversationId);
        Response.Headers["X-Conversation-Id"] = conversationId;
        var result = await _chat.ChatAsync(request.Message!, null, conversationId, user, ct, request.OrchestratorId);
        return Ok(result);
    }

    /// <summary>
    /// 串流聊天 — text/event-stream。每個 chunk 一個 SSE event:每行前綴 "data:"(冒號後不加空格),
    /// event 以空行結尾;含換行的 chunk 拆成同一 event 內多個 data: 行;逐 event flush。
    /// 驗證失敗由 [ApiController] 在進入前自動回 400 JSON(不會寫任何 SSE bytes)。
    /// </summary>
    [HttpPost("stream")]
    public async Task Stream([FromBody] ChatRequest request, CancellationToken ct)
    {
        var user = RequireUserContext();
        var conversationId = GetConversationId(request.ConversationId);
        Response.Headers["X-Conversation-Id"] = conversationId;
        Response.ContentType = "text/event-stream; charset=utf-8";
        Response.Headers.CacheControl = "no-cache";

        // 關閉回應緩衝,確保 chunk 即時送出。
        var bodyFeature = HttpContext.Features.Get<IHttpResponseBodyFeature>();
        bodyFeature?.DisableBuffering();

        // 串流可能在已送出部分 token 後才由 LLM/下游拋錯;此時回應 body 已開始,GlobalExceptionHandler
        // 無法再改寫(HasStarted),連線會無聲斷開。故就地 try/catch:失敗時補一個終止用的 error frame 再正常結束。
        try
        {
            await foreach (var chunk in _chat.StreamChatAsync(request.Message!, null, conversationId, user, ct, request.OrchestratorId))
            {
                foreach (var line in chunk.Split('\n'))
                {
                    await Response.WriteAsync($"data:{line}\n", ct);
                }

                await Response.WriteAsync("\n", ct);
                await Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 客戶端主動中斷:連線已無對象可寫,不是錯誤,不寫 error frame。
        }
        catch (Exception)
        {
            // 終止語意:已送出的 token 之後補一個 event:error frame(維持無空格 data: 風格),再正常結束回應。
            // 例外細節不外洩,只給通用訊息(原始細節由 ChatService/下游各自 log)。
            await Response.WriteAsync("event:error\n", ct);
            await Response.WriteAsync($"data:{StreamErrorMessage}\n\n", ct);
            await Response.Body.FlushAsync(ct);
        }
    }

    private UserContext RequireUserContext()
        => User.ToUsableChatUserContext()
           ?? throw new InvalidCredentialsException("需要有效的使用者身分");

    private static string GetConversationId(string? conversationId)
        => string.IsNullOrWhiteSpace(conversationId)
            ? Guid.NewGuid().ToString("D")
            : Guid.Parse(conversationId).ToString("D");

    /// <summary>
    /// 聊天歷史 — 回 List&lt;ChatResponse&gt;,createdAt DESC,依登入身分過濾(只回自己租戶+自己的紀錄);
    /// JWT 身分不完整時 fail closed。
    /// </summary>
    [HttpGet("history")]
    public async Task<ActionResult<IReadOnlyList<ChatResponse>>> History(CancellationToken ct)
    {
        var history = await _chat.HistoryAsync(RequireUserContext(), ct);
        return Ok(history);
    }

    /// <summary>Additive keyset-paginated history. The legacy array endpoint is unchanged.</summary>
    [HttpGet("history/page")]
    public async Task<ActionResult<ChatHistoryPage>> HistoryPage(
        [FromQuery, Range(1, 100)] int limit = 50,
        [FromQuery] string? before = null,
        CancellationToken ct = default)
    {
        var page = await _chat.HistoryPageAsync(limit, before, RequireUserContext(), ct);
        return Ok(page);
    }
}
