using Platform.Service.Abstractions;
using Platform.Service.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace Platform.Web.Controllers;

/// <summary>聊天端點,全部公開(免 token)。</summary>
[ApiController]
[Route("api/chat")]
[AllowAnonymous]
public sealed class ChatController : ControllerBase
{
    private readonly IChatService _chat;

    public ChatController(IChatService chat) => _chat = chat;

    /// <summary>阻塞式聊天 — 回 ChatResponse(id/reply/createdAt)。</summary>
    [HttpPost]
    public async Task<ActionResult<ChatResponse>> Chat([FromBody] ChatRequest request, CancellationToken ct)
    {
        var result = await _chat.ChatAsync(request.Message!, request.UserId, request.ConversationId, ct);
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
        Response.ContentType = "text/event-stream; charset=utf-8";
        Response.Headers.CacheControl = "no-cache";

        // 關閉回應緩衝,確保 chunk 即時送出。
        var bodyFeature = HttpContext.Features.Get<IHttpResponseBodyFeature>();
        bodyFeature?.DisableBuffering();

        await foreach (var chunk in _chat.StreamChatAsync(request.Message!, request.UserId, request.ConversationId, ct))
        {
            foreach (var line in chunk.Split('\n'))
            {
                await Response.WriteAsync($"data:{line}\n", ct);
            }

            await Response.WriteAsync("\n", ct);
            await Response.Body.FlushAsync(ct);
        }
    }

    /// <summary>聊天歷史 — 回 List&lt;ChatResponse&gt;,createdAt DESC(全域,不分租戶/使用者)。</summary>
    [HttpGet("history")]
    public async Task<ActionResult<IReadOnlyList<ChatResponse>>> History(CancellationToken ct)
    {
        var history = await _chat.HistoryAsync(ct);
        return Ok(history);
    }
}
