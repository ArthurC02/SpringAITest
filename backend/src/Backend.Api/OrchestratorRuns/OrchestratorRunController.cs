using Backend.Api.Common;
using Backend.Api.Contexts;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Api.OrchestratorRuns;

[ApiController, Route("api")]
public sealed class OrchestratorRunController(IOrchestratorRunRepository runs) : ControllerBase
{
    [HttpPost("admin/orchestrators/{orchestratorId:guid}/runs")]
    public async Task<IActionResult> Start(Guid orchestratorId, OrchestratorRunStartRequest request, CancellationToken ct)
    {
        Request.RequireCapability("workflow.manage");
        var result = await runs.CreateAsync(Request.RequireTenant(), Request.RequireUserId(), Request.RequireUserRole(), Request.UserGroups(), Request.UserCapabilities(), orchestratorId, Conversation(request.ConversationId), Message(request.Message), Key(), ct);
        return Accepted(result);
    }
    [HttpGet("orchestrator-runs/{runId:guid}")]
    public async Task<IActionResult> Get(Guid runId, CancellationToken ct) => Ok(await runs.GetAsync(Request.RequireTenant(), Request.RequireUserId(), runId, ct) ?? throw Missing());
    [HttpGet("orchestrator-runs/{runId:guid}/events")]
    public async Task<IActionResult> Events(Guid runId, [FromQuery(Name = "after_sequence")] long after = 0, [FromQuery] int limit = 100, CancellationToken ct = default)
    { if (after < 0 || limit is < 1 or > 200) throw new ApiException(400, "after_sequence/limit is invalid"); return Ok(await runs.EventsAsync(Request.RequireTenant(), Request.RequireUserId(), runId, after, limit, ct) ?? throw Missing()); }
    [HttpPost("orchestrator-runs/{runId:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid runId, OrchestratorRunCancelRequest? request, CancellationToken ct)
    { var result = await runs.CancelAsync(Request.RequireTenant(), Request.RequireUserId(), runId, request?.Reason?.Trim(), Key(), ct); return Accepted(result); }
    [HttpGet("orchestrator-runs/{runId:guid}/execution-artifact")]
    public async Task<IActionResult> Artifact(Guid runId, CancellationToken ct) => Content(await runs.ExecutionArtifactAsync(Request.RequireTenant(), Request.RequireUserId(), runId, ct) ?? throw Missing(), "application/json; charset=utf-8");
    [HttpPost("orchestrator-runs/{runId:guid}/commands/{commandId:guid}/claim")]
    public async Task<IActionResult> Claim(Guid runId, Guid commandId, OrchestratorRunCommandClaimRequest request, CancellationToken ct)
    { var claim = await runs.ClaimCommandAsync(Request.RequireTenant(), Request.RequireUserId(), runId, commandId, request.WorkerId?.Trim() ?? "", request.LeaseSeconds, ct); return claim is null ? NoContent() : Ok(claim); }
    [HttpPost("orchestrator-runs/{runId:guid}/commands/{commandId:guid}/lease/renew")]
    public async Task<IActionResult> Renew(Guid runId, Guid commandId, OrchestratorRunCommandRenewRequest request, CancellationToken ct)
    { var claim = await runs.RenewCommandAsync(Request.RequireTenant(), Request.RequireUserId(), runId, commandId, request.ClaimToken ?? "", request.LeaseGeneration, request.LeaseSeconds, ct); return claim is null ? NoContent() : Ok(claim); }
    [HttpPost("orchestrator-runs/{runId:guid}/commands/{commandId:guid}/dispatch/complete")]
    public async Task<IActionResult> Complete(Guid runId, Guid commandId, OrchestratorRunDispatchCompleteRequest request, CancellationToken ct)
    { var result = await runs.CompleteDispatchAsync(Request.RequireTenant(), Request.RequireUserId(), runId, commandId, request.ClaimToken ?? "", ct); return result switch { OrchestratorRunDispatchCompleteStatus.Success => NoContent(), OrchestratorRunDispatchCompleteStatus.NotFound => throw Missing(), _ => throw new ApiException(409, "Orchestrator command claim conflicted") }; }
    [HttpPost("orchestrator-runs/recovery/claim")]
    public async Task<IActionResult> Recover(OrchestratorRunRecoveryClaimRequest request, CancellationToken ct)
    { var workerId = Worker(request.WorkerId, request.Limit, request.LeaseSeconds); return Ok(await runs.ClaimRecoveryAsync(workerId, request.Limit, request.LeaseSeconds, ct)); }
    [HttpPost("orchestrator-runs/{runId:guid}/children")]
    public async Task<IActionResult> CreateChild(Guid runId, OrchestratorChildCreateRequest request, CancellationToken ct)
    {
        Child(request);
        // The server-owned Context projection re-validates the envelope it produced (oversized view,
        // missing role view, mismatched context_ref).  That is a permanent 400 like the caller-side
        // check above — never a 500 Workflow would treat as retryable.
        try { return Ok(await runs.CreateChildAsync(Request.RequireTenant(), Request.RequireUserId(), runId, request, ct) ?? throw new ApiException(409, "Child task is not authorized by the immutable root snapshot")); }
        catch (ArgumentException e) { throw new ApiException(400, e.Message); }
    }
    [HttpGet("orchestrator-runs/{runId:guid}/children/{childId:guid}")]
    public async Task<IActionResult> ChildStatus(Guid runId, Guid childId, CancellationToken ct)
        => Ok(await runs.GetChildAsync(Request.RequireTenant(), Request.RequireUserId(), runId, childId, ct) ?? throw Missing());
    [HttpPost("orchestrator-runs/{runId:guid}/transitions")]
    public async Task<IActionResult> Transition(Guid runId, OrchestratorRootTransitionRequest request, CancellationToken ct)
        => Accepted(await runs.TransitionAsync(Request.RequireTenant(), Request.RequireUserId(), runId, request, ct));
    [HttpPost("orchestrator-runs/{runId:guid}/context/acquire")]
    public async Task<IActionResult> AcquireContext(Guid runId, OrchestratorContextAcquireRequest request, CancellationToken ct)
        => Ok(await runs.AcquireContextAsync(Request.RequireTenant(), Request.RequireUserId(), runId, request, ct) ?? throw new ApiException(409, "Context acquisition authority is invalid"));
    [HttpPost("orchestrator-runs/{runId:guid}/children/{childId:guid}/context-requests")]
    public async Task<IActionResult> CreateContextRequest(Guid runId, Guid childId, CancellationToken ct)
    {
        var result = await runs.GetOrCreateContextRequestAsync(Request.RequireTenant(), Request.RequireUserId(), runId, childId, ct) ?? throw Missing();
        Response.SetVersionETag(result.Version);
        return Ok(result);
    }
    [HttpGet("orchestrator-runs/{runId:guid}/children/{childId:guid}/context-requests/{requestId:guid}")]
    public async Task<IActionResult> GetContextRequest(Guid runId, Guid childId, Guid requestId, CancellationToken ct)
    {
        var result = await runs.GetContextRequestAsync(Request.RequireTenant(), Request.RequireUserId(), runId, childId, requestId, ct) ?? throw Missing();
        Response.SetVersionETag(result.Version);
        return Ok(result);
    }
    [HttpPost("orchestrator-runs/{runId:guid}/children/{childId:guid}/context-requests/{requestId:guid}/deltas")]
    public async Task<IActionResult> AppendContextDelta(Guid runId, Guid childId, Guid requestId, OrchestratorContextDeltaRequest request, CancellationToken ct)
    {
        try
        {
            var result = await runs.AppendContextDeltaAsync(Request.RequireTenant(), Request.RequireUserId(), runId, childId, requestId, Request.RequireIfMatchVersion(), request, ct);
            if (result.Status == OrchestratorContextDeltaStatus.NotFound) throw Missing();
            if (result.Status == OrchestratorContextDeltaStatus.Conflict)
                throw new ApiException(409, "Context request version conflicted")
                { FieldErrors = new Dictionary<string, string> { ["If-Match"] = "Context request version is stale" } };
            Response.SetVersionETag(result.Version);
            return Ok(result.Revision);
        }
        catch (ContextPolicyUnavailableException) { throw new ApiException(503, "No active context policy is available"); }
        catch (ContextPolicyInvalidException e) { throw new ApiException(422, e.Message); }
        catch (ArgumentException e) { throw new ApiException(400, e.Message); }
    }
    private IActionResult Accepted(OrchestratorRunWriteResult r) { if (r.Status == OrchestratorRunWriteStatus.NotFound) throw Missing(); if (r.Status is OrchestratorRunWriteStatus.Conflict or OrchestratorRunWriteStatus.InvalidState) throw new ApiException(409, r.Message ?? "Orchestrator run state conflict"); Response.Headers["X-Orchestrator-Run-Replayed"] = r.Replayed ? "true" : "false"; if (r.Dispatch is not null) Response.Headers["X-Orchestrator-Run-Command-Id"] = r.Dispatch.CommandId.ToString("D"); return StatusCode(202, r.Run! with { CommandId = r.Dispatch?.CommandId }); }
    // The recovery repositories throw on these bounds; validate them here so an invalid
    // Workflow scanner request is a 400 ApiError instead of a 500.
    private static string Worker(string? workerId, int limit, int leaseSeconds)
    { var x = workerId?.Trim(); if (string.IsNullOrEmpty(x) || x.Length > 256 || limit is < 1 or > 100 || leaseSeconds is < 1 or > 300) throw new ApiException(400, "worker_id、limit 或 lease_seconds 無效"); return x; }
    // Both repositories throw on a malformed child request; a 500 would look retryable to Workflow
    // while a 409 would be retried forever. Surface the same fixed messages as a permanent 400.
    private static void Child(OrchestratorChildCreateRequest request)
    { try { OrchestratorTaskEnvelope.ValidateChild(request); } catch (ArgumentException e) { throw new ApiException(400, e.Message); } }
    private string Key() => Request.RequireIdempotencyKey();
    private static string Message(string? x) { x = x?.Trim(); if (string.IsNullOrEmpty(x)) throw new ApiException(400, "message is required"); if (x.Length > 16384) throw new ApiException(413, "message is too large"); return x; }
    private static string Conversation(string? x) { x = x?.Trim(); if (string.IsNullOrEmpty(x) || x.Length > 128 || x.Any(char.IsControl)) throw new ApiException(400, "conversation_id is required"); return x; }
    private static ApiException Missing() => new(404, "Orchestrator run not found");
}
