namespace Platform.Service.Abstractions;

/// <summary>
/// 傳給 LLM 的一則訊息。<see cref="Role"/> 為 "system" | "user" | "assistant"。
/// 這是 Service 層自有的薄型別,讓 ChatService 不直接相依 Agent Framework 的訊息型別
/// (便於單元測試 fake <see cref="ILlmAgent"/>)。
/// </summary>
public sealed record LlmMessage(string Role, string Content);
