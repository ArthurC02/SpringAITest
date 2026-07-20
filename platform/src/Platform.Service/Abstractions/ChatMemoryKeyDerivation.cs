using Platform.Service.Dtos;

namespace Platform.Service.Abstractions;

/// <summary>
/// 記憶鍵推導的純函式,供 <see cref="IChatIdentityAccessor"/> 的正式實作(Web 層
/// <c>HttpChatIdentityAccessor</c>)與測試 fake 共用,消除逐字重複(P2 遺留:<c>ChatService.cs</c> 與
/// <c>HttpChatIdentityAccessor.cs</c> 曾各自維護一份相同邏輯)。
/// 防 IDOR 語意:已登入時(<paramref name="userCtx"/> 非 null)一律以 JWT 身分為準,uid = 租戶碼:使用者;
/// cid 空白退回 uid,非空白時前綴身分(同人可多對話視窗,跨租戶/跨使用者永不撞 key)。不信任 body 的 userId。
/// 匿名(userCtx null)沒有 JWT 身分,維持既有 body fallback 語義(userId 空白 → "default";
/// conversationId 空白 → 退回 uid)。
/// </summary>
public static class ChatMemoryKeyDerivation
{
    public static (string Uid, string Cid) Derive(string? userId, string? conversationId, UserContext? userCtx)
    {
        if (userCtx is null)
        {
            var anonUid = NormalizeUser(userId);
            return (anonUid, NormalizeConversation(conversationId, anonUid));
        }

        var uid = $"{userCtx.TenantCode}:{userCtx.UserId}";
        var cid = string.IsNullOrWhiteSpace(conversationId) ? uid : $"{uid}:{conversationId}";
        return (uid, cid);
    }

    private static string NormalizeUser(string? userId) =>
        string.IsNullOrWhiteSpace(userId) ? "default" : userId;

    private static string NormalizeConversation(string? conversationId, string uid) =>
        string.IsNullOrWhiteSpace(conversationId) ? uid : conversationId;
}
