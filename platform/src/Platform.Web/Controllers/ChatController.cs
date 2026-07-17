using Platform.Service.Abstractions;
using Platform.Service.Dtos;
using Platform.Web.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace Platform.Web.Controllers;

/// <summary>
/// 聊天端點,全部公開(免 token)。
/// 但請求帶有效 JWT 時仍取出使用者情境傳給 service:登入者的聊天自動獲得租戶知識庫檢索工具(RAG),
/// 匿名請求維持裸聊(檢索需要租戶身分)。
/// </summary>
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
        SetAuthInvalidHeaderIfNeeded();
        var result = await _chat.ChatAsync(request.Message!, request.UserId, request.ConversationId, MaybeUserContext(), ct);
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
        SetAuthInvalidHeaderIfNeeded();

        // 關閉回應緩衝,確保 chunk 即時送出。
        var bodyFeature = HttpContext.Features.Get<IHttpResponseBodyFeature>();
        bodyFeature?.DisableBuffering();

        await foreach (var chunk in _chat.StreamChatAsync(request.Message!, request.UserId, request.ConversationId, MaybeUserContext(), ct))
        {
            foreach (var line in chunk.Split('\n'))
            {
                await Response.WriteAsync($"data:{line}\n", ct);
            }

            await Response.WriteAsync("\n", ct);
            await Response.Body.FlushAsync(ct);
        }
    }

    /// <summary>AllowAnonymous 下認證中介軟體仍會驗有帶的 Bearer:驗過就有身分,沒帶或無效即匿名。</summary>
    private UserContext? MaybeUserContext()
        => User.Identity?.IsAuthenticated == true ? User.ToUserContext() : null;

    /// <summary>
    /// 帶了 Authorization header 但驗證未通過(過期/無效 JWT)時回 X-Auth-Invalid: 1,
    /// 讓前端全域登出機制能偵測到聊天路徑上的失效 token(這兩個端點 AllowAnonymous,永遠不會回 401)。
    /// 完全沒帶 Authorization(真匿名)不加這個 header。
    /// </summary>
    private void SetAuthInvalidHeaderIfNeeded()
    {
        if (Request.Headers.ContainsKey("Authorization") && User.Identity?.IsAuthenticated != true)
        {
            Response.Headers["X-Auth-Invalid"] = "1";
        }
    }

    /// <summary>
    /// 聊天歷史 — 回 List&lt;ChatResponse&gt;,createdAt DESC,依登入身分過濾(只回自己租戶+自己的紀錄);
    /// 匿名(無有效 JWT)回空陣列。
    /// </summary>
    [HttpGet("history")]
    public async Task<ActionResult<IReadOnlyList<ChatResponse>>> History(CancellationToken ct)
    {
        var history = await _chat.HistoryAsync(MaybeUserContext(), ct);
        return Ok(history);
    }
}
