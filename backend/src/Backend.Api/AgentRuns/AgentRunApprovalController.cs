using Backend.Api.Common;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Api.AgentRuns;

[ApiController]
[Route("api/runs/{runId:guid}/approvals")]
public sealed class AgentRunApprovalController(IAgentRunApprovalRepository approvals, IConfiguration configuration) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AgentRunApprovalPublicResponse>>> List(Guid runId, CancellationToken ct)
    { RequireEnabled(); return Ok((await approvals.ListAsync(Request.RequireTenant(), RequireUser(), Request.UserRole() ?? "", runId, ct) ?? throw new ApiException(404, "run not found")).Select(Public)); }

    [HttpPost("{approvalId:guid}/approve")]
    public Task<ActionResult<AgentRunApprovalResponse>> Approve(Guid runId, Guid approvalId, [FromBody] AgentRunApprovalDecisionRequest? request, CancellationToken ct) => Decide(runId, approvalId, true, request, ct);
    [HttpPost("{approvalId:guid}/reject")]
    public Task<ActionResult<AgentRunApprovalResponse>> Reject(Guid runId, Guid approvalId, [FromBody] AgentRunApprovalDecisionRequest? request, CancellationToken ct) => Decide(runId, approvalId, false, request, ct);
    [HttpPost("~/api/agent-runs/{runId:guid}/approvals")]
    public async Task<ActionResult<AgentRunApprovalResponse>> Create(Guid runId, [FromBody] AgentRunApprovalCreateRequest request, CancellationToken ct)
    { RequireEnabled(); return Write(await approvals.CreateAsync(Request.RequireTenant(), RequireUser(), runId, request, ct), true); }

    [HttpPost("~/api/agent-runs/{runId:guid}/approvals/{approvalId:guid}/consume")]
    public async Task<ActionResult<AgentRunApprovalConsumeResponse>> Consume(Guid runId, Guid approvalId, [FromBody] AgentRunApprovalConsumeRequest request, CancellationToken ct)
    {
        RequireEnabled();
        var result = await approvals.ConsumeAsync(Request.RequireTenant(), runId, approvalId, request, ct);
        return result.Status switch
        {
            AgentRunApprovalWriteStatus.Success when result.Response is not null => Ok(result.Response),
            AgentRunApprovalWriteStatus.Replay when result.Response is not null => Ok(result.Response),
            AgentRunApprovalWriteStatus.NotFound => throw new ApiException(404, "approval not found"),
            AgentRunApprovalWriteStatus.Expired => throw new ApiException(409, "approval expired"),
            _ => throw new ApiException(409, result.Message ?? "approval cannot be consumed"),
        };
    }

    [HttpGet("~/api/agent-runs/{runId:guid}/approvals/{approvalId:guid}/execution-identity")]
    public async Task<ActionResult<AgentRunApprovalExecutionIdentity>> ExecutionIdentity(Guid runId, Guid approvalId, CancellationToken ct)
    { RequireEnabled(); return Ok(await approvals.GetExecutionIdentityAsync(Request.RequireTenant(), RequireUser(), runId, approvalId, ct) ?? throw new ApiException(404,"approval is not executable")); }

    [HttpPost("~/api/agent-runs/{runId:guid}/write-effects/{effectId:guid}/complete")]
    public async Task<IActionResult> CompleteEffect(Guid runId, Guid effectId, [FromQuery] bool succeeded, CancellationToken ct)
    { RequireEnabled(); return await approvals.CompleteEffectAsync(Request.RequireTenant(), runId, effectId, succeeded, ct) == AgentRunApprovalWriteStatus.Success ? NoContent() : throw new ApiException(409, "write effect state changed"); }

    // D7's only shipped write tool.  This is intentionally Backend-owned: the
    // Workflow process never carries a process-local side-effect ledger.  The
    // repository writes the effect evidence and its outbox record atomically,
    // keyed by the already-server-generated effect id.
    [HttpPost("~/api/agent-runs/{runId:guid}/write-effects/{effectId:guid}/evidence")]
    public async Task<ActionResult<AgentRunWriteEvidenceResponse>> WriteEvidence(Guid runId, Guid effectId, [FromBody] AgentRunWriteEvidenceRequest request, CancellationToken ct)
    {
        RequireEnabled();
        var result = await approvals.WriteEvidenceAsync(Request.RequireTenant(), runId, effectId, request, ct);
        return result.Status switch
        {
            AgentRunApprovalWriteStatus.Success when result.Response is not null => Ok(result.Response),
            AgentRunApprovalWriteStatus.Replay when result.Response is not null => Ok(result.Response),
            AgentRunApprovalWriteStatus.NotFound => throw new ApiException(404, "write effect not found"),
            _ => throw new ApiException(409, result.Message ?? "write effect state changed"),
        };
    }

    [HttpPost("~/api/agent-run-approval-executions/recovery/claim")]
    public async Task<ActionResult<IReadOnlyList<AgentRunApprovalExecuteClaim>>> ClaimRecovery([FromQuery] int limit = 20, CancellationToken ct = default)
    { RequireEnabled(); return Ok(await approvals.ClaimExecuteRecoveryAsync(limit, ct)); }

    [HttpPost("~/api/agent-runs/{runId:guid}/approvals/{approvalId:guid}/execute/claim")]
    public async Task<ActionResult<AgentRunApprovalExecuteClaim>> ClaimExecute(Guid runId, Guid approvalId, CancellationToken ct)
    { RequireEnabled(); return Ok(await approvals.ClaimExecuteAsync(Request.RequireTenant(), runId, approvalId, ct) ?? throw new ApiException(409, "approval execution is unavailable")); }

    [HttpPost("~/api/agent-run-approval-executions/{approvalId:guid}/complete")]
    public async Task<IActionResult> CompleteRecovery(Guid approvalId, [FromBody] AgentRunApprovalExecuteCompleteRequest request, CancellationToken ct)
    {
        RequireEnabled();
        if (string.IsNullOrWhiteSpace(request.ClaimToken) || request.ClaimToken.Length > 256) throw new ApiException(400, "claim token is required");
        return await approvals.CompleteExecuteAsync(approvalId, request.ClaimToken, request.DeadLetter, ct) == AgentRunApprovalWriteStatus.Success
            ? NoContent()
            : throw new ApiException(409, "approval execution claim changed");
    }

    private async Task<ActionResult<AgentRunApprovalResponse>> Decide(Guid runId, Guid approvalId, bool approve, AgentRunApprovalDecisionRequest? request, CancellationToken ct)
    {
        RequireEnabled(); var key = Request.Headers["Idempotency-Key"].FirstOrDefault()?.Trim(); if (string.IsNullOrWhiteSpace(key) || key.Length > 128) throw new ApiException(400, "Idempotency-Key is required");
        return Write(await approvals.DecideAsync(Request.RequireTenant(), RequireUser(), Request.UserRole() ?? "", runId, approvalId, approve, key, request?.Reason?.Trim(), ct), false);
    }
    private static ActionResult<AgentRunApprovalResponse> Write(AgentRunApprovalWriteResult result, bool created) => result.Status switch
    { AgentRunApprovalWriteStatus.Success when result.Approval is not null => created ? new ObjectResult(Public(result.Approval)) { StatusCode = StatusCodes.Status202Accepted } : new OkObjectResult(Public(result.Approval)), AgentRunApprovalWriteStatus.NotFound => throw new ApiException(404, "approval not found"), AgentRunApprovalWriteStatus.Forbidden => throw new ApiException(403, "approval decision is not authorized"), AgentRunApprovalWriteStatus.Expired => throw new ApiException(409, "approval expired"), AgentRunApprovalWriteStatus.Replay => throw new ApiException(409, "approval was already decided"), _ => throw new ApiException(409, result.Message ?? "approval state changed") };
    private static AgentRunApprovalPublicResponse Public(AgentRunApprovalResponse value) => new(value.Id, value.RunId, value.Status, value.RequiredRole, value.ExpiresAt, value.SelfApprovalForbidden, value.Decision, value.DecidedAt);
    private void RequireEnabled() { if (!string.Equals(configuration["AGENT_WRITE_TOOLS_ENABLED"], "true", StringComparison.OrdinalIgnoreCase)) throw new ApiException(404, "Feature is unavailable"); }
    private string RequireUser() => Request.UserId() ?? throw new ApiException(400, "X-User-Id is required");
}
