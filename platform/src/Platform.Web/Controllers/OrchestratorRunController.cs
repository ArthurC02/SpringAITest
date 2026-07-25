using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Service.Abstractions;
using Platform.Web.Auth;

namespace Platform.Web.Controllers;

[ApiController, Route("api"), Authorize]
public sealed class OrchestratorRunController(IOrchestratorRunService runs) : ControllerBase
{
    [Authorize(Policy = "workflow.manage")]
    [HttpPost("admin/orchestrators/{id:guid}/runs")]
    public Task<IActionResult> Start(Guid id,[FromBody] StartRequest request,CancellationToken ct)=>Write(runs.StartAsync(id,request.Message,request.ConversationId,request.Context,Key,User.ToUserContext(),ct));
    [HttpGet("orchestrator-runs/{id:guid}")] public Task<IActionResult> Get(Guid id,CancellationToken ct)=>Write(runs.GetAsync(id,User.ToUserContext(),ct));
    [HttpGet("orchestrator-runs/{id:guid}/events")] public Task<IActionResult> Events(Guid id,[FromQuery]long afterSequence=0,[FromQuery]int limit=100,CancellationToken ct=default)=>Write(runs.EventsAsync(id,afterSequence,limit,User.ToUserContext(),ct));
    [HttpPost("orchestrator-runs/{id:guid}/cancel")] public Task<IActionResult> Cancel(Guid id,[FromBody] CancelRequest? request,CancellationToken ct)=>Write(runs.CancelAsync(id,request?.Reason,Key,User.ToUserContext(),ct));
    private string? Key=>Request.Headers.TryGetValue("Idempotency-Key",out var value)?value.ToString():null;
    private static async Task<IActionResult> Write(Task<AgentProxyResponse> task){var r=await task;return new ContentResult{StatusCode=r.Status,Content=r.Body,ContentType="application/json; charset=utf-8"};}
}
public sealed record StartRequest(string? Message,string? ConversationId,System.Text.Json.JsonElement? Context = null); public sealed record CancelRequest(string? Reason);
