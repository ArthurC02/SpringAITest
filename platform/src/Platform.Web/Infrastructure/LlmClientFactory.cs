using System.ClientModel;
using Platform.Service.Options;
using OpenAI;
using OpenAI.Chat;

namespace Platform.Web.Infrastructure;

/// <summary>
/// 建 LiteLLM 相容的 OpenAI <see cref="ChatClient"/> 的單一真理來源。Endpoint 指向 LiteLLM 閘道
/// (信任 http);NetworkTimeout 明確設 90s(與其他下游讀取逾時一致,避免預設 100s 掛住連線)。
/// 單一消費點:Program.cs 接 <c>.AsIChatClient()</c> 註冊成 <c>IChatClient</c> 單例,兩顆 hosted agent
/// 與 <c>AgentFrameworkLlmAgent</c> 都用那一顆。
/// 放 Platform.Web 而非 Platform.Service:消費者在 Web 層,且 OpenAI SDK 套件只被 Platform.Web 參照。
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
