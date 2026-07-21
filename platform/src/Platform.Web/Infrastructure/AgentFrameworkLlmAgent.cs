using System.Runtime.CompilerServices;
using Platform.Service.Abstractions;
using Platform.Service.Options;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
// 同時引入 OpenAI.Chat(為了 AsAIAgent 擴充方法)與 Microsoft.Extensions.AI 時,
// ChatMessage 名稱會衝突;明確指定用 Agent Framework 用的 Microsoft.Extensions.AI 版本。
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Platform.Web.Infrastructure;

/// <summary>
/// 以 Microsoft Agent Framework 的 <see cref="AIAgent"/> 實作 <see cref="ILlmAgent"/>。
/// OpenAI SDK 的 endpoint 指向 LiteLLM(http,非 https);阻塞走 RunAsync、串流走 RunStreamingAsync。
/// 每次呼叫都把完整訊息列(system? + 短期記憶 + user)當成無狀態輸入傳入,
/// 不依賴 AgentThread 的隱式儲存——因為視窗裁切邏輯由 ChatService 自控。
/// </summary>
public sealed class AgentFrameworkLlmAgent : ILlmAgent
{
    private readonly AIAgent _agent;

    public AgentFrameworkLlmAgent(LlmOptions options)
    {
        // OpenAI 相容 client(Endpoint→LiteLLM、90s NetworkTimeout)由 LlmClientFactory 建;
        // 經 Microsoft.Agents.AI 的 AIAgent 抽象呼叫,溫度由 options 帶入。
        _agent = LlmClientFactory.Create(options).AsAIAgent(
            new ChatClientAgentOptions
            {
                ChatOptions = new ChatOptions { Temperature = options.Temperature },
            });
    }

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
