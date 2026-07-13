namespace Platform.Service.Abstractions;

/// <summary>
/// 聊天代理可用的工具(function calling)。Service 層的薄定義,
/// Web 層(AgentFrameworkLlmAgent)轉成 Agent Framework 的 AIFunction。
/// ponytail: 固定單一字串參數 question;需要多參數工具時再泛化成 JSON schema。
/// </summary>
public sealed record LlmTool(
    string Name,
    string Description,
    Func<string, CancellationToken, Task<string>> InvokeAsync);
