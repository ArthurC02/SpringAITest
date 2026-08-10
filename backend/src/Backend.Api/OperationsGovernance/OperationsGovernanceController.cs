using Backend.Api.Common;
using Backend.Api.RuntimeDiscovery;
using Backend.Api.Skills;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json.Serialization;
using static Backend.Api.Common.ApiErrors;

namespace Backend.Api.OperationsGovernance;

/// <summary>D7 release-control plane. Persistent records govern future-root selection only.</summary>
[ApiController, Route("api/admin/operations")]
public sealed class OperationsGovernanceController(
    RuntimeDiscoveryService runtime,
    IRuntimeBindingRepository bindings,
    IOperationsGovernanceRepository governance,
    IEvalRepository evals) : ControllerBase
{
    [HttpPost("regressions")]
    public async Task<IActionResult> RecordRegression(RegressionRequest request, CancellationToken ct)
    {
        RequireManage(); var tenant = Request.RequireTenant();
        var suite = Required(request.Suite, "suite", 128); var evidence = Required(request.EvidenceRef, "evidence_ref", 256);

        // E3 gate closure: when the caller pins a completed eval result, Backend recomputes
        // pass/fail itself from stored suite policy/case results -- it never trusts request.Passed
        // in that case. Omitting eval_run_id keeps the pre-existing caller-supplied `passed`
        // contract byte-for-byte unchanged (removing that path entirely is C2 cleanup, not this phase).
        var passed = request.Passed;
        if (request.EvalRunId is Guid evalRunId)
        {
            var evaluation = await evals.EvaluateGateAsync(tenant, evalRunId, suite, ct)
                ?? throw new ApiException(400, "eval_run_id not found for this tenant");
            passed = evaluation.Passed;
        }

        var gate = await governance.RecordRegressionAsync(tenant, suite, passed, evidence, Request.RequireUserId(), ct);
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
    public async Task<IActionResult> Metrics([FromQuery(Name = "window_days")] int? windowDays, CancellationToken ct)
    {
        RequireManage(); var window = Window(windowDays);
        var value = await governance.GetMetricsAsync(Request.RequireTenant(), window, ct);
        // DTO is aggregate-only: no prompts, tool values, tokens, context or idempotency keys.
        // window_days 是刻意外露的:數字只涵蓋最近 N 天,不告訴呼叫方就是把效能問題轉嫁成正確性問題。
        return Ok(new { window_days = window, release_gate = new { regression_passed = value.RegressionPassed, override_active = value.OverrideActive, audit_entries = value.ReleaseAuditEntries }, multi_agent = new { rollout_events = value.RolloutEvents, root_runs = value.RootRuns, child_runs = value.ChildRuns, child_success = value.ChildSucceeded, verifier_reject = value.VerifierRejected, repair_rounds = value.RepairEvents, write_effects = value.WriteEffects, agents = value.Agents, skills = value.Skills, tools = value.Tools, nodes = value.Nodes, aggregation = value.Aggregation } });
    }

    [HttpGet("version-comparison")]
    public async Task<IActionResult> Compare([FromQuery(Name = "window_days")] int? windowDays, CancellationToken ct)
    {
        RequireManage(); var window = Window(windowDays); var tenant = Request.RequireTenant(); var binding = await bindings.GetAsync(tenant, ct);
        return Ok(await governance.GetVersionComparisonAsync(tenant, binding?.DefaultOrchestratorRevision, window, ct));
    }

    /// <summary>W2-02(e):有界統計時間窗;超界拒絕(不夾邊界)。<see cref="OperationsMetricsWindow"/> 說明理由。</summary>
    private static int Window(int? windowDays)
        => windowDays is null ? OperationsMetricsWindow.DefaultDays
            : windowDays is >= OperationsMetricsWindow.MinDays and <= OperationsMetricsWindow.MaxDays ? windowDays.Value
            : throw new ApiException(400, "window_days 必須介於 1 到 365");

    [HttpGet("legacy-inventory")]
    public async Task<IActionResult> LegacyInventory(CancellationToken ct)
    { RequireManage(); return Ok(await governance.GetLegacyInventoryAsync(Request.RequireTenant(), ct)); }

    /// <summary>E1 dual-write audit: proves the extended envelope never becomes a second
    /// usage/cost authority alongside the legacy metric ledger.</summary>
    [HttpGet("evidence-reconcile")]
    public async Task<IActionResult> EvidenceReconcile(CancellationToken ct)
    { RequireManage(); return Ok(await governance.GetEvidenceReconcileAsync(Request.RequireTenant(), ct)); }

    private void RequireManage() => Request.RequireCapability("workflow.manage");
    private string Key() => Request.RequireIdempotencyKey();
    private static ReleaseGateResponse Public(RegressionGate gate) => new(gate.Passed, gate.OverrideActive, gate.AuditEntries);
}

public sealed record RegressionRequest([property: JsonPropertyName("suite")] string? Suite, [property: JsonPropertyName("passed")] bool Passed, [property: JsonPropertyName("evidence_ref")] string? EvidenceRef, [property: JsonPropertyName("eval_run_id")] Guid? EvalRunId = null);
public sealed record OverrideRequest([property: JsonPropertyName("reason")] string? Reason);
public sealed record RolloutRequest([property: JsonPropertyName("enabled")] bool Enabled, [property: JsonPropertyName("orchestrator_id")] Guid? OrchestratorId, [property: JsonPropertyName("revision")] int? Revision, [property: JsonPropertyName("canary_user_ids")] IReadOnlyList<string>? CanaryUserIds);
public sealed record ReleaseGateResponse([property: JsonPropertyName("regression_passed")] bool RegressionPassed, [property: JsonPropertyName("override_active")] bool OverrideActive, [property: JsonPropertyName("audit_entries")] int AuditEntries);
public sealed record RolloutResponse([property: JsonPropertyName("binding")] TenantRuntimeBinding Binding, [property: JsonPropertyName("new_roots_only")] bool NewRootsOnly);
