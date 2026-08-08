namespace Platform.Service.Abstractions;

/// <summary>
/// 聊天路由/執行(<see cref="Platform.Service.SkillRoutingAgent"/> 的確定性管線)用的工具薄定義:
/// 一個名稱 + 說明 + 單字串輸入的委派。
/// 不再轉成 LLM 原生 function-calling(那條路徑已移除);LLM 只做路由選擇與結果潤飾。
/// ponytail: 固定單一字串參數 question;需要多參數工具時再泛化成 JSON schema。
/// </summary>
public sealed record LlmTool(
    string Name,
    string Description,
    Func<string, CancellationToken, Task<string>> InvokeAsync);
