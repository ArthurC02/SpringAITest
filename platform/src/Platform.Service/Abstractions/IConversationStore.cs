using Platform.Service.Dtos;

namespace Platform.Service.Abstractions;

/// <summary>
/// 聊天歷史持久化(改由 backend /api/conversations 承載)。
/// ChatService 只相依此介面;失敗時拋 BackendCallException(對外 500,維持原本地失敗行為)。
/// </summary>
public interface IConversationStore
{
    /// <summary>新增一輪對話,回 backend 產生的 id 與 createdAt(reply 原樣帶回)。</summary>
    Task<ChatResponse> AddAsync(string prompt, string reply, CancellationToken ct = default);

    /// <summary>歷史清單,created_at DESC(全域,不分租戶/使用者)。</summary>
    Task<IReadOnlyList<ChatResponse>> ListDescAsync(CancellationToken ct = default);
}
