using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Service;
using Platform.Web.Auth;

namespace Platform.Web.Controllers;

/// <summary>D7 redacted operations proxy. Backend remains the release/audit authority.</summary>
[ApiController, Route("api/admin/operations"), Authorize(Policy = "workflow.manage")]
public sealed class OperationsGovernanceController(BackendClient backend) : ProxyControllerBase
{
    [HttpGet("metrics")] public Task<IActionResult> Metrics(CancellationToken ct) => Send(HttpMethod.Get, "metrics", null, ct);
    [HttpGet("version-comparison")] public Task<IActionResult> Compare(CancellationToken ct) => Send(HttpMethod.Get, "version-comparison", null, ct);
    [HttpGet("legacy-inventory")] public Task<IActionResult> Inventory(CancellationToken ct) => Send(HttpMethod.Get, "legacy-inventory", null, ct);
    [HttpGet("evidence-reconcile")] public Task<IActionResult> EvidenceReconcile(CancellationToken ct) => Send(HttpMethod.Get, "evidence-reconcile", null, ct);
    [HttpPost("regressions")] public Task<IActionResult> Regression([FromBody] object body, CancellationToken ct) => Send(HttpMethod.Post, "regressions", body, ct);
    [HttpPost("regression-overrides")] public Task<IActionResult> Override([FromBody] object body, CancellationToken ct) => Send(HttpMethod.Post, "regression-overrides", body, ct, true);
    [HttpPut("rollout")] public Task<IActionResult> Rollout([FromBody] object body, CancellationToken ct) => Send(HttpMethod.Put, "rollout", body, ct);
    [HttpGet("eval-suites")] public Task<IActionResult> ListEvalSuites(CancellationToken ct) => Send(HttpMethod.Get, "eval-suites", null, ct);
    [HttpGet("eval-suites/{suiteId}")] public Task<IActionResult> GetEvalSuite(string suiteId, CancellationToken ct) => Send(HttpMethod.Get, "eval-suites/" + suiteId, null, ct);
    [HttpPost("eval-runs")] public Task<IActionResult> CreateEvalRun([FromBody] object body, CancellationToken ct) => Send(HttpMethod.Post, "eval-runs", body, ct, true);
    [HttpGet("eval-runs")] public Task<IActionResult> ListEvalRuns(CancellationToken ct) => Send(HttpMethod.Get, "eval-runs", null, ct);
    [HttpGet("eval-runs/{runId}")] public Task<IActionResult> GetEvalRun(string runId, CancellationToken ct) => Send(HttpMethod.Get, "eval-runs/" + runId, null, ct);
    private async Task<IActionResult> Send(HttpMethod method, string suffix, object? body, CancellationToken ct, bool key = false)
    {
        var response = await backend.SendForAgentProxyAsync(
            method,
            "/api/admin/operations/" + suffix,
            User.ToUserContext(),
            body,
            "Operations backend ",
            key && IdempotencyKey is { } idempotencyKey ? ("Idempotency-Key", idempotencyKey) : null,
            ct);
        return Write(response);
    }
}
