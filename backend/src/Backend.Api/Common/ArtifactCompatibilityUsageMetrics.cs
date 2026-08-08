using System.Diagnostics.Metrics;
using System.Text.Json;

namespace Backend.Api.Common;

public sealed class ArtifactCompatibilityUsageMetrics : IDisposable
{
    public const string MeterName = "Backend.Api.ArtifactCompatibilityUsage";
    public const string CounterName = "artifact_compatibility_usage_total";

    public static ArtifactCompatibilityUsageMetrics Shared { get; } = new(MeterName);
    public static readonly object CountedItemKey = new();

    // 有界維度白名單(根 AGENTS.md 不變量:artifact_compatibility_usage_total 只用有界維度)。
    // **集合真相來源是 workflow/app/usage_evidence.py 的 `_ALLOWED`**,fail-fast 語意(未知值直接丟,
    // 不是靜默記一筆)也由它鏡像而來;各服務**自己**能發出哪些 surface/operation 則由
    // scripts/export-artifact-compatibility-usage-v1.py 的 AUTHORITY["backend"] 界定 —— backend 是
    // 公開 artifact CRUD/讀取的權威,不共用 workflow 的內部 surface 或 validate/invoke。
    // 改任一處須同步其餘兩處,否則匯出器會收到高基數標籤而整份證據作廢。
    //
    // 逐 surface 配對(不是平坦兩集合):匯出器檢查的是 `operation in AUTHORITY[service][surface]`,
    // 平坦集合會放行 AUTHORITY 沒有的組合(例:public_business_workflows × revision_read),
    // 匯出時整份證據 ExportError 作廢。下表逐字鏡像 AUTHORITY["backend"]。
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> SurfaceOperations =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["public_skills"] = new HashSet<string>(StringComparer.Ordinal)
            {
                "list", "read", "create", "update", "delete", "import", "export",
                "package", "revision_read", "revision_restore", "execution_artifact",
            },
            ["public_business_workflows"] = new HashSet<string>(StringComparer.Ordinal)
            {
                "list", "read", "create", "update", "delete", "export",
            },
        };

    private static readonly IReadOnlySet<string> ArtifactTypes =
        new HashSet<string>(StringComparer.Ordinal) { "agent_skill", "business_workflow", "unknown" };

    private static readonly IReadOnlySet<string> Outcomes =
        new HashSet<string>(StringComparer.Ordinal) { "success", "rejected", "not_found", "error" };

    private readonly Meter _meter;
    private readonly Counter<long> _usage;

    public ArtifactCompatibilityUsageMetrics(string meterName)
    {
        _meter = new Meter(meterName);
        _usage = _meter.CreateCounter<long>(CounterName);
    }

    public async Task<T> TrackAsync<T>(HttpContext context, string surface, string operation, Func<ArtifactUsage, Task<T>> action)
    {
        var usage = new ArtifactUsage();
        try
        {
            var result = await action(usage);
            Record(surface, operation, usage.ResolvedArtifactTypes, "success");
            return result;
        }
        catch (ApiException ex)
        {
            RecordOnErrorPath(surface, operation, usage.ResolvedArtifactTypes, ex.Status switch
            {
                StatusCodes.Status404NotFound => "not_found",
                >= 400 and < 500 => "rejected",
                _ => "error",
            });
            throw;
        }
        catch
        {
            RecordOnErrorPath(surface, operation, usage.ResolvedArtifactTypes, "error");
            throw;
        }
        finally
        {
            context.Items[CountedItemKey] = true;
        }
    }

    public void RecordRequestFailure(string surface, string operation, int statusCode)
        => Record(surface, operation, ["unknown"], statusCode switch
        {
            StatusCodes.Status404NotFound => "not_found",
            >= 400 and < 500 => "rejected",
            _ => "error",
        });

    // 錯誤路徑專用:此時呼叫端已有一個要往上丟的例外,白名單 fail-fast 若在這裡炸開會**取代**它,
    // 把原本的 4xx 變成 500 —— 觀測絕不能改變 API 回應。成功路徑仍維持 fail-fast(見 Record)。
    private void RecordOnErrorPath(
        string surface, string operation, IEnumerable<string> resolvedArtifactTypes, string outcome)
    {
        try
        {
            Record(surface, operation, resolvedArtifactTypes, outcome);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"artifact compatibility usage evidence dropped: {ex.Message}");
        }
    }

    private void Record(string surface, string operation, IEnumerable<string> resolvedArtifactTypes, string outcome)
    {
        foreach (var resolvedArtifactType in resolvedArtifactTypes)
        {
            Record(surface, operation, resolvedArtifactType, outcome);
        }
    }

    private void Record(string surface, string operation, string resolvedArtifactType, string outcome)
    {
        // 白名單守門刻意在 try 之外:try 是為了「觀測不得影響 API 行為」而吞監聽器例外,
        // 未知維度值卻是程式錯誤,吞掉就等於默默寫出無界標籤。
        RequireAuthoritative(surface, operation);
        RequireBounded(ArtifactTypes, resolvedArtifactType, "resolved_artifact_type");
        RequireBounded(Outcomes, outcome, "outcome");
        try
        {
            _usage.Add(1,
                new("service", "backend"),
                new("surface", surface),
                new("operation", operation),
                new("resolved_artifact_type", resolvedArtifactType),
                new("outcome", outcome));
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                @event = CounterName,
                timestampUtc = DateTime.UtcNow.ToString("O"),
                deploymentVersion = Environment.GetEnvironmentVariable("DEPLOYMENT_VERSION") ?? "unknown",
                service = "backend",
                surface,
                operation,
                resolvedArtifactType,
                outcome,
                count = 1,
            }));
        }
        catch
        {
            // Instrument listeners are optional observability; they must not affect API behavior.
        }
    }

    /// <summary>backend 是否為這個 surface×operation 的權威(<c>AUTHORITY["backend"]</c> 配對)。</summary>
    public static bool IsAuthoritative(string surface, string operation)
        => SurfaceOperations.TryGetValue(surface, out var operations) && operations.Contains(operation);

    private static void RequireAuthoritative(string surface, string operation)
    {
        if (!SurfaceOperations.TryGetValue(surface, out var operations))
        {
            throw new ArgumentException("unsupported usage evidence surface", nameof(surface));
        }

        RequireBounded(operations, operation, nameof(operation));
    }

    private static void RequireBounded(IReadOnlySet<string> allowed, string value, string dimension)
    {
        if (!allowed.Contains(value))
        {
            throw new ArgumentException($"unsupported usage evidence {dimension}", dimension);
        }
    }

    public void Dispose() => _meter.Dispose();
}

public sealed class ArtifactUsage
{
    public IReadOnlyList<string> ResolvedArtifactTypes { get; private set; } = ["unknown"];

    public void Resolve(string? kind)
        => ResolvedArtifactTypes = [ResolveKind(kind)];

    private static string ResolveKind(string? kind)
        => kind switch
        {
            "agentic" => "agent_skill",
            "flow" => "business_workflow",
            _ => "unknown",
        };

    public void Resolve(IEnumerable<string> kinds)
    {
        ResolvedArtifactTypes = kinds.Select(ResolveKind).Distinct(StringComparer.Ordinal).Take(3).ToArray();
        if (ResolvedArtifactTypes.Count == 0)
        {
            ResolvedArtifactTypes = ["unknown"];
        }
    }
}
