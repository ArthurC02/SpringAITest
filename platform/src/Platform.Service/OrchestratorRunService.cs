using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Platform.Service.Abstractions;
using Platform.Service.Dtos;
using Platform.Service.Exceptions;
using Platform.Service.Options;

namespace Platform.Service;

/// <summary>D5 allocation is durable at Backend first.  Dispatch is deliberately best-effort:
/// an unavailable Workflow leaves the command reclaimable and never rolls back an accepted root run.</summary>
public sealed class OrchestratorRunService(
    BackendClient backend,
    HttpClient workflow,
    WorkflowOptions workflowOptions,
    ILogger<OrchestratorRunService> logger) : IOrchestratorRunService
{
    private const string FailurePrefix = "Orchestrator run ";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Root input is deliberately only the Backend-canonicalised message + observed_at:
    /// caller context is not a recoverable authority and never enters root dispatch, so the public
    /// start body carries no context field at all.</summary>
    public async Task<AgentProxyResponse> StartAsync(
        Guid id,
        string? message,
        string? conversation,
        string? key,
        UserContext ctx,
        CancellationToken ct = default)
    {
        var allocated = await Send(
            HttpMethod.Post,
            $"/api/admin/orchestrators/{id:D}/runs",
            ctx,
            new { message, conversation_id = conversation },
            key,
            ct);
        if (allocated.Status is >= 200 and < 300)
        {
            var (runId, commandId) = RequiredDispatchIds(allocated.Body);
            await DispatchBestEffortAsync(runId, commandId, ctx, ct);
        }

        return Redact(allocated);
    }

    public async Task<AgentProxyResponse> GetAsync(
        Guid id,
        UserContext ctx,
        CancellationToken ct = default)
        => Redact(await Send(HttpMethod.Get, $"/api/orchestrator-runs/{id:D}", ctx, null, null, ct));

    public Task<AgentProxyResponse> EventsAsync(
        Guid id,
        long after,
        int limit,
        UserContext ctx,
        CancellationToken ct = default)
        => Send(
            HttpMethod.Get,
            $"/api/orchestrator-runs/{id:D}/events?after_sequence={after.ToString(CultureInfo.InvariantCulture)}&limit={limit.ToString(CultureInfo.InvariantCulture)}",
            ctx,
            null,
            null,
            ct);

    public async Task<AgentProxyResponse> CancelAsync(
        Guid id,
        string? reason,
        string? key,
        UserContext ctx,
        CancellationToken ct = default)
        => Redact(await Send(
            HttpMethod.Post,
            $"/api/orchestrator-runs/{id:D}/cancel",
            ctx,
            new { reason },
            key,
            ct));

    /// <summary>The durable command identity is an internal execution claim, never a public API:
    /// strip it from every public run body (shared with D3 via <see cref="RunCommandRedaction"/>).
    /// Backend returns it on start/cancel accept and on the owner-readable run row, so all three go
    /// through here; events carry a different DTO with no top-level command id.</summary>
    private static AgentProxyResponse Redact(AgentProxyResponse response)
        => response.Status is <200 or >=300
            ? response
            : response with { Body = RunCommandRedaction.StripCommandId(response.Body, FailurePrefix) };

    private async Task<AgentProxyResponse> Send(
        HttpMethod method,
        string path,
        UserContext ctx,
        object? body,
        string? key,
        CancellationToken ct)
    {
        var request = backend.BuildRequest(method, path, ctx, body);
        if (!string.IsNullOrWhiteSpace(key))
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        }

        var (status, responseBody, etag) = await backend.SendForProxyAsync(
            request,
            FailurePrefix + "Backend ",
            ct);
        return new AgentProxyResponse(status, responseBody, etag);
    }

    private static (Guid RunId, Guid CommandId) RequiredDispatchIds(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("id", out var run)
                && run.ValueKind == JsonValueKind.String
                && run.TryGetGuid(out var runId)
                && root.TryGetProperty("command_id", out var command)
                && command.ValueKind == JsonValueKind.String
                && command.TryGetGuid(out var commandId))
            {
                return (runId, commandId);
            }
        }
        catch (JsonException)
        {
            // Converted to the controlled downstream failure below.
        }

        throw new WorkflowInvocationException(
            FailurePrefix + "Backend allocation 缺少有效 id 或 command_id");
    }

    private Task DispatchBestEffortAsync(
        Guid runId,
        Guid commandId,
        UserContext ctx,
        CancellationToken ct)
        => InternalRequest.KickBestEffortAsync(workflow,
            workflowOptions.BaseUrl.TrimEnd('/') + $"/orchestrator-runs/{runId:D}/dispatch",
            workflowOptions.InternalToken,
            ctx,
            new { command_id = commandId.ToString("D"), context = new { } },
            Json,
            logger,
            $"Root Workflow dispatch for run {runId:D}",
            ct);
}
