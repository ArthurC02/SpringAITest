using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Service;
using Platform.Web.Auth;

namespace Platform.Web.Controllers;

/// <summary>
/// O5 one-shot durable triggers (04-operations-trigger-plan.md §6). Gated by
/// <c>AGENT_TRIGGERS_ENABLED</c> before authentication (see Program.cs) and, per §8's
/// "tenant + exact capability" rollout, the exact <c>workflow.manage</c> capability D4/D5/O2's
/// admin surfaces already require — ADMIN alone never grants access.
///
/// A transparent proxy like <see cref="OperationsGovernanceController"/>: Backend owns the schedule,
/// the pinned target, the immutable principal snapshot, the fire ledger and the ETag. Platform adds
/// no validation of its own — a second copy of the schedule rules here would only be a place for
/// the two sides to drift.
/// </summary>
[ApiController, Route("api/admin/triggers"), Authorize(Policy = "workflow.manage")]
public sealed class TriggerController(BackendClient backend) : ProxyControllerBase
{
    private const string BasePath = "/api/admin/triggers";

    [HttpPost]
    public Task<IActionResult> Create([FromBody] object body, CancellationToken ct)
        => Send(HttpMethod.Post, string.Empty, body, ct);

    [HttpGet]
    public Task<IActionResult> List(CancellationToken ct)
        => Send(HttpMethod.Get, string.Empty, null, ct);

    [HttpGet("{id:guid}")]
    public Task<IActionResult> Get(Guid id, CancellationToken ct)
        => Send(HttpMethod.Get, $"/{id:D}", null, ct);

    /// <summary>Optimistic lock is Backend's: <c>If-Match</c> is forwarded verbatim (missing → 428, stale → 409).</summary>
    [HttpPost("{id:guid}/cancel")]
    public Task<IActionResult> Cancel(Guid id, CancellationToken ct)
        => Send(HttpMethod.Post, $"/{id:D}/cancel", null, ct, ifMatch: true);

    /// <summary>Fire history. The keyset cursor and page bounds are Backend's, so the query string rides through untouched.</summary>
    [HttpGet("{id:guid}/occurrences")]
    public Task<IActionResult> Occurrences(Guid id, CancellationToken ct)
        => Send(HttpMethod.Get, $"/{id:D}/occurrences" + (Request.QueryString.Value ?? string.Empty), null, ct);

    private async Task<IActionResult> Send(
        HttpMethod method, string suffix, object? body, CancellationToken ct, bool ifMatch = false)
    {
        var response = await backend.SendForAgentProxyAsync(
            method,
            BasePath + suffix,
            User.ToUserContext(),
            body,
            "Trigger backend ",
            ifMatch && IfMatch is { } precondition ? ("If-Match", precondition) : null,
            ct);
        return Write(response);
    }
}
