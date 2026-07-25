using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Service;
using Platform.Service.Exceptions;
using Platform.Web.Auth;

namespace Platform.Web.Controllers;

/// <summary>D7 redacted operations proxy. Backend remains the release/audit authority.</summary>
[ApiController, Route("api/admin/operations"), Authorize(Policy = "workflow.manage")]
public sealed class OperationsGovernanceController(BackendClient backend) : ControllerBase
{
    [HttpGet("metrics")] public Task<IActionResult> Metrics(CancellationToken ct) => Send(HttpMethod.Get, "metrics", null, ct);
    [HttpGet("version-comparison")] public Task<IActionResult> Compare(CancellationToken ct) => Send(HttpMethod.Get, "version-comparison", null, ct);
    [HttpGet("legacy-inventory")] public Task<IActionResult> Inventory(CancellationToken ct) => Send(HttpMethod.Get, "legacy-inventory", null, ct);
    [HttpPost("regressions")] public Task<IActionResult> Regression([FromBody] object body, CancellationToken ct) => Send(HttpMethod.Post, "regressions", body, ct);
    [HttpPost("regression-overrides")] public Task<IActionResult> Override([FromBody] object body, CancellationToken ct) => Send(HttpMethod.Post, "regression-overrides", body, ct, true);
    [HttpPut("rollout")] public Task<IActionResult> Rollout([FromBody] object body, CancellationToken ct) => Send(HttpMethod.Put, "rollout", body, ct);
    private async Task<IActionResult> Send(HttpMethod method, string suffix, object? body, CancellationToken ct, bool key = false)
    {
        using var request = backend.BuildRequest(method, "/api/admin/operations/" + suffix, User.ToUserContext(), body);
        if (key && Request.Headers.TryGetValue("Idempotency-Key", out var value)) request.Headers.TryAddWithoutValidation("Idempotency-Key", value.ToString());
        using var response = await backend.SendAsync(request, ex => new WorkflowInvocationException("Operations gateway unavailable", ex), ct);
        if ((int)response.StatusCode >= 500) throw new WorkflowInvocationException("Operations backend unavailable");
        return new ContentResult { StatusCode = (int)response.StatusCode, Content = await response.Content.ReadAsStringAsync(ct), ContentType = "application/json; charset=utf-8" };
    }
}
