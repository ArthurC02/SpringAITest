using System.Diagnostics;
using OpenTelemetry;

namespace Platform.Service;

/// <summary>
/// OTel Lite 模式遙測儲存:固定容量 200 span 的環形緩衝。
/// 超限時自動出隊最舊,get_recent_traces 工具讀最近 N 條 trace。
/// 執行緒安全:enqueue+trim 與 snapshot 皆在單一 lock 下,任何觀察者看到的筆數恆 ≤ 200
/// (lock-free enqueue-then-trim 會讓讀者瞬間看到 201,故此處用 lock 保證嚴格上限)。
/// 生命週期:Singleton,由 Program.cs 建立後同時交給 OTel pipeline 與 DI 容器(同一顆)。
/// 進程層級、所有使用者共用(Lite 定位開發機,非多租戶隔離)。
/// </summary>
public sealed class RingBufferActivityExporter : BaseExporter<Activity>
{
    private const int MaxCapacity = 200;

    // 白名單:只留給 LLM 讀得懂的關鍵 attribute,避免序列化整包 tags。
    private static readonly HashSet<string> KeyAttributes = new(StringComparer.Ordinal)
    {
        "skill_name", "model", "tool_name", "error", "document_id", "query",
    };

    private readonly Queue<ActivitySnapshot> _buffer = new();
    private readonly object _lock = new();

    /// <summary>簡化後的 Activity 快照(供 LLM 讀)。</summary>
    public sealed class ActivitySnapshot
    {
        public DateTime StartTimeUtc { get; init; }
        public DateTime EndTimeUtc { get; init; }
        public string Name { get; init; } = "";
        public TimeSpan Duration { get; init; }
        public Dictionary<string, object?> Attributes { get; init; } = new();
    }

    /// <summary>OTel export 進入點:批次 Activity → 逐一入隊,超容量從隊頭出隊維持上限。</summary>
    public override ExportResult Export(in Batch<Activity> batch)
    {
        foreach (var activity in batch)
        {
            var snapshot = new ActivitySnapshot
            {
                StartTimeUtc = activity.StartTimeUtc,
                EndTimeUtc = activity.StartTimeUtc.Add(activity.Duration),
                Name = activity.DisplayName,
                Duration = activity.Duration,
                Attributes = ExtractKeyAttributes(activity),
            };

            lock (_lock)
            {
                _buffer.Enqueue(snapshot);
                while (_buffer.Count > MaxCapacity)
                {
                    _buffer.Dequeue();
                }
            }
        }

        return ExportResult.Success;
    }

    /// <summary>
    /// 讀取最近的 spans。limit=null 回全部;否則回最新 limit 條。皆為最新優先(遞減)。
    /// </summary>
    public IReadOnlyList<ActivitySnapshot> GetRecentSpans(int? limit = null)
    {
        ActivitySnapshot[] snapshot;
        lock (_lock)
        {
            snapshot = _buffer.ToArray(); // 舊→新
        }

        IEnumerable<ActivitySnapshot> recent = snapshot;
        if (limit.HasValue)
        {
            recent = snapshot.TakeLast(limit.Value);
        }

        return recent.Reverse().ToList(); // 最新優先
    }

    private static Dictionary<string, object?> ExtractKeyAttributes(Activity activity)
    {
        var result = new Dictionary<string, object?>();
        foreach (var tag in activity.TagObjects)
        {
            if (KeyAttributes.Contains(tag.Key))
            {
                result[tag.Key] = tag.Value;
            }
        }

        return result;
    }
}
