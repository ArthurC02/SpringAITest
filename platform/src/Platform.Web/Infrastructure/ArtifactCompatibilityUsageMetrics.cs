using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Controllers;
using Platform.Web.Controllers;

namespace Platform.Web.Infrastructure;

internal static class ArtifactCompatibilityUsageMetrics
{
    internal const string MeterName = "Platform.Web.ArtifactCompatibilityUsage";
    internal const string CounterName = "artifact_compatibility_usage_total";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Usage = Meter.CreateCounter<long>(CounterName);
    private static readonly object ActionReachedMarker = new();

    // 有界維度白名單(根 AGENTS.md 不變量:artifact_compatibility_usage_total 只用有界維度)。
    // **集合真相來源是 workflow/app/usage_evidence.py 的 `_ALLOWED`**,fail-fast 語意(未知值直接丟,
    // 不是靜默記一筆)也由它鏡像而來;各服務**自己**能發出哪些 surface/operation 則由
    // scripts/export-artifact-compatibility-usage-v1.py 的 AUTHORITY["platform"] 界定 —— platform 是
    // public validate/invoke 的權威,不共用 workflow 的內部 surface。改任一處須同步其餘兩處,
    // 否則匯出器會收到高基數標籤而整份證據作廢。
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> SurfaceOperations =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["public_skills"] = new HashSet<string>(StringComparer.Ordinal) { "validate", "invoke" },
            ["public_business_workflows"] = new HashSet<string>(StringComparer.Ordinal) { "validate" },
        };

    private static readonly IReadOnlySet<string> ArtifactTypes =
        new HashSet<string>(StringComparer.Ordinal) { "agent_skill", "business_workflow", "unknown" };

    private static readonly IReadOnlySet<string> Outcomes =
        new HashSet<string>(StringComparer.Ordinal) { "success", "rejected", "not_found", "error" };

    internal static async Task<JsonElement> TrackValidationAsync(
        HttpContext context, string surface, Func<Task<JsonElement>> action)
    {
        MarkActionReached(context);
        try
        {
            var result = await action();
            Record(surface, ResolveType(result), IsValid(result) ? "success" : "rejected");
            return result;
        }
        catch
        {
            RecordOnErrorPath(surface, "validate", "unknown", "error");
            throw;
        }
    }

    internal static void MarkActionReached(HttpContext context)
        => context.Items[ActionReachedMarker] = true;

    internal static async Task CountPreControllerFailureAsync(HttpContext context, RequestDelegate next)
    {
        var action = context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>();
        var dimensions = action is null ? null : (action.ControllerName, action.ActionName) switch
        {
            ("Skill", nameof(SkillController.Validate))
                => (Surface: "public_skills", Operation: "validate"),
            ("BusinessWorkflow", nameof(BusinessWorkflowController.Validate))
                => (Surface: "public_business_workflows", Operation: "validate"),
            ("Skill", nameof(SkillController.Invoke))
                => (Surface: "public_skills", Operation: "invoke"),
            _ => ((string Surface, string Operation)?)null,
        };

        try
        {
            await next(context);
        }
        finally
        {
            if (dimensions is { } value
                && context.Response.StatusCode >= 400
                && !context.Items.ContainsKey(ActionReachedMarker))
            {
                RecordOnErrorPath(value.Surface, value.Operation, "unknown", context.Response.StatusCode switch
                {
                    StatusCodes.Status404NotFound => "not_found",
                    < StatusCodes.Status500InternalServerError => "rejected",
                    _ => "error",
                });
            }
        }
    }

    private static bool IsValid(JsonElement result)
        => result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("valid", out var valid)
            && valid.ValueKind == JsonValueKind.True;

    private static string ResolveType(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("skill", out var skill)
            || skill.ValueKind != JsonValueKind.Object
            || !skill.TryGetProperty("kind", out var kind)
            || kind.ValueKind != JsonValueKind.String)
        {
            return "unknown";
        }

        return kind.GetString() switch
        {
            "agentic" => "agent_skill",
            "flow" => "business_workflow",
            _ => "unknown",
        };
    }

    private static void Record(
        string surface, string resolvedArtifactType, string outcome)
        => Record(surface, "validate", resolvedArtifactType, outcome);

    // 錯誤路徑專用:此時已有一個要往上丟的例外(或一個已定案的 4xx 回應),白名單 fail-fast 若在這裡炸開
    // 會取代它,把原本的 4xx 變成 500 —— 觀測絕不能改變 API 回應。成功路徑仍維持 fail-fast(見 Record)。
    private static void RecordOnErrorPath(
        string surface, string operation, string resolvedArtifactType, string outcome)
    {
        try
        {
            Record(surface, operation, resolvedArtifactType, outcome);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"artifact compatibility usage evidence dropped: {ex.Message}");
        }
    }

    private static void Record(
        string surface, string operation, string resolvedArtifactType, string outcome)
    {
        // 白名單守門刻意在 try 之外:try 是為了「觀測不得影響 API 行為」而吞監聽器例外,
        // 未知維度值卻是程式錯誤,吞掉就等於默默寫出無界標籤。
        RequireAuthoritative(surface, operation);
        RequireBounded(ArtifactTypes, resolvedArtifactType, "resolved_artifact_type");
        RequireBounded(Outcomes, outcome, "outcome");
        try
        {
            Usage.Add(1,
                new("service", "platform"),
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
                service = "platform",
                surface,
                operation,
                resolvedArtifactType,
                outcome,
                count = 1,
            }));
        }
        catch
        {
            // Optional observability listeners must not affect API behavior.
        }
    }

    internal static void RequireAuthoritative(string surface, string operation)
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
}
