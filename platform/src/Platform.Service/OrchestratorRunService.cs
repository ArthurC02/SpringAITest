using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Platform.Service.Abstractions;
using Platform.Service.Dtos;
using Platform.Service.Exceptions;
using Platform.Service.Options;

namespace Platform.Service;

/// <summary>D5 allocation is durable at Backend first.  Dispatch is deliberately best-effort:
/// an unavailable Workflow leaves the command reclaimable and never rolls back an accepted root run.</summary>
public sealed class OrchestratorRunService(
    BackendClient backend, HttpClient workflow, WorkflowOptions workflowOptions,
    ILogger<OrchestratorRunService> logger) : IOrchestratorRunService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task<AgentProxyResponse> StartAsync(Guid id,string? message,string? conversation,JsonElement? initialContext,string? key,UserContext ctx,CancellationToken ct=default)
    {
        // Root input is deliberately only the Backend-canonicalised message + observed_at.
        // Caller context is not a recoverable authority and must not enter root dispatch.
        _ = initialContext;
        var allocated=await Send(HttpMethod.Post,$"/api/admin/orchestrators/{id:D}/runs",ctx,new { message, conversation_id=conversation },key,ct);
        if(allocated.Status is >=200 and <300 && TryDispatch(allocated.Body,out var runId,out var commandId))
            await DispatchBestEffortAsync(runId,commandId,ctx,ct);
        return Redact(allocated);
    }
    public async Task<AgentProxyResponse> GetAsync(Guid id,UserContext ctx,CancellationToken ct=default)=>Redact(await Send(HttpMethod.Get,$"/api/orchestrator-runs/{id:D}",ctx,null,null,ct));
    public Task<AgentProxyResponse> EventsAsync(Guid id,long after,int limit,UserContext ctx,CancellationToken ct=default)=>Send(HttpMethod.Get,$"/api/orchestrator-runs/{id:D}/events?after_sequence={after.ToString(CultureInfo.InvariantCulture)}&limit={limit.ToString(CultureInfo.InvariantCulture)}",ctx,null,null,ct);
    public async Task<AgentProxyResponse> CancelAsync(Guid id,string? reason,string? key,UserContext ctx,CancellationToken ct=default)=>Redact(await Send(HttpMethod.Post,$"/api/orchestrator-runs/{id:D}/cancel",ctx,new { reason },key,ct));
    /// <summary>The durable command identity is an internal execution claim, never a public API:
    /// strip it from every public run body (D3 AgentRunService.StripInternalCommandMetadata posture).
    /// Backend returns it on start/cancel accept and on the owner-readable run row, so all three go
    /// through here; events carry a different DTO with no top-level command id.</summary>
    private static AgentProxyResponse Redact(AgentProxyResponse response)
    {
        if(response.Status is <200 or >=300)return response;
        try{ if(JsonNode.Parse(response.Body) is JsonObject run && run.Remove("command_id")) return response with { Body=run.ToJsonString(Json) }; }
        catch(JsonException){ /* a non-JSON accepted body carries no top-level command_id to strip */ }
        return response;
    }
    private async Task<AgentProxyResponse> Send(HttpMethod method,string path,UserContext ctx,object? body,string? key,CancellationToken ct)
    { using var request=backend.BuildRequest(method,path,ctx,body);if(!string.IsNullOrWhiteSpace(key))request.Headers.TryAddWithoutValidation("Idempotency-Key",key);using var response=await backend.SendAsync(request,ex=>new WorkflowInvocationException("Orchestrator run Backend unavailable",ex),ct);if((int)response.StatusCode>=500)throw new WorkflowInvocationException($"Orchestrator run Backend HTTP {(int)response.StatusCode}");return new AgentProxyResponse((int)response.StatusCode,await response.Content.ReadAsStringAsync(ct),response.Headers.ETag?.ToString()); }
    private static bool TryDispatch(string body,out Guid runId,out Guid commandId)
    { runId=default;commandId=default;try{using var json=JsonDocument.Parse(body);return json.RootElement.TryGetProperty("id",out var run)&&run.TryGetGuid(out runId)&&json.RootElement.TryGetProperty("command_id",out var command)&&command.TryGetGuid(out commandId);}catch(JsonException){return false;} }
    private async Task DispatchBestEffortAsync(Guid runId,Guid commandId,UserContext ctx,CancellationToken ct)
    {
        try
        {
            using var request=InternalRequest.Build(HttpMethod.Post,
                workflowOptions.BaseUrl.TrimEnd('/')+ $"/orchestrator-runs/{runId:D}/dispatch",
                workflowOptions.InternalToken,ctx,new { command_id=commandId.ToString("D"), context=new { } },Json);
            using var response=await InternalRequest.SendAsync(workflow,request,
                ex=>new WorkflowInvocationException("Root Workflow dispatch unavailable",ex),ct);
            if(!response.IsSuccessStatusCode){logger.LogWarning("Root Workflow dispatch failed for {RunId}: HTTP {Status}",runId,(int)response.StatusCode);return;}
        }
        catch(Exception ex) when(ex is HttpRequestException or JsonException or FormatException or WorkflowInvocationException or TaskCanceledException)
        { logger.LogWarning(ex,"Root dispatch deferred for {RunId}; durable command remains reclaimable",runId); }
    }
}
