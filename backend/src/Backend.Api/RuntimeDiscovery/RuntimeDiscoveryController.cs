using Backend.Api.Common;
using Backend.Api.OrchestratorRuns;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Api.RuntimeDiscovery;

[ApiController, Route("api")]
public sealed class RuntimeDiscoveryController(RuntimeDiscoveryService service, IOrchestratorRunRepository runs) : ControllerBase
{
    [HttpGet("runtime-discovery/orchestrators")]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(new RuntimeOrchestratorListResponse(
        await service.ListAsync(Request.RequireTenant(), Request.RequireUserRole(), Request.UserGroups(), ct)));

    [HttpPost("runtime-discovery/resolve")]
    public async Task<IActionResult> Resolve(RuntimeResolveRequest request, CancellationToken ct) => Ok(
        await service.ResolveAsync(Request.RequireTenant(), Request.RequireUserId(), Request.RequireUserRole(), Request.UserGroups(), request.OrchestratorId, ct));

    [HttpPut("admin/runtime-binding")]
    public async Task<IActionResult> PutBinding(TenantRuntimeBindingUpsert request, CancellationToken ct)
    {
        RequireManage();
        return Ok(await service.PutBindingAsync(Request.RequireTenant(), request, ct));
    }

    [HttpGet("admin/runtime-binding")]
    public async Task<IActionResult> GetBinding([FromServices] IRuntimeBindingRepository bindings, CancellationToken ct)
    {
        RequireManage();
        return Ok(await bindings.GetAsync(Request.RequireTenant(), ct) ?? new TenantRuntimeBinding(false, null, null, []));
    }

    [HttpPost("chat-runs")]
    public async Task<IActionResult> Start(ChatRunStartRequest request, CancellationToken ct)
    {
        var value = await service.StartAsync(Request.RequireTenant(), Request.RequireUserId(), Request.RequireUserRole(), Request.UserGroups(), Request.UserCapabilities(), request, Request.RequireIdempotencyKey(), ct);
        Response.Headers["X-Chat-Run-Replayed"] = value.Replayed ? "true" : "false";
        return StatusCode(202, value);
    }

    [HttpGet("chat-runs/{runId:guid}")]
    public async Task<IActionResult> Get(Guid runId, CancellationToken ct)
    {
        var run = await runs.GetAsync(Request.RequireTenant(), Request.RequireUserId(), runId, ct) ?? throw new ApiException(404, "Chat root run not found");
        return Ok(new ChatRunResponse("orchestrator", run, run.CommandId));
    }

    [HttpGet("chat-runs/active")]
    public async Task<IActionResult> Active([FromQuery(Name = "conversation_id")] string? conversationId, CancellationToken ct)
    {
        var conversation = conversationId?.Trim();
        if (string.IsNullOrWhiteSpace(conversation) || conversation.Length > 128 || conversation.Any(char.IsControl)) throw new ApiException(400, "conversation_id is required");
        var active = await runs.FindActiveAsync(Request.RequireTenant(), Request.RequireUserId(), conversation, ct);
        if (active.IsAmbiguous) throw new ApiException(409, "Multiple active chat root runs require operator intervention");
        var run = active.Run ?? throw new ApiException(404, "Active chat root run not found");
        return Ok(new ChatRunResponse("orchestrator", run, run.CommandId));
    }

    [HttpPost("chat-runs/replay")]
    public async Task<IActionResult> Replay(OrchestratorRunReplayRequest request, CancellationToken ct)
    {
        request = CanonicalizeReplay(request);
        ValidateReplay(request);
        var prior = await runs.FindByIdempotencyKeyAsync(
            Request.RequireTenant(), Request.RequireUserId(), Request.RequireIdempotencyKey(), request, ct);
        if (prior.IsAmbiguous)
            throw new ApiException(409, "Multiple chat root runs share this logical attempt");
        if (prior.IsMismatch)
            throw new ApiException(409, "Chat logical attempt does not match this request");
        var run = prior.Run ?? throw new ApiException(404, "Chat logical attempt not found");
        return Ok(new ChatRunResponse("orchestrator", run, prior.CommandId ?? run.CommandId, Replayed: true));
    }

    [HttpPost("chat-runs/{runId:guid}/resume")]
    public async Task<IActionResult> Resume(Guid runId, OrchestratorRunResumeRequest request, CancellationToken ct)
    {
        var input = request.Input?.Trim();
        if (string.IsNullOrEmpty(input) || input.Length > 16_384 || input.Any(char.IsControl)) throw new ApiException(400, "input is required");
        var result = await runs.ResumeAsync(Request.RequireTenant(), Request.RequireUserId(), runId, input, Request.RequireIdempotencyKey(), ct);
        if (result.Status == OrchestratorRunWriteStatus.NotFound) throw new ApiException(404, "Chat root run not found");
        if (result.Status is OrchestratorRunWriteStatus.InvalidState or OrchestratorRunWriteStatus.Conflict) throw new ApiException(409, result.Message ?? "Chat root run state conflict");
        var run = result.Run ?? throw new InvalidOperationException("Resume returned no run");
        Response.Headers["X-Chat-Run-Replayed"] = result.Replayed ? "true" : "false";
        return StatusCode(202, new ChatRunResponse("orchestrator", run with { CommandId = result.Dispatch?.CommandId ?? run.CommandId }, result.Dispatch?.CommandId ?? run.CommandId, result.Replayed));
    }

    private void RequireManage() => Request.RequireCapability("workflow.manage");
    private static void ValidateReplay(OrchestratorRunReplayRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ConversationId) || request.ConversationId.Length > 128 || request.ConversationId.Any(char.IsControl)
            || request.Message is null || request.Message.Length > 16_384)
            throw new ApiException(400, "Invalid chat replay request");
    }
    private static OrchestratorRunReplayRequest CanonicalizeReplay(OrchestratorRunReplayRequest request) => request with
    {
        ConversationId = request.ConversationId?.Trim(),
        Message = request.Message?.Trim(),
    };
}
