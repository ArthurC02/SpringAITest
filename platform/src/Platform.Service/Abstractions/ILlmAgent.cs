namespace Platform.Service.Abstractions;

/// <summary>
/// LLM 代理的薄介面。實作在 Web 層以 Microsoft Agent Framework 的 AIAgent 提供;
/// ChatService 只相依此介面,單元測試可自行 fake。
/// </summary>
public interface ILlmAgent
{
    /// <summary>阻塞式:送出整串訊息,取回完整回覆字串。tools 非空時啟用 function calling(工具迴圈由實作處理)。</summary>
    Task<string> CompleteAsync(IReadOnlyList<LlmMessage> messages, IReadOnlyList<LlmTool>? tools, CancellationToken ct);

    /// <summary>串流式:逐塊吐出回覆片段(已過濾空字串 chunk)。tools 同上。</summary>
    IAsyncEnumerable<string> StreamAsync(IReadOnlyList<LlmMessage> messages, IReadOnlyList<LlmTool>? tools, CancellationToken ct);
}
