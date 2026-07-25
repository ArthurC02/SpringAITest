using Backend.Api.Common;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Api.AgentRuns;

/// <summary>
/// D3 published Agent direct-test run API。Backend 仍是 internal-only；Platform/Workflow 轉發的
/// tenant/user/role headers 是唯一身分來源。所有讀寫同時以 tenant + owner 過濾，錯誤不洩漏存在性。
/// </summary>
[ApiController]
[Route("api")]
[AdminOnly("權限不足，無法存取 Agent run")]
public sealed class AgentRunController : ControllerBase
{
    private const int MaxMessageLength = 16_384;
    private const int MaxIdempotencyKeyLength = 128;
    private readonly IAgentRunRepository _runs;

    public AgentRunController(IAgentRunRepository runs) => _runs = runs;

    [HttpPost("agents/{agentId:guid}/runs")]
    public async Task<ActionResult<AgentRunResponse>> Start(
        Guid agentId, [FromBody] DirectAgentRunStartRequest request, CancellationToken ct)
    {
        var result = await _runs.CreateDirectAsync(
            Request.RequireTenant(),
            RequireUser(),
            Request.UserRole()!,
            Request.UserGroups(),
            Request.UserCapabilities(),
            agentId,
            RequireMessage(request.Message),
            RequireIdempotencyKey(),
            ct);
        return AcceptedResult(result);
    }

    [HttpGet("runs/{runId:guid}")]
    public async Task<ActionResult<AgentRunResponse>> Get(Guid runId, CancellationToken ct)
        => Ok(await _runs.GetAsync(Request.RequireTenant(), RequireUser(), runId, ct)
              ?? throw RunNotFound());

    [HttpGet("runs/{runId:guid}/events")]
    public async Task<ActionResult<AgentRunEventsResponse>> Events(
        Guid runId,
        [FromQuery(Name = "after_sequence")] long afterSequence = 0,
        [FromQuery] int limit = 100,
        CancellationToken ct = default)
    {
        if (afterSequence < 0 || limit is < 1 or > 200)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "after_sequence 必須大於等於 0，limit 必須介於 1 到 200");
        }

        return Ok(await _runs.GetEventsAsync(
                      Request.RequireTenant(), RequireUser(), runId, afterSequence, limit, ct)
                  ?? throw RunNotFound());
    }

    [HttpPost("runs/{runId:guid}/resume")]
    public async Task<ActionResult<AgentRunResponse>> Resume(
        Guid runId, [FromBody] AgentRunResumeRequest request, CancellationToken ct)
    {
        if (request.ExpectedCheckpointVersion is not long checkpointVersion || checkpointVersion < 0)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "resume 必須帶 expected_checkpoint_version");
        }

        var result = await _runs.ResumeAsync(
            Request.RequireTenant(),
            RequireUser(),
            runId,
            RequireMessage(request.Message),
            checkpointVersion,
            RequireIdempotencyKey(),
            ct);
        return AcceptedResult(result);
    }

    [HttpPost("runs/{runId:guid}/cancel")]
    public async Task<ActionResult<AgentRunResponse>> Cancel(
        Guid runId, [FromBody] AgentRunCancelRequest? request, CancellationToken ct)
    {
        var reason = request?.Reason?.Trim();
        if (reason?.Length > 500)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "cancel reason 過長");
        }

        var result = await _runs.CancelAsync(
            Request.RequireTenant(),
            RequireUser(),
            runId,
            reason,
            RequireIdempotencyKey(),
            ct);
        return AcceptedResult(result);
    }

    /// <summary>Workflow-only sensitive snapshot；Platform 不得公開代理。</summary>
    [HttpGet("agent-runs/{runId:guid}/execution-artifact")]
    public async Task<IActionResult> ExecutionArtifact(Guid runId, CancellationToken ct)
    {
        var json = await _runs.GetExecutionArtifactAsync(
            Request.RequireTenant(), RequireUser(), runId, ct) ?? throw RunNotFound();
        return Content(json, "application/json; charset=utf-8");
    }

    [HttpPost("agent-runs/{runId:guid}/transitions")]
    public async Task<ActionResult<AgentRunResponse>> Transition(
        Guid runId, [FromBody] AgentRunTransitionRequest request, CancellationToken ct)
        => WriteResult(await _runs.TransitionAsync(
            Request.RequireTenant(), RequireUser(), runId, request, ct));

    [HttpPost("agent-runs/{runId:guid}/events")]
    public async Task<ActionResult<AgentRunResponse>> AppendEvents(
        Guid runId, [FromBody] AgentRunEventsAppendRequest request, CancellationToken ct)
        => WriteResult(await _runs.AppendEventsAsync(
            Request.RequireTenant(), RequireUser(), runId, request, ct));

    [HttpPost("agent-runs/{runId:guid}/lease")]
    public async Task<ActionResult<AgentRunLeaseResponse>> ClaimLease(
        Guid runId, [FromBody] AgentRunLeaseRequest request, CancellationToken ct)
    {
        var result = await _runs.ClaimLeaseAsync(
            Request.RequireTenant(), RequireUser(), runId, request, ct);
        return result.Status switch
        {
            AgentRunWriteStatus.NotFound => throw RunNotFound(),
            AgentRunWriteStatus.Conflict => throw Conflict(result.Message),
            AgentRunWriteStatus.InvalidState => throw Conflict(result.Message),
            _ => Ok(result.Lease!),
        };
    }

    [HttpPost("agent-runs/{runId:guid}/commands/{commandId:guid}/dispatch/complete")]
    public async Task<IActionResult> CompleteDispatch(
        Guid runId,
        Guid commandId,
        [FromBody] AgentRunDispatchCompleteRequest request,
        CancellationToken ct)
    {
        var token = request.ClaimToken?.Trim();
        if (string.IsNullOrEmpty(token) || token.Length > 256)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "claim_token 無效");
        }

        return await _runs.CompleteDispatchAsync(
            Request.RequireTenant(),
            RequireUser(),
            runId,
            commandId,
            token,
            ct) switch
        {
            AgentRunDispatchCompleteStatus.Success => NoContent(),
            AgentRunDispatchCompleteStatus.NotFound => throw RunNotFound(),
            _ => throw Conflict("command dispatch claim 無效"),
        };
    }

    [HttpPost("agent-runs/{runId:guid}/commands/{commandId:guid}/claim")]
    public async Task<ActionResult<AgentRunRecoveryItem>> ClaimCommand(
        Guid runId,
        Guid commandId,
        [FromBody] AgentRunCommandClaimRequest request,
        CancellationToken ct)
    {
        var workerId = request.WorkerId?.Trim();
        if (string.IsNullOrEmpty(workerId)
            || workerId.Length > 200
            || request.LeaseSeconds is < 5 or > 300)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "worker_id or lease_seconds is invalid");
        }

        var result = await _runs.ClaimCommandAsync(
            Request.RequireTenant(),
            RequireUser(),
            runId,
            commandId,
            request with { WorkerId = workerId },
            ct);
        return result.Status switch
        {
            AgentRunWriteStatus.NotFound => throw RunNotFound(),
            AgentRunWriteStatus.Conflict => throw Conflict(result.Message),
            AgentRunWriteStatus.InvalidState => throw Conflict(result.Message),
            AgentRunWriteStatus.Replay => NoContent(),
            _ => Ok(result.Item!),
        };
    }

    private ActionResult<AgentRunResponse> AcceptedResult(AgentRunWriteResult result)
    {
        var response = ResultOrThrow(result) with
        {
            CommandId = result.Dispatch?.CommandId,
        };
        Response.Headers["X-Agent-Run-Replayed"] =
            result.Replayed ? "true" : "false";
        Response.Headers["X-Agent-Run-Dispatch-Required"] =
            result.Dispatch is null ? "false" : "true";
        if (result.Dispatch is { } dispatch)
        {
            Response.Headers["X-Agent-Run-Command-Id"] = dispatch.CommandId.ToString("D");
            Response.Headers["X-Agent-Run-Dispatch-Claim"] = dispatch.ClaimToken;
            Response.Headers["X-Agent-Run-Dispatch-Claim-Expires-At"] =
                dispatch.ClaimExpiresAt.ToString("O");
            Response.Headers["X-Agent-Run-Dispatch-Attempt"] =
                dispatch.DispatchAttempt.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        Response.Headers.Location = $"/api/runs/{response.Id:D}";
        return StatusCode(StatusCodes.Status202Accepted, response);
    }

    private ActionResult<AgentRunResponse> WriteResult(AgentRunWriteResult result)
        => Ok(ResultOrThrow(result));

    private static AgentRunResponse ResultOrThrow(AgentRunWriteResult result)
        => result.Status switch
        {
            AgentRunWriteStatus.NotFound => throw RunNotFound(),
            AgentRunWriteStatus.Conflict => throw Conflict(result.Message),
            AgentRunWriteStatus.InvalidState => throw Conflict(result.Message),
            _ => result.Run!,
        };

    private string RequireIdempotencyKey()
    {
        var key = Request.Headers["Idempotency-Key"].ToString().Trim();
        if (key.Length is < 1 or > MaxIdempotencyKeyLength || key.Any(char.IsControl))
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                $"Idempotency-Key 必須為 1 到 {MaxIdempotencyKeyLength} 個可見字元");
        }
        return key;
    }

    private string RequireUser()
        => Request.UserId() is { } user ? user : throw new ApiException(
            StatusCodes.Status400BadRequest, "缺少使用者識別標頭：X-User-Id");

    private static string RequireMessage(string? message)
    {
        var normalized = message?.Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "message 不可為空");
        }
        if (normalized.Length > MaxMessageLength)
        {
            throw new ApiException(StatusCodes.Status413PayloadTooLarge, "message 過長");
        }
        return normalized;
    }

    private static ApiException RunNotFound()
        => new(StatusCodes.Status404NotFound, "找不到 Agent run");

    private static ApiException Conflict(string? message)
        => new(StatusCodes.Status409Conflict, message ?? "Agent run 狀態衝突");
}
