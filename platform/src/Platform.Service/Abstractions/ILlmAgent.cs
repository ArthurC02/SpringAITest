namespace Platform.Service.Abstractions;

/// <summary>
/// LLM 代理的薄介面。實作在 Web 層以 Microsoft Agent Framework 的 AIAgent 提供;
/// 唯一消費者是 <see cref="Platform.Service.SkillRoutingAgent"/>(路由決策與命中後的摘要,
/// 刻意走這條「裸」LLM 而不經 ChatClientAgent),單元測試可自行 fake。
/// </summary>
public interface ILlmAgent
{
    /// <summary>阻塞式:送出整串訊息,取回完整回覆字串。</summary>
    Task<string> CompleteAsync(IReadOnlyList<LlmMessage> messages, CancellationToken ct);

    /// <summary>串流式:逐塊吐出回覆片段(已過濾空字串 chunk)。</summary>
    IAsyncEnumerable<string> StreamAsync(IReadOnlyList<LlmMessage> messages, CancellationToken ct);
}
