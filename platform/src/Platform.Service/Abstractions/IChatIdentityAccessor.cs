using Platform.Service.Dtos;

namespace Platform.Service.Abstractions;

// ponytail: 一個介面一個實作,通常違反 YAGNI——但 Platform.Service 不能引用 ASP.NET Core,
// 且 57 個既有測試要能塞身分而不起 WebApplicationFactory。這是被專案結構逼出來的,不是投機抽象。

/// <summary>
/// 供 agent pipeline 取得本次請求的登入身分與記憶 key。實作在 Web 層讀 IHttpContextAccessor;
/// 測試直接塞固定值。匿名時 CurrentUser 為 null。
/// </summary>
public interface IChatIdentityAccessor
{
    UserContext? CurrentUser { get; }

    /// <summary>沿用 ChatService.cs 既有的防 IDOR 語意(見 <see cref="ChatMemoryKeyDerivation"/>),不放寬。</summary>
    (string Uid, string Cid) DeriveMemoryKeys();

    /// <summary>本輪持久化失敗時由 ChatTurnRecorder 記錄;阻塞端點據此決定是否拋 500。</summary>
    Exception? PersistFailure { get; set; }

    /// <summary>
    /// 本輪持久化成功時,由 ChatTurnRecorder 寫入 backend 產生的 <see cref="ChatResponse"/>(含 Id/CreatedAt)。
    /// ChatTurnRecorder 跑在 agent pipeline 內部,阻塞端點(ChatService.ChatAsync)本身拿不到
    /// IConversationStore.AddAsync 的回傳值,只能靠這個管道取回持久化後的 Id 組回傳給呼叫端。
    /// </summary>
    ChatResponse? PersistedResponse { get; set; }

    /// <summary>
    /// ChatService 在鏈路 A 每輪開始時呼叫,把本輪 body 的 userId/conversationId 與(已由 controller 從 JWT
    /// 解析出的)userCtx 一併交給身分層,供本輪內 agent pipeline 的 ChatContextProvider/ChatTurnRecorder
    /// 經 CurrentUser/DeriveMemoryKeys() 取用;同時重置上一輪殘留的 PersistFailure/PersistedResponse
    /// (每輪全新狀態)。Web 實作的 CurrentUser 恆以 HttpContext.User 為準(此處傳入的 userCtx 與其永遠一致,
    /// 只是同一份已驗證身分的重複傳遞,不構成信任來源疊加,不放寬 IDOR 防護);AG-UI 不呼叫此方法,
    /// threadId 走 wire、身分完全來自 JWT。
    /// </summary>
    void SetRequestKeys(string? userId, string? conversationId, UserContext? userCtx);
}
