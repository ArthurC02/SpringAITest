using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Service.Abstractions;
using Platform.Web.Auth;

namespace Platform.Web.Controllers;

/// <summary>
/// D7 runtime approval surface.  It intentionally requires an authenticated person, not
/// ADMIN or workflow.manage: the durable Backend decision enforces tenant, required role,
/// expiry, action fingerprint and separation of duties again at the point of decision.
/// </summary>
[ApiController]
[Route("api/runs/{runId:guid}/approvals")]
[Authorize]
public sealed class RunApprovalController(IAgentRunService runs) : ProxyControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(Guid runId, CancellationToken ct)
        => Write(await runs.ApprovalsAsync(runId, User.ToUserContext(), ct));

    /// <summary>
    /// O3 discoverable approval queue. A transparent proxy like <see cref="List"/> — Backend owns
    /// both the "visible"/"actionable" predicates and the keyset cursor; Platform only forwards
    /// identity and the query string verbatim.
    /// </summary>
    [HttpGet("~/api/runs/approvals")]
    public async Task<IActionResult> Queue(
        [FromQuery] string scope = "visible",
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 20,
        CancellationToken ct = default)
        => Write(await runs.QueueAsync(scope, cursor, limit, User.ToUserContext(), ct));

    [HttpPost("{approvalId:guid}/approve")]
    public async Task<IActionResult> Approve(
        Guid runId,
        Guid approvalId,
        [FromBody] ApprovalDecisionRequest? request,
        CancellationToken ct)
        => Write(await runs.DecideApprovalAsync(
            runId, approvalId, true, request?.Reason, IdempotencyKey, User.ToUserContext(), ct));

    [HttpPost("{approvalId:guid}/reject")]
    public async Task<IActionResult> Reject(
        Guid runId,
        Guid approvalId,
        [FromBody] ApprovalDecisionRequest? request,
        CancellationToken ct)
        => Write(await runs.DecideApprovalAsync(
            runId, approvalId, false, request?.Reason, IdempotencyKey, User.ToUserContext(), ct));
}

public sealed record ApprovalDecisionRequest(
    [property: JsonPropertyName("reason")] string? Reason = null);
