using System.ClientModel;
using System.Runtime.CompilerServices;
using Platform.Service.Abstractions;
using Platform.Service.Options;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
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
    private readonly float _temperature;

    public AgentFrameworkLlmAgent(LlmOptions options)
    {
        _temperature = options.Temperature;

        // OpenAI 相容 client,Endpoint 指向 LiteLLM 閘道(信任 http)。
        var openAiClient = new OpenAIClient(
            new ApiKeyCredential(options.ApiKey),
            new OpenAIClientOptions { Endpoint = new Uri(options.BaseUrl) });

        // 經 Microsoft.Agents.AI 的 AIAgent 抽象呼叫;溫度固定 0.7(由 options 帶入)。
        _agent = openAiClient.GetChatClient(options.ChatModel).AsAIAgent(
            new ChatClientAgentOptions
            {
                ChatOptions = new ChatOptions { Temperature = options.Temperature },
            });
    }

    public async Task<string> CompleteAsync(IReadOnlyList<LlmMessage> messages, IReadOnlyList<LlmTool>? tools, CancellationToken ct)
    {
        var response = await _agent.RunAsync(ToChatMessages(messages), null, ToRunOptions(tools), ct);
        return response.Text ?? string.Empty;
    }

    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<LlmMessage> messages, IReadOnlyList<LlmTool>? tools, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var update in _agent.RunStreamingAsync(ToChatMessages(messages), null, ToRunOptions(tools), ct))
        {
            // 過濾空字串 chunk(串流常有無內容的 metadata update)。
            if (!string.IsNullOrEmpty(update.Text))
            {
                yield return update.Text;
            }
        }
    }

    /// <summary>
    /// 把 Service 層的 LlmTool 轉成 AIFunction 掛進 run-level ChatOptions。
    /// ChatClientAgent 內建 function calling 迴圈,工具呼叫由框架自動執行。
    /// 注意 run-level ChatOptions 不與建構時的合併,溫度要重帶。
    /// </summary>
    private ChatClientAgentRunOptions? ToRunOptions(IReadOnlyList<LlmTool>? tools)
    {
        if (tools is null || tools.Count == 0)
        {
            return null;
        }

        var aiTools = new List<AITool>(tools.Count);
        foreach (var tool in tools)
        {
            var invoke = tool.InvokeAsync;
            aiTools.Add(AIFunctionFactory.Create(
                (string question, CancellationToken ct) => invoke(question, ct),
                tool.Name,
                tool.Description));
        }

        return new ChatClientAgentRunOptions(new ChatOptions { Temperature = _temperature, Tools = aiTools });
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
