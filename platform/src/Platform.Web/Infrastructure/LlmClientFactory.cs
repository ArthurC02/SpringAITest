using System.ClientModel;
using Platform.Service.Options;
using OpenAI;
using OpenAI.Chat;

namespace Platform.Web.Infrastructure;

/// <summary>
/// 建 LiteLLM 相容的 OpenAI <see cref="ChatClient"/> 的單一真理來源。Endpoint 指向 LiteLLM 閘道
/// (信任 http);NetworkTimeout 明確設 90s(與其他下游讀取逾時一致,避免預設 100s 掛住連線)。
/// 兩處消費:Program.cs 接 <c>.AsIChatClient()</c>(AG-UI hosted agent)、
/// <c>AgentFrameworkLlmAgent</c> 接 <c>.AsAIAgent(...)</c>(ILlmAgent 實作)。
/// 放 Platform.Web 而非 Platform.Service:兩處消費者都在 Web 層,且 OpenAI SDK 套件只被 Platform.Web 參照。
/// </summary>
internal static class LlmClientFactory
{
    internal static ChatClient Create(LlmOptions options) =>
        new OpenAIClient(
            new ApiKeyCredential(options.ApiKey),
            new OpenAIClientOptions
            {
                Endpoint = new Uri(options.BaseUrl),
                NetworkTimeout = TimeSpan.FromSeconds(90),
            })
        .GetChatClient(options.ChatModel);
}
