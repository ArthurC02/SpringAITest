using System.Runtime.CompilerServices;
using Platform.Service.Abstractions;
using Platform.Service.Options;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Platform.Web.Infrastructure;

/// <summary>
/// 以 Microsoft Agent Framework 的 <see cref="AIAgent"/> 實作 <see cref="ILlmAgent"/>。
/// 底層是 DI 中那顆共用的 <see cref="IChatClient"/> 單例(endpoint 指向 LiteLLM,http 非 https),
/// 與 AG-UI/ChatAssistant 兩顆 hosted agent 同一顆 client;阻塞走 RunAsync、串流走 RunStreamingAsync。
/// 每次呼叫都把完整訊息列(system? + 短期記憶 + user)當成無狀態輸入傳入,不依賴 AgentThread 的隱式儲存
/// ——短期記憶視窗由框架的 InMemoryChatHistoryProvider + SlidingWindowCompactionStrategy 管理,不在這一層。
/// </summary>
public sealed class AgentFrameworkLlmAgent : ILlmAgent
{
    private readonly AIAgent _agent;

    public AgentFrameworkLlmAgent(IChatClient chatClient, LlmOptions options)
        => _agent = chatClient.AsAIAgent(
            new ChatClientAgentOptions
            {
                ChatOptions = new ChatOptions { Temperature = options.Temperature },
            });

    public async Task<string> CompleteAsync(IReadOnlyList<LlmMessage> messages, CancellationToken ct)
    {
        var response = await _agent.RunAsync(ToChatMessages(messages), cancellationToken: ct);
        return response.Text ?? string.Empty;
    }

    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<LlmMessage> messages, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var update in _agent.RunStreamingAsync(ToChatMessages(messages), cancellationToken: ct))
        {
            // 過濾空字串 chunk(串流常有無內容的 metadata update)。
            if (!string.IsNullOrEmpty(update.Text))
            {
                yield return update.Text;
            }
        }
    }

    // 把 Service 層的薄 LlmMessage 轉成 Microsoft.Extensions.AI 的 ChatMessage。
    private static List<ChatMessage> ToChatMessages(IReadOnlyList<LlmMessage> messages)
    {
        var result = new List<ChatMessage>(messages.Count);
        foreach (var m in messages)
        {
            var role = m.Role switch
            {
                "system" => ChatRole.System,
                "assistant" => ChatRole.Assistant,
                _ => ChatRole.User,
            };
            result.Add(new ChatMessage(role, m.Content));
        }

        return result;
    }
}
