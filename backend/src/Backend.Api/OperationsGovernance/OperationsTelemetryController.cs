using Backend.Api.Common;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Backend.Api.OperationsGovernance;

/// <summary>Workflow-internal, append-only, redacted execution metering.</summary>
[ApiController, Route("api/operations/telemetry")]
public sealed class OperationsTelemetryController(
    IOperationsGovernanceRepository governance,
    RunEvidenceState evidence,
    ILogger<OperationsTelemetryController> logger) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Append(OperationsTelemetryRequest request, CancellationToken ct)
    {
        var tenant = Request.RequireTenant();
        if (request.RunId == Guid.Empty || request.EventId == Guid.Empty || request.Kind is not ("model" or "tool" or "node")
            || !Safe(request.NodeId, 200) || !Safe(request.ToolName, 200) || !Safe(request.SkillName, 128) || !Safe(request.AgentId, 128) || request.SkillRevision is < 1 or > 1_000_000 || request.AgentRevision is < 1 or > 1_000_000
            || request.UsageUnits is < 0 or > 10_000_000 || request.CostUnits is < 0 or > 1_000_000m || request.LatencyMs is < 0 or > 86_400_000)
            throw new ApiException(400, "invalid execution telemetry");
        // E1 dual-write: validated *before* the legacy metric write below, so a malformed nested
        // evidence request is rejected with 400 and changes nothing at all -- ingest failure must
        // never alter the caller's run outcome, and that includes "half accepted" (legacy metric
        // written, envelope rejected). Only attempted while the flag is on, so flag-off stays
        // byte-identical to the pre-E1 endpoint (no validation of the nested fields).
        if (evidence.Enabled && !SafeEvidence(request.Evidence, out var error))
        {
            throw new ApiException(400, error!);
        }

        await governance.RecordTelemetryAsync(tenant, new(request.RunId, request.EventId, request.Kind, request.NodeId, request.ToolName, request.SkillName, request.SkillRevision, request.AgentId, request.AgentRevision, request.UsageUnits, request.CostUnits, request.LatencyMs), ct);

        if (evidence.Enabled)
        {
            try
            {
                await governance.RecordEvidenceAsync(tenant, BuildEnvelope(request), ct);
            }
            catch (Exception ex)
            {
                // Evidence ingest must never change the caller's run outcome: log and swallow
                // instead of surfacing a 5xx that could make Workflow retry or fail the run.
                logger.LogWarning(ex, "run evidence envelope ingest failed (tenant={Tenant}, run={RunId}, event={EventId})", tenant, request.RunId, request.EventId);
            }
        }

        // Workflow's shared internal client validates JSON responses; return a deliberately
        // content-free acknowledgement object instead of 204 so a successful append is not
        // mistaken for a transport failure and silently dropped.
        return Ok(new { accepted = true });
    }

    private static RunEvidenceEnvelope BuildEnvelope(OperationsTelemetryRequest request)
    {
        var e = request.Evidence;
        var hasObserved = request.UsageUnits is not null || request.CostUnits is not null || request.LatencyMs is not null;
        // Missing usage/cost/latency is always "unknown", never silently coerced to measured=0.
        var quality = !hasObserved ? "unknown" : e?.ObservationQuality ?? "measured";
        return new(
            request.RunId, request.EventId, request.Kind!, e?.Outcome ?? "unknown", e?.ErrorClass, e?.SnapshotSha256,
            request.AgentId, request.AgentRevision, e?.OrchestratorRevision, request.SkillName, request.SkillRevision,
            e?.PromptManifestSha256, e?.ContextRevision, e?.RoleView, e?.PolicyRevision,
            e?.ModelProvider, e?.ModelDeployment, e?.ModelId, e?.ModelFingerprint, e?.ModelSettingsHash,
            request.ToolName, e?.ToolRevision, request.NodeId, e?.TraceId, e?.SpanId,
            request.UsageUnits, request.CostUnits, request.LatencyMs, quality,
            e?.VerifierVerdict, e?.CaseVerdict, e?.RedactionNote);
    }

    private static bool SafeEvidence(RunEvidenceRequest? e, out string? error)
    {
        error = null;
        if (e is null) return true;
        if (e.Outcome is not (null or "success" or "failure" or "unknown")) { error = "invalid evidence outcome"; return false; }
        if (e.ObservationQuality is not (null or "measured" or "estimated" or "unknown")) { error = "invalid evidence observation_quality"; return false; }
        if (!SafeErrorClass(e.ErrorClass) || !Safe(e.SnapshotSha256, 128) || !Safe(e.PromptManifestSha256, 128) || !Safe(e.RoleView, 128)
            || !Safe(e.ModelProvider, 128) || !Safe(e.ModelDeployment, 128) || !Safe(e.ModelId, 200) || !Safe(e.ModelFingerprint, 256)
            || !Safe(e.ModelSettingsHash, 128) || !Safe(e.TraceId, 128) || !Safe(e.SpanId, 128)
            || !Safe(e.VerifierVerdict, 64) || !Safe(e.CaseVerdict, 64) || !Safe(e.RedactionNote, 256))
        { error = "invalid execution telemetry evidence"; return false; }
        if (e.OrchestratorRevision is < 1 or > 1_000_000 || e.ContextRevision is < 1 or > 1_000_000
            || e.PolicyRevision is < 1 or > 1_000_000 || e.ToolRevision is < 1 or > 1_000_000)
        { error = "invalid execution telemetry evidence"; return false; }
        return true;
    }

    private static bool Safe(string? value, int max) => value is null || value.Length <= max && !value.Any(char.IsControl);

    // error_class is a machine token (exception/error type name), not free text -- shape-constrain
    // it so it can never smuggle control characters or oversized junk through the length-only Safe()
    // check; kept in sync with the DB CHECK on operations_run_evidence.error_class. \A/\z (not ^/$)
    // deliberately: .NET's $ matches before a trailing "\n", which would let "token\n" slip through.
    private static readonly Regex ErrorClassPattern = new(@"\A[A-Za-z0-9_.:+-]{1,200}\z", RegexOptions.Compiled);
    private static bool SafeErrorClass(string? value) => value is null || ErrorClassPattern.IsMatch(value);
}
public sealed record OperationsTelemetryRequest(
    [property: JsonPropertyName("run_id")] Guid RunId, [property: JsonPropertyName("event_id")] Guid EventId,
    [property: JsonPropertyName("kind")] string? Kind, [property: JsonPropertyName("node_id")] string? NodeId,
    [property: JsonPropertyName("tool_name")] string? ToolName, [property: JsonPropertyName("skill_name")] string? SkillName,
    [property: JsonPropertyName("skill_revision")] int? SkillRevision, [property: JsonPropertyName("agent_id")] string? AgentId,
    [property: JsonPropertyName("agent_revision")] int? AgentRevision, [property: JsonPropertyName("usage_units")] long? UsageUnits,
    [property: JsonPropertyName("cost_units")] decimal? CostUnits, [property: JsonPropertyName("latency_ms")] long? LatencyMs,
    [property: JsonPropertyName("evidence")] RunEvidenceRequest? Evidence);

/// <summary>
/// E1 extension of the telemetry contract, nested so the pre-existing top-level fields above stay
/// untouched. Ignored entirely while <c>RUN_EVIDENCE_ENABLED</c> is off. Never carries raw prompt,
/// context body, memory fact, tool argument/result, JWT, credential or chain-of-thought.
/// </summary>
public sealed record RunEvidenceRequest(
    [property: JsonPropertyName("outcome")] string? Outcome,
    [property: JsonPropertyName("error_class")] string? ErrorClass,
    [property: JsonPropertyName("snapshot_sha256")] string? SnapshotSha256,
    [property: JsonPropertyName("orchestrator_revision")] int? OrchestratorRevision,
    [property: JsonPropertyName("prompt_manifest_sha256")] string? PromptManifestSha256,
    [property: JsonPropertyName("context_revision")] int? ContextRevision,
    [property: JsonPropertyName("role_view")] string? RoleView,
    [property: JsonPropertyName("policy_revision")] int? PolicyRevision,
    [property: JsonPropertyName("model_provider")] string? ModelProvider,
    [property: JsonPropertyName("model_deployment")] string? ModelDeployment,
    [property: JsonPropertyName("model_id")] string? ModelId,
    [property: JsonPropertyName("model_fingerprint")] string? ModelFingerprint,
    [property: JsonPropertyName("model_settings_hash")] string? ModelSettingsHash,
    [property: JsonPropertyName("tool_revision")] int? ToolRevision,
    [property: JsonPropertyName("trace_id")] string? TraceId,
    [property: JsonPropertyName("span_id")] string? SpanId,
    [property: JsonPropertyName("observation_quality")] string? ObservationQuality,
    [property: JsonPropertyName("verifier_verdict")] string? VerifierVerdict,
    [property: JsonPropertyName("case_verdict")] string? CaseVerdict,
    [property: JsonPropertyName("redaction_note")] string? RedactionNote);
