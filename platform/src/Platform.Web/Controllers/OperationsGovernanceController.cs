using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Service;
using Platform.Service.Abstractions;
using Platform.Web.Auth;

namespace Platform.Web.Controllers;

/// <summary>D7 redacted operations proxy. Backend remains the release/audit authority.</summary>
[ApiController, Route("api/admin/operations"), Authorize(Policy = "workflow.manage")]
public sealed class OperationsGovernanceController(BackendClient backend) : ProxyControllerBase
{
    [HttpGet("metrics")] public Task<IActionResult> Metrics(CancellationToken ct) => Send(HttpMethod.Get, "metrics", null, ct);
    [HttpGet("version-comparison")] public Task<IActionResult> Compare(CancellationToken ct) => Send(HttpMethod.Get, "version-comparison", null, ct);
    [HttpGet("legacy-inventory")] public Task<IActionResult> Inventory(CancellationToken ct) => Send(HttpMethod.Get, "legacy-inventory", null, ct);
    [HttpPost("regressions")] public Task<IActionResult> Regression([FromBody] object body, CancellationToken ct) => Send(HttpMethod.Post, "regressions", body, ct);
    [HttpPost("regression-overrides")] public Task<IActionResult> Override([FromBody] object body, CancellationToken ct) => Send(HttpMethod.Post, "regression-overrides", body, ct, true);
    [HttpPut("rollout")] public Task<IActionResult> Rollout([FromBody] object body, CancellationToken ct) => Send(HttpMethod.Put, "rollout", body, ct);
    private async Task<IActionResult> Send(HttpMethod method, string suffix, object? body, CancellationToken ct, bool key = false)
    {
        var request = backend.BuildRequest(method, "/api/admin/operations/" + suffix, User.ToUserContext(), body);
        if (key && IdempotencyKey is { } idempotencyKey) request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        var (status, responseBody, etag) = await backend.SendForProxyAsync(request, "Operations backend ", ct);
        return Write(new AgentProxyResponse(status, responseBody, etag));
    }
}
