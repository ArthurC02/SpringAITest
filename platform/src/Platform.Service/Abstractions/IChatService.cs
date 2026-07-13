using Platform.Service.Dtos;

namespace Platform.Service.Abstractions;

/// <summary>聊天服務:阻塞、串流、歷史。</summary>
public interface IChatService
{
    /// <summary>
    /// 阻塞式聊天:呼叫 LLM 取完整回覆、持久化、更新記憶,回 ChatResponse。
    /// userCtx 非 null(已登入)時掛上知識庫檢索工具,模型可自行決定何時檢索。
    /// </summary>
    Task<ChatResponse> ChatAsync(string message, string? userId, string? conversationId, UserContext? userCtx = null, CancellationToken ct = default);

    /// <summary>串流式聊天:逐塊吐出回覆;串流完成時才持久化串接後的全文與更新記憶。userCtx 同上。</summary>
    IAsyncEnumerable<string> StreamChatAsync(string message, string? userId, string? conversationId, UserContext? userCtx = null, CancellationToken ct = default);

    /// <summary>聊天歷史:全撈依 CreatedAt DESC(全域,不分租戶/使用者)。</summary>
    Task<IReadOnlyList<ChatResponse>> HistoryAsync(CancellationToken ct = default);
}
