using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Platform.Service.Abstractions;
using Platform.Web.Auth;

namespace Platform.Web.Controllers;

/// <summary>
/// ADMIN-only D3 direct Agent test runs. Identity always comes from the authenticated JWT;
/// request bodies carry only user input and optimistic checkpoint versions.
/// </summary>
[ApiController]
[Route("api")]
[Authorize]
[AdminOnly("權限不足，無法執行 Agent")]
public sealed class AgentRunController : ControllerBase
{
    private readonly IAgentRunService _runs;

    public AgentRunController(IAgentRunService runs) => _runs = runs;

    private string? IdempotencyKey =>
        Request.Headers.TryGetValue("Idempotency-Key", out var value)
            ? value.ToString()
            : null;

    private IActionResult Write(AgentProxyResponse response)
        => new ContentResult
        {
            StatusCode = response.Status,
            Content = response.Body,
            ContentType = "application/json; charset=utf-8",
        };

    [HttpPost("agents/{agentId:guid}/runs")]
    public async Task<IActionResult> Start(
        Guid agentId,
        [FromBody] AgentRunStartRequest request,
        CancellationToken ct)
        => Write(await _runs.StartAsync(
            agentId, request.Message, IdempotencyKey, User.ToUserContext(), ct));

    [HttpGet("runs/{runId:guid}")]
    public async Task<IActionResult> Get(Guid runId, CancellationToken ct)
        => Write(await _runs.GetAsync(runId, User.ToUserContext(), ct));

    [HttpGet("runs/{runId:guid}/events")]
    public async Task<IActionResult> Events(
        Guid runId,
        [FromQuery] long afterSequence = 0,
        [FromQuery] int limit = 100,
        CancellationToken ct = default)
        => Write(await _runs.EventsAsync(
            runId, afterSequence, limit, User.ToUserContext(), ct));

    [HttpPost("runs/{runId:guid}/resume")]
    public async Task<IActionResult> Resume(
        Guid runId,
        [FromBody] AgentRunResumePublicRequest request,
        CancellationToken ct)
        => Write(await _runs.ResumeAsync(
            runId,
            request.Input?.Message,
            request.ExpectedCheckpointVersion,
            IdempotencyKey,
            User.ToUserContext(),
            ct));

    [HttpPost("runs/{runId:guid}/cancel")]
    public async Task<IActionResult> Cancel(
        Guid runId,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] AgentRunCancelPublicRequest? request,
        CancellationToken ct)
        => Write(await _runs.CancelAsync(
            runId, request?.Reason, IdempotencyKey, User.ToUserContext(), ct));
}

public sealed record AgentRunStartRequest(
    [property: JsonPropertyName("message")] string? Message);

public sealed record AgentRunInput(
    [property: JsonPropertyName("message")] string? Message);

public sealed record AgentRunResumePublicRequest(
    [property: JsonPropertyName("input")] AgentRunInput? Input,
    [property: JsonPropertyName("expectedCheckpointVersion")] long? ExpectedCheckpointVersion);

public sealed record AgentRunCancelPublicRequest(
    [property: JsonPropertyName("reason")] string? Reason = null);
