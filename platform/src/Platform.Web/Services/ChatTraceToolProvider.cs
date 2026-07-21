using Microsoft.Extensions.AI;
using Platform.Service;

namespace Platform.Web.Services;

/// <summary>
/// Lite 模式 only(OTEL_MODE=console):讓 LLM 讀最近的 trace 摘要,診斷聊天流程/工作流執行。
/// 由 Program.cs 在 console 模式時掛到兩條聊天管線共用的 ChatClientAgent 的 ChatOptions.Tools。
/// 進程層級 ring buffer、所有使用者共用——生產環境絕不啟用(治理鎖見 04-acceptance-test B-T-02)。
/// </summary>
public static class ChatTraceToolProvider
{
    /// <summary>LLM 讀到的單筆 trace(snake_case JSON 由 AIFunctionFactory 依屬性推導)。</summary>
    public sealed class TraceInfo
    {
        public DateTime TimestampUtc { get; init; }
        public string Name { get; init; } = "";
        public long DurationMs { get; init; }
        public Dictionary<string, object?> Attributes { get; init; } = new();
    }

    public sealed class GetRecentTracesResult
    {
        public List<TraceInfo> Traces { get; init; } = new();
    }

    /// <summary>
    /// 造出 get_recent_traces AIFunction。參數 schema 由 delegate 簽章推導:
    /// filter(選填,name/attributes 文字過濾,case-insensitive)、limit(預設 10)。
    /// </summary>
    public static AIFunction Create(RingBufferActivityExporter ringBuffer)
    {
        return AIFunctionFactory.Create(
            (string? filter, int limit) =>
            {
                var spans = ringBuffer.GetRecentSpans(limit > 0 ? limit : 10).ToList();

                if (!string.IsNullOrWhiteSpace(filter))
                {
                    spans = spans
                        .Where(s => s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                    || s.Attributes.Values.Any(v =>
                                        v?.ToString()?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))
                        .ToList();
                }

                return new GetRecentTracesResult
                {
                    Traces = spans.Select(s => new TraceInfo
                    {
                        TimestampUtc = s.StartTimeUtc,
                        Name = s.Name,
                        DurationMs = (long)s.Duration.TotalMilliseconds,
                        Attributes = s.Attributes,
                    }).ToList(),
                };
            },
            "get_recent_traces",
            "取得最近的追蹤資訊(spans):包括操作名稱、耗時、attributes。用於診斷聊天流程或查看工作流執行狀況。");
    }
}
