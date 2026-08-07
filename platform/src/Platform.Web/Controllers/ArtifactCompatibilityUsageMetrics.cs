using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace Platform.Web.Controllers;

internal static class ArtifactCompatibilityUsageMetrics
{
    internal const string MeterName = "Platform.Web.ArtifactCompatibilityUsage";
    internal const string CounterName = "artifact_compatibility_usage_total";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Usage = Meter.CreateCounter<long>(CounterName);
    private static readonly object ActionReachedMarker = new();

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
            Record(surface, "unknown", "error");
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
                Record(value.Surface, value.Operation, "unknown", context.Response.StatusCode switch
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

    private static void Record(
        string surface, string operation, string resolvedArtifactType, string outcome)
    {
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
}
