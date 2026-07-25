using Backend.Api.Common;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json.Serialization;

namespace Backend.Api.OperationsGovernance;

/// <summary>Workflow-internal, append-only, redacted execution metering.</summary>
[ApiController, Route("api/operations/telemetry")]
public sealed class OperationsTelemetryController(IOperationsGovernanceRepository governance) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Append(OperationsTelemetryRequest request, CancellationToken ct)
    {
        var tenant = Request.RequireTenant();
        if (request.RunId == Guid.Empty || request.EventId == Guid.Empty || request.Kind is not ("model" or "tool" or "node")
            || !Safe(request.NodeId, 200) || !Safe(request.ToolName, 200) || !Safe(request.SkillName, 128) || !Safe(request.AgentId, 128) || request.SkillRevision is < 1 or > 1_000_000 || request.AgentRevision is < 1 or > 1_000_000
            || request.UsageUnits is < 0 or > 10_000_000 || request.CostUnits is < 0 or > 1_000_000m || request.LatencyMs is < 0 or > 86_400_000)
            throw new ApiException(400, "invalid execution telemetry");
        await governance.RecordTelemetryAsync(tenant, new(request.RunId, request.EventId, request.Kind, request.NodeId, request.ToolName, request.SkillName, request.SkillRevision, request.AgentId, request.AgentRevision, request.UsageUnits, request.CostUnits, request.LatencyMs), ct);
        // Workflow's shared internal client validates JSON responses; return a deliberately
        // content-free acknowledgement object instead of 204 so a successful append is not
        // mistaken for a transport failure and silently dropped.
        return Ok(new { accepted = true });
    }
    private static bool Safe(string? value, int max) => value is null || value.Length <= max && !value.Any(char.IsControl);
}
public sealed record OperationsTelemetryRequest([property: JsonPropertyName("run_id")] Guid RunId, [property: JsonPropertyName("event_id")] Guid EventId, [property: JsonPropertyName("kind")] string? Kind, [property: JsonPropertyName("node_id")] string? NodeId, [property: JsonPropertyName("tool_name")] string? ToolName, [property: JsonPropertyName("skill_name")] string? SkillName, [property: JsonPropertyName("skill_revision")] int? SkillRevision, [property: JsonPropertyName("agent_id")] string? AgentId, [property: JsonPropertyName("agent_revision")] int? AgentRevision, [property: JsonPropertyName("usage_units")] long? UsageUnits, [property: JsonPropertyName("cost_units")] decimal? CostUnits, [property: JsonPropertyName("latency_ms")] long? LatencyMs);
