using Backend.Api.Common;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Api.AgentRuns;

/// <summary>
/// Workflow-only global recovery queue. Backend's global internal-token middleware is the trust
/// boundary; unlike caller-owned run APIs this scanner intentionally spans tenants and therefore
/// accepts no caller identity from the body.
/// </summary>
[ApiController]
[Route("api/agent-runs/recovery")]
public sealed class AgentRunRecoveryController : ControllerBase
{
    private readonly IAgentRunRepository _runs;

    public AgentRunRecoveryController(IAgentRunRepository runs) => _runs = runs;

    [HttpPost("claim")]
    public Task<AgentRunRecoveryClaimResponse> Claim(
        [FromBody] AgentRunRecoveryClaimRequest request,
        CancellationToken ct)
    {
        var workerId = request.WorkerId?.Trim();
        if (string.IsNullOrEmpty(workerId) || workerId.Length > 200
            || request.Limit is < 1 or > 100
            || request.LeaseSeconds is < 5 or > 300)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "worker_id、limit 或 lease_seconds 無效");
        }

        return _runs.ClaimRecoveryAsync(
            request with { WorkerId = workerId },
            ct);
    }
}
