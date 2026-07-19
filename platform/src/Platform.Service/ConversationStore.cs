using Platform.Service.Abstractions;
using Platform.Service.Dtos;
using Platform.Service.Exceptions;

namespace Platform.Service;

/// <summary>
/// 聊天歷史持久化,改走 backend /api/conversations。
/// 失敗一律拋 BackendCallException(對外 500),不引入 chat 端點原先沒有的 502。
/// </summary>
public sealed class ConversationStore : IConversationStore
{
    private const string FailurePrefix = "聊天記錄服務呼叫失敗：";

    private readonly BackendClient _backend;

    public ConversationStore(BackendClient backend) => _backend = backend;

    private Exception WrapTransport(Exception ex) => new BackendCallException(FailurePrefix + ex.Message, ex);

    public async Task<ChatResponse> AddAsync(string prompt, string reply, UserContext ctx, CancellationToken ct = default)
    {
        var created = await _backend.SendForJsonAsync<ConversationCreated>(
            _backend.BuildRequest(HttpMethod.Post, "/api/conversations", ctx, body: new { prompt, reply }),
            WrapTransport,
            (r, _) => Task.FromResult<Exception>(new BackendCallException(FailurePrefix + "HTTP " + (int)r.StatusCode)),
            () => new BackendCallException(FailurePrefix + "回應內容為空"),
            ct);

        // reply 由呼叫端提供(backend 只回 id 與 createdAt);createdAt 正規化為 UTC 以保留結尾 Z。
        return new ChatResponse(created.Id, reply, DateTime.SpecifyKind(created.CreatedAt, DateTimeKind.Utc));
    }

    public async Task<IReadOnlyList<ChatResponse>> ListDescAsync(UserContext ctx, CancellationToken ct = default)
    {
        var items = await _backend.SendForJsonListAsync<ConversationItem>(
            _backend.BuildRequest(HttpMethod.Get, "/api/conversations", ctx),
            WrapTransport,
            (r, _) => Task.FromResult<Exception>(new BackendCallException(FailurePrefix + "HTTP " + (int)r.StatusCode)),
            ct);

        return items
            .Select(i => new ChatResponse(i.Id, i.Reply, DateTime.SpecifyKind(i.CreatedAt, DateTimeKind.Utc)))
            .ToList();
    }

    /// <summary>backend POST /api/conversations 回應:{ id, createdAt }。</summary>
    private sealed record ConversationCreated(long Id, DateTime CreatedAt);

    /// <summary>backend GET /api/conversations 項目:{ id, reply, createdAt }。</summary>
    private sealed record ConversationItem(long Id, string Reply, DateTime CreatedAt);
}
