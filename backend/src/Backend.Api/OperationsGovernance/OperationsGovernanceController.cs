using Backend.Api.Common;
using Backend.Api.RuntimeDiscovery;
using Backend.Api.Skills;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json.Serialization;

namespace Backend.Api.OperationsGovernance;

/// <summary>D7 release-control plane. Persistent records govern future-root selection only.</summary>
[ApiController, Route("api/admin/operations")]
public sealed class OperationsGovernanceController(
    RuntimeDiscoveryService runtime,
    IRuntimeBindingRepository bindings,
    IOperationsGovernanceRepository governance) : ControllerBase
{
    [HttpPost("regressions")]
    public async Task<IActionResult> RecordRegression(RegressionRequest request, CancellationToken ct)
    {
        RequireManage(); var tenant = Request.RequireTenant();
        var suite = Required(request.Suite, "suite", 128); var evidence = Required(request.EvidenceRef, "evidence_ref", 256);
        var gate = await governance.RecordRegressionAsync(tenant, suite, request.Passed, evidence, Request.RequireUserId(), ct);
        return Ok(Public(gate));
    }

    [HttpPost("regression-overrides")]
    public async Task<IActionResult> OverrideRegression(OverrideRequest request, CancellationToken ct)
    {
        RequireManage(); var tenant = Request.RequireTenant(); var key = Key();
        var reason = Required(request.Reason, "an explicit override reason", 1000);
        if (reason.Length < 8) throw new ApiException(400, "an explicit override reason is required");
        var gate = await governance.GetCurrentGateAsync(tenant, ct) ?? throw new ApiException(409, "no regression result is available to override");
        var result = await governance.CreateOverrideAsync(tenant, gate.Id, SkillHash.Sha256(key), reason, Request.RequireUserId(), ct);
        return result.Status switch
        {
            OverrideWriteStatus.Accepted or OverrideWriteStatus.Replay => Ok(Public(result.Gate!)),
            OverrideWriteStatus.NoLongerRequired => throw new ApiException(409, "a passing regression gate cannot be overridden"),
            _ => throw new ApiException(409, "regression gate changed; reload before overriding"),
        };
    }

    [HttpPut("rollout")]
    public async Task<IActionResult> Rollout(RolloutRequest request, CancellationToken ct)
    {
        RequireManage(); var tenant = Request.RequireTenant();
        var binding = runtime.ValidateBinding(new TenantRuntimeBindingUpsert(
            request.Enabled, request.OrchestratorId, request.Revision, request.CanaryUserIds));
        if (await governance.ApplyRolloutAsync(tenant, binding, Request.RequireUserId(), ct)
            == RolloutWriteStatus.RegressionBlocked)
            throw new ApiException(409, "regression gate blocks rollout without an audited override");
        return Ok(new RolloutResponse(binding, true));
    }

    [HttpGet("metrics")]
    public async Task<IActionResult> Metrics(CancellationToken ct)
    {
        RequireManage(); var value = await governance.GetMetricsAsync(Request.RequireTenant(), ct);
        // DTO is aggregate-only: no prompts, tool values, tokens, context or idempotency keys.
        return Ok(new { release_gate = new { regression_passed = value.RegressionPassed, override_active = value.OverrideActive, audit_entries = value.ReleaseAuditEntries }, multi_agent = new { rollout_events = value.RolloutEvents, root_runs = value.RootRuns, child_runs = value.ChildRuns, child_success = value.ChildSucceeded, verifier_reject = value.VerifierRejected, repair_rounds = value.RepairEvents, write_effects = value.WriteEffects, agents = value.Agents, skills = value.Skills, tools = value.Tools, nodes = value.Nodes, aggregation = value.Aggregation } });
    }

    [HttpGet("version-comparison")]
    public async Task<IActionResult> Compare(CancellationToken ct)
    {
        RequireManage(); var tenant = Request.RequireTenant(); var binding = await bindings.GetAsync(tenant, ct);
        return Ok(await governance.GetVersionComparisonAsync(tenant, binding?.DefaultOrchestratorRevision, ct));
    }

    [HttpGet("legacy-inventory")]
    public async Task<IActionResult> LegacyInventory(CancellationToken ct)
    { RequireManage(); return Ok(await governance.GetLegacyInventoryAsync(Request.RequireTenant(), ct)); }

    private void RequireManage() { if (!Request.HasCapability("workflow.manage")) throw new ApiException(403, "workflow.manage capability is required"); }
    private string Key()
    {
        var values = Request.Headers["Idempotency-Key"]; var key = values.Count == 1 ? values[0]?.Trim() : null;
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128 || key.Any(char.IsControl)) throw new ApiException(400, "Idempotency-Key is required");
        return key;
    }
    private static string Required(string? value, string field, int max)
    {
        value = value?.Trim();
        if (string.IsNullOrWhiteSpace(value) || value.Length > max || value.Any(char.IsControl)) throw new ApiException(400, $"{field} is required");
        return value;
    }
    private static ReleaseGateResponse Public(RegressionGate gate) => new(gate.Passed, gate.OverrideActive, gate.AuditEntries);
}

public sealed record RegressionRequest([property: JsonPropertyName("suite")] string? Suite, [property: JsonPropertyName("passed")] bool Passed, [property: JsonPropertyName("evidence_ref")] string? EvidenceRef);
public sealed record OverrideRequest([property: JsonPropertyName("reason")] string? Reason);
public sealed record RolloutRequest([property: JsonPropertyName("enabled")] bool Enabled, [property: JsonPropertyName("orchestrator_id")] Guid? OrchestratorId, [property: JsonPropertyName("revision")] int? Revision, [property: JsonPropertyName("canary_user_ids")] IReadOnlyList<string>? CanaryUserIds);
public sealed record ReleaseGateResponse([property: JsonPropertyName("regression_passed")] bool RegressionPassed, [property: JsonPropertyName("override_active")] bool OverrideActive, [property: JsonPropertyName("audit_entries")] int AuditEntries);
public sealed record RolloutResponse([property: JsonPropertyName("binding")] TenantRuntimeBinding Binding, [property: JsonPropertyName("new_roots_only")] bool NewRootsOnly);
