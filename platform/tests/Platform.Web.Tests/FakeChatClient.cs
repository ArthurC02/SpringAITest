using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Platform.Web.Tests;

/// <summary>
/// AG-UI 端點的 IChatClient fake:串流吐「你好」「世界」兩塊,阻塞回固定字串。
/// 讓 AG-UI 冒煙測試走真的 MapAGUI/SSE 序列化,但不打真的 LiteLLM。
/// (單獨一檔避免與 Platform.Service.Dtos.ChatResponse 名稱衝突。)
/// </summary>
public sealed class FakeChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "測試回覆")));

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        yield return new ChatResponseUpdate(ChatRole.Assistant, "你好");
        yield return new ChatResponseUpdate(ChatRole.Assistant, "世界");
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
