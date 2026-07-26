using Platform.Service.Dtos;

namespace Platform.Service.Abstractions;

/// <summary>
/// 聊天歷史持久化(改由 backend /api/conversations 承載),以 (tenant_id, user_id) 隔離,故兩個方法都要求呼叫者身分。
/// ChatService 只相依此介面;失敗時拋 BackendCallException(對外 500,維持原本地失敗行為)。
/// </summary>
public interface IConversationStore
{
    /// <summary>
    /// 新增一輪對話,回 backend 產生的 id 與 createdAt(reply 原樣帶回)。
    /// <paramref name="metadata"/> 是 D6 的 Root Orchestrator lineage,一般聊天輪為 null。
    /// </summary>
    Task<ChatResponse> AddAsync(
        string prompt, string reply, UserContext ctx, ChatTurnMetadata? metadata = null,
        CancellationToken ct = default);

    /// <summary>歷史清單,created_at DESC,只回 ctx 所屬租戶+使用者的紀錄。</summary>
    Task<IReadOnlyList<ChatResponse>> ListDescAsync(UserContext ctx, CancellationToken ct = default);
}
