using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Platform.Service.Abstractions;
using Platform.Service.Dtos;
using Platform.Service.Exceptions;
using Platform.Service.Options;

namespace Platform.Service;

/// <summary>
/// Coordinates the D3 durable command protocol. Backend first allocates the command;
/// Workflow then claims the immutable command envelope by ID and owns dispatch completion.
/// </summary>
public sealed class AgentRunService : IAgentRunService
{
    private const string FailurePrefix = "Agent 執行服務失敗：";
    private static readonly JsonSerializerOptions JsonOpts =
        new(JsonSerializerDefaults.Web);

    private readonly BackendClient _backend;
    private readonly HttpClient _workflow;
    private readonly WorkflowOptions _workflowOptions;
    private readonly ILogger<AgentRunService> _logger;

    public AgentRunService(
        BackendClient backend,
        HttpClient workflow,
        WorkflowOptions workflowOptions,
        ILogger<AgentRunService> logger)
    {
        _backend = backend;
        _workflow = workflow;
        _workflowOptions = workflowOptions;
        _logger = logger;
    }

    public async Task<AgentProxyResponse> StartAsync(
        Guid agentId,
        string? message,
        string? idempotencyKey,
        UserContext ctx,
        CancellationToken ct = default)
    {
        var normalizedMessage = message?.Trim();
        var allocation = await BackendCommandAsync(
            HttpMethod.Post,
            $"/api/agents/{agentId:D}/runs",
            ctx,
            new { message = normalizedMessage },
            idempotencyKey,
            ct);
        if (!IsSuccess(allocation.Response.Status) || !allocation.DispatchRequired)
        {
            return allocation.Response;
        }

        var runId = RequiredGuid(allocation.Response.Body, "id");
        await KickWorkflowAsync(
            $"/agent-runs/{runId:D}/start",
            ctx,
            allocation.CommandId!.Value,
            ct);
        return allocation.Response;
    }

    public Task<AgentProxyResponse> GetAsync(
        Guid runId,
        UserContext ctx,
        CancellationToken ct = default)
        => BackendAsync(HttpMethod.Get, $"/api/runs/{runId:D}", ctx, null, null, ct);

    public Task<AgentProxyResponse> EventsAsync(
        Guid runId,
        long afterSequence,
        int limit,
        UserContext ctx,
        CancellationToken ct = default)
        => BackendAsync(
            HttpMethod.Get,
            $"/api/runs/{runId:D}/events?after_sequence="
            + afterSequence.ToString(CultureInfo.InvariantCulture)
            + "&limit="
            + limit.ToString(CultureInfo.InvariantCulture),
            ctx,
            null,
            null,
            ct);

    public async Task<AgentProxyResponse> ResumeAsync(
        Guid runId,
        string? message,
        long? expectedCheckpointVersion,
        string? idempotencyKey,
        UserContext ctx,
        CancellationToken ct = default)
    {
        var normalizedMessage = message?.Trim();
        var allocation = await BackendCommandAsync(
            HttpMethod.Post,
            $"/api/runs/{runId:D}/resume",
            ctx,
            new
            {
                message = normalizedMessage,
                expected_checkpoint_version = expectedCheckpointVersion,
            },
            idempotencyKey,
            ct);
        if (!IsSuccess(allocation.Response.Status) || !allocation.DispatchRequired)
        {
            return allocation.Response;
        }

        await KickWorkflowAsync(
            $"/agent-runs/{runId:D}/resume",
            ctx,
            allocation.CommandId!.Value,
            ct);
        return allocation.Response;
    }

    public async Task<AgentProxyResponse> CancelAsync(
        Guid runId,
        string? reason,
        string? idempotencyKey,
        UserContext ctx,
        CancellationToken ct = default)
    {
        var allocation = await BackendCommandAsync(
            HttpMethod.Post,
            $"/api/runs/{runId:D}/cancel",
            ctx,
            new { reason },
            idempotencyKey,
            ct);
        if (!IsSuccess(allocation.Response.Status) || !allocation.DispatchRequired)
        {
            return allocation.Response;
        }

        await KickWorkflowAsync(
            $"/agent-runs/{runId:D}/cancel",
            ctx,
            allocation.CommandId!.Value,
            ct);
        return allocation.Response;
    }

    private async Task<AgentProxyResponse> BackendAsync(
        HttpMethod method,
        string path,
        UserContext ctx,
        object? body,
        string? idempotencyKey,
        CancellationToken ct)
    {
        using var request = _backend.BuildRequest(method, path, ctx, body);
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        using var response = await _backend.SendAsync(request, WrapTransport, ct);
        var status = (int)response.StatusCode;
        if (status >= 500)
        {
            throw new WorkflowInvocationException(FailurePrefix + "Backend HTTP " + status);
        }

        return new AgentProxyResponse(
            status,
            await response.Content.ReadAsStringAsync(ct),
            response.Headers.ETag?.ToString());
    }

    private async Task<BackendCommandResult> BackendCommandAsync(
        HttpMethod method,
        string path,
        UserContext ctx,
        object? body,
        string? idempotencyKey,
        CancellationToken ct)
    {
        using var request = _backend.BuildRequest(method, path, ctx, body);
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        using var response = await _backend.SendAsync(request, WrapTransport, ct);
        var status = (int)response.StatusCode;
        if (status >= 500)
        {
            throw new WorkflowInvocationException(FailurePrefix + "Backend HTTP " + status);
        }

        var backendBody = await response.Content.ReadAsStringAsync(ct);
        var proxy = new AgentProxyResponse(
            status,
            IsSuccess(status)
                ? StripInternalCommandMetadata(backendBody)
                : backendBody,
            response.Headers.ETag?.ToString());
        if (!IsSuccess(status))
        {
            return new BackendCommandResult(proxy, false, null);
        }

        var dispatchRequired = RequiredBooleanHeader(
            response,
            "X-Agent-Run-Dispatch-Required");
        if (!dispatchRequired)
        {
            return new BackendCommandResult(proxy, false, null);
        }

        var commandIdText = RequiredHeader(response, "X-Agent-Run-Command-Id");
        if (!Guid.TryParse(commandIdText, out var commandId))
        {
            throw new WorkflowInvocationException(
                FailurePrefix + "Backend command dispatch metadata 無效");
        }

        return new BackendCommandResult(proxy, true, commandId);
    }

    private async Task KickWorkflowAsync(
        string path,
        UserContext ctx,
        Guid commandId,
        CancellationToken ct)
    {
        try
        {
            using var request = InternalRequest.Build(
                HttpMethod.Post,
                _workflowOptions.BaseUrl.TrimEnd('/') + path,
                _workflowOptions.InternalToken,
                ctx,
                new { command_id = commandId },
                JsonOpts);
            using var response = await InternalRequest.SendAsync(
                _workflow,
                request,
                WrapTransport,
                ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Agent run Workflow kick failed for command {CommandId}: HTTP {StatusCode}",
                    commandId,
                    (int)response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Agent run Workflow kick failed for command {CommandId}; durable command remains recoverable",
                commandId);
        }
    }

    private Exception WrapTransport(Exception ex)
        => new WorkflowInvocationException(FailurePrefix + ex.Message, ex);

    private static bool IsSuccess(int status) => status is >= 200 and < 300;

    private static string RequiredHeader(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values)
            ? values.Single()
            : throw new WorkflowInvocationException(
                FailurePrefix + $"Backend 缺少必要 header：{name}");

    private static bool RequiredBooleanHeader(HttpResponseMessage response, string name)
        => bool.TryParse(RequiredHeader(response, name), out var value)
            ? value
            : throw new WorkflowInvocationException(
                FailurePrefix + $"Backend header {name} 無效");

    private static Guid RequiredGuid(string json, string propertyName)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty(propertyName, out var value)
                && value.ValueKind == JsonValueKind.String
                && value.TryGetGuid(out var parsed))
            {
                return parsed;
            }
        }
        catch (JsonException)
        {
            // Converted to the same controlled downstream failure below.
        }

        throw new WorkflowInvocationException(
            FailurePrefix + $"Backend 缺少必要欄位：{propertyName}");
    }

    private static string StripInternalCommandMetadata(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject run)
            {
                throw new JsonException("Backend run response is not an object");
            }

            run.Remove("command_id");
            return run.ToJsonString(JsonOpts);
        }
        catch (JsonException ex)
        {
            throw new WorkflowInvocationException(
                FailurePrefix + "Backend run response is invalid",
                ex);
        }
    }

    private sealed record BackendCommandResult(
        AgentProxyResponse Response,
        bool DispatchRequired,
        Guid? CommandId);
}
