using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Service.Abstractions;
using Platform.Web.Auth;

namespace Platform.Web.Controllers;

/// <summary>
/// D5 Root Orchestrator runs。啟動需精確 <c>workflow.manage</c> 能力(比照 Designer 管理面);
/// 讀取/取消只要求已認證,授權由 Backend 依 run 擁有者判定。
/// </summary>
[ApiController]
[Route("api")]
[Authorize]
public sealed class OrchestratorRunController(IOrchestratorRunService runs) : ProxyControllerBase
{
    [Authorize(Policy = "workflow.manage")]
    [HttpPost("admin/orchestrators/{id:guid}/runs")]
    public async Task<IActionResult> Start(
        Guid id,
        [FromBody] OrchestratorRunStartRequest request,
        CancellationToken ct)
        => Write(await runs.StartAsync(
            id, request.Message, request.ConversationId, IdempotencyKey, User.ToUserContext(), ct));

    [HttpGet("orchestrator-runs/{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        => Write(await runs.GetAsync(id, User.ToUserContext(), ct));

    [HttpGet("orchestrator-runs/{id:guid}/events")]
    public async Task<IActionResult> Events(
        Guid id,
        [FromQuery] long afterSequence = 0,
        [FromQuery] int limit = 100,
        CancellationToken ct = default)
        => Write(await runs.EventsAsync(id, afterSequence, limit, User.ToUserContext(), ct));

    [HttpPost("orchestrator-runs/{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(
        Guid id,
        [FromBody] OrchestratorRunCancelRequest? request,
        CancellationToken ct)
        => Write(await runs.CancelAsync(
            id, request?.Reason, IdempotencyKey, User.ToUserContext(), ct));
}

public sealed record OrchestratorRunStartRequest(string? Message, string? ConversationId);

public sealed record OrchestratorRunCancelRequest(string? Reason);
