using Backend.Api.Common;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Api.OrchestratorRuns;

[ApiController, Route("api")]
public sealed class OrchestratorRunController(IOrchestratorRunRepository runs) : ControllerBase
{
    [HttpPost("admin/orchestrators/{orchestratorId:guid}/runs")]
    public async Task<IActionResult> Start(Guid orchestratorId, OrchestratorRunStartRequest request, CancellationToken ct)
    {
        RequireSystemAdmin();
        var result = await runs.CreateAsync(Request.RequireTenant(), CurrentUser(), Request.UserRole()!, Request.UserGroups(), Request.UserCapabilities(), orchestratorId, Conversation(request.ConversationId), Message(request.Message), Key(), ct);
        return Accepted(result);
    }
    [HttpGet("orchestrator-runs/{runId:guid}")]
    public async Task<IActionResult> Get(Guid runId, CancellationToken ct) => Ok(await runs.GetAsync(Request.RequireTenant(), CurrentUser(), runId, ct) ?? throw Missing());
    [HttpGet("orchestrator-runs/{runId:guid}/events")]
    public async Task<IActionResult> Events(Guid runId, [FromQuery(Name = "after_sequence")] long after = 0, [FromQuery] int limit = 100, CancellationToken ct = default)
    { if (after < 0 || limit is < 1 or > 200) throw new ApiException(400, "after_sequence/limit is invalid"); return Ok(await runs.EventsAsync(Request.RequireTenant(), CurrentUser(), runId, after, limit, ct) ?? throw Missing()); }
    [HttpPost("orchestrator-runs/{runId:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid runId, OrchestratorRunCancelRequest? request, CancellationToken ct)
    { var result = await runs.CancelAsync(Request.RequireTenant(), CurrentUser(), runId, request?.Reason?.Trim(), Key(), ct); return Accepted(result); }
    [HttpGet("orchestrator-runs/{runId:guid}/execution-artifact")]
    public async Task<IActionResult> Artifact(Guid runId, CancellationToken ct) => Content(await runs.ExecutionArtifactAsync(Request.RequireTenant(), CurrentUser(), runId, ct) ?? throw Missing(), "application/json; charset=utf-8");
    [HttpPost("orchestrator-runs/{runId:guid}/commands/{commandId:guid}/claim")]
    public async Task<IActionResult> Claim(Guid runId, Guid commandId, OrchestratorRunCommandClaimRequest request, CancellationToken ct)
    { var claim = await runs.ClaimCommandAsync(Request.RequireTenant(), CurrentUser(), runId, commandId, request.WorkerId?.Trim() ?? "", request.LeaseSeconds, ct); return claim is null ? NoContent() : Ok(claim); }
    [HttpPost("orchestrator-runs/{runId:guid}/commands/{commandId:guid}/renew")]
    [HttpPost("orchestrator-runs/{runId:guid}/commands/{commandId:guid}/lease/renew")]
    public async Task<IActionResult> Renew(Guid runId, Guid commandId, OrchestratorRunCommandRenewRequest request, CancellationToken ct)
    { var claim = await runs.RenewCommandAsync(Request.RequireTenant(), CurrentUser(), runId, commandId, request.ClaimToken ?? "", request.LeaseGeneration, request.LeaseSeconds, ct); return claim is null ? NoContent() : Ok(claim); }
    [HttpPost("orchestrator-runs/{runId:guid}/commands/{commandId:guid}/dispatch/complete")]
    public async Task<IActionResult> Complete(Guid runId, Guid commandId, OrchestratorRunDispatchCompleteRequest request, CancellationToken ct)
    { var result = await runs.CompleteDispatchAsync(Request.RequireTenant(), CurrentUser(), runId, commandId, request.ClaimToken ?? "", ct); return result switch { OrchestratorRunDispatchCompleteStatus.Success => NoContent(), OrchestratorRunDispatchCompleteStatus.NotFound => throw Missing(), _ => throw new ApiException(409, "Orchestrator command claim conflicted") }; }
    [HttpPost("orchestrator-runs/recovery/claim")]
    public async Task<IActionResult> Recover(OrchestratorRunRecoveryClaimRequest request, CancellationToken ct)
        => Ok(await runs.ClaimRecoveryAsync(request.WorkerId?.Trim() ?? "", request.Limit, request.LeaseSeconds, ct));
    [HttpPost("orchestrator-runs/{runId:guid}/children")]
    public async Task<IActionResult> CreateChild(Guid runId, OrchestratorChildCreateRequest request, CancellationToken ct)
        => Ok(await runs.CreateChildAsync(Request.RequireTenant(), CurrentUser(), runId, request, ct) ?? throw new ApiException(409, "Child task is not authorized by the immutable root snapshot"));
    [HttpGet("orchestrator-runs/{runId:guid}/children/{childId:guid}")]
    public async Task<IActionResult> ChildStatus(Guid runId, Guid childId, CancellationToken ct)
        => Ok(await runs.GetChildAsync(Request.RequireTenant(), CurrentUser(), runId, childId, ct) ?? throw Missing());
    [HttpPost("orchestrator-runs/{runId:guid}/transitions")]
    public async Task<IActionResult> Transition(Guid runId, OrchestratorRootTransitionRequest request, CancellationToken ct)
        => Accepted(await runs.TransitionAsync(Request.RequireTenant(), CurrentUser(), runId, request, ct));
    [HttpPost("orchestrator-runs/{runId:guid}/context/acquire")]
    public async Task<IActionResult> AcquireContext(Guid runId, OrchestratorContextAcquireRequest request, CancellationToken ct)
        => Ok(await runs.AcquireContextAsync(Request.RequireTenant(), CurrentUser(), runId, request, ct) ?? throw new ApiException(409, "Context acquisition authority is invalid"));
    private IActionResult Accepted(OrchestratorRunWriteResult r) { if (r.Status == OrchestratorRunWriteStatus.NotFound) throw Missing(); if (r.Status is OrchestratorRunWriteStatus.Conflict or OrchestratorRunWriteStatus.InvalidState) throw new ApiException(409, r.Message ?? "Orchestrator run state conflict"); Response.Headers["X-Orchestrator-Run-Replayed"] = r.Replayed ? "true" : "false"; if (r.Dispatch is not null) Response.Headers["X-Orchestrator-Run-Command-Id"] = r.Dispatch.CommandId.ToString("D"); return StatusCode(202, r.Run! with { CommandId = r.Dispatch?.CommandId }); }
    private void RequireSystemAdmin() { if (!Request.HasCapability("workflow.manage")) throw new ApiException(403, "workflow.manage capability is required"); }
    private string CurrentUser() => Request.UserId() ?? throw new ApiException(400, "X-User-Id is required");
    private string Key() { var x = Request.Headers["Idempotency-Key"].ToString().Trim(); if (x.Length is < 1 or > 128 || x.Any(char.IsControl)) throw new ApiException(400, "Idempotency-Key is required"); return x; }
    private static string Message(string? x) { x = x?.Trim(); if (string.IsNullOrEmpty(x) || x.Length > 16384) throw new ApiException(400, "message is required"); return x; }
    private static string Conversation(string? x) { x = x?.Trim(); if (string.IsNullOrEmpty(x) || x.Length > 128 || x.Any(char.IsControl)) throw new ApiException(400, "conversation_id is required"); return x; }
    private static ApiException Missing() => new(404, "Orchestrator run not found");
}
