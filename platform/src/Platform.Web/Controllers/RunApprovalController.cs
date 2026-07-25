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
public sealed class RunApprovalController(IAgentRunService runs) : ControllerBase
{
    private string? IdempotencyKey => Request.Headers.TryGetValue("Idempotency-Key", out var value)
        ? value.ToString()
        : null;

    [HttpGet]
    public Task<IActionResult> List(Guid runId, CancellationToken ct)
        => Write(runs.ApprovalsAsync(runId, User.ToUserContext(), ct));

    [HttpPost("{approvalId:guid}/approve")]
    public Task<IActionResult> Approve(
        Guid runId,
        Guid approvalId,
        [FromBody] ApprovalDecisionRequest? request,
        CancellationToken ct)
        => Write(runs.DecideApprovalAsync(
            runId, approvalId, true, request?.Reason, IdempotencyKey, User.ToUserContext(), ct));

    [HttpPost("{approvalId:guid}/reject")]
    public Task<IActionResult> Reject(
        Guid runId,
        Guid approvalId,
        [FromBody] ApprovalDecisionRequest? request,
        CancellationToken ct)
        => Write(runs.DecideApprovalAsync(
            runId, approvalId, false, request?.Reason, IdempotencyKey, User.ToUserContext(), ct));

    private static async Task<IActionResult> Write(Task<AgentProxyResponse> responseTask)
    {
        var response = await responseTask;
        return new ContentResult
        {
            StatusCode = response.Status,
            Content = response.Body,
            ContentType = "application/json; charset=utf-8",
        };
    }
}

public sealed record ApprovalDecisionRequest(
    [property: JsonPropertyName("reason")] string? Reason = null);
