using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
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
    private const string BackendFailurePrefix = FailurePrefix + "Backend ";
    private const string IdempotencyKeyHeader = "Idempotency-Key";

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

    public Task<AgentProxyResponse> ApprovalsAsync(
        Guid runId,
        UserContext ctx,
        CancellationToken ct = default)
        => BackendAsync(HttpMethod.Get, $"/api/runs/{runId:D}/approvals", ctx, null, null, ct);

    public async Task<AgentProxyResponse> DecideApprovalAsync(
        Guid runId,
        Guid approvalId,
        bool approve,
        string? reason,
        string? idempotencyKey,
        UserContext ctx,
        CancellationToken ct = default)
    {
        var response = await BackendAsync(
            HttpMethod.Post,
            $"/api/runs/{runId:D}/approvals/{approvalId:D}/" + (approve ? "approve" : "reject"),
            ctx,
            new { reason = reason?.Trim() },
            idempotencyKey,
            ct);
        if (approve && IsSuccess(response.Status))
        {
            await KickApprovedWriteAsync(runId, approvalId, ctx, ct);
        }
        return response;
    }

    private Task<AgentProxyResponse> BackendAsync(
        HttpMethod method,
        string path,
        UserContext ctx,
        object? body,
        string? idempotencyKey,
        CancellationToken ct)
        => _backend.SendForAgentProxyAsync(
            method,
            path,
            ctx,
            body,
            BackendFailurePrefix,
            string.IsNullOrWhiteSpace(idempotencyKey) ? null : (IdempotencyKeyHeader, idempotencyKey),
            ct);

    private async Task<BackendCommandResult> BackendCommandAsync(
        HttpMethod method,
        string path,
        UserContext ctx,
        object? body,
        string? idempotencyKey,
        CancellationToken ct)
    {
        // dispatch metadata 在 response header 上,必須在 response 釋放前取下。
        HttpResponseHeaders? headers = null;
        var (status, backendBody, etag) = await _backend.SendForProxyAsync(
            BuildRequest(method, path, ctx, body, idempotencyKey),
            BackendFailurePrefix,
            ct,
            response => headers = response.Headers);

        var proxy = new AgentProxyResponse(
            status,
            IsSuccess(status)
                ? RunCommandRedaction.StripCommandId(backendBody, FailurePrefix)
                : backendBody,
            etag);
        if (!IsSuccess(status))
        {
            return new BackendCommandResult(proxy, false, null);
        }

        var dispatchRequired = RequiredBooleanHeader(
            headers!,
            "X-Agent-Run-Dispatch-Required");
        if (!dispatchRequired)
        {
            return new BackendCommandResult(proxy, false, null);
        }

        var commandIdText = RequiredHeader(headers!, "X-Agent-Run-Command-Id");
        if (!Guid.TryParse(commandIdText, out var commandId))
        {
            throw new WorkflowInvocationException(
                FailurePrefix + "Backend command dispatch metadata 無效");
        }

        return new BackendCommandResult(proxy, true, commandId);
    }

    /// <summary>
    /// 只服務 <see cref="BackendCommandAsync"/>:它要在 response 釋放前取下 dispatch metadata header,
    /// 需要 <see cref="BackendClient.SendForProxyAsync"/> 的 inspectResponse,不能走
    /// <see cref="BackendClient.SendForAgentProxyAsync"/> 這條收斂路徑。
    /// </summary>
    private HttpRequestMessage BuildRequest(
        HttpMethod method, string path, UserContext ctx, object? body, string? idempotencyKey)
    {
        var request = _backend.BuildRequest(method, path, ctx, body);
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            request.Headers.TryAddWithoutValidation(IdempotencyKeyHeader, idempotencyKey);
        }

        return request;
    }

    private Task KickWorkflowAsync(
        string path,
        UserContext ctx,
        Guid commandId,
        CancellationToken ct)
        => InternalRequest.KickBestEffortAsync(
            _workflow,
            _workflowOptions.BaseUrl.TrimEnd('/') + path,
            _workflowOptions.InternalToken,
            ctx,
            new { command_id = commandId },
            _logger,
            $"Agent run Workflow kick for command {commandId:D}",
            ct);

    private Task KickApprovedWriteAsync(Guid runId, Guid approvalId, UserContext ctx, CancellationToken ct)
        => InternalRequest.KickBestEffortAsync(
            _workflow,
            _workflowOptions.BaseUrl.TrimEnd('/') + $"/agent-runs/{runId:D}/approvals/{approvalId:D}/execute",
            _workflowOptions.InternalToken,
            ctx,
            new { },
            _logger,
            $"Approved write Workflow kick for approval {approvalId:D}",
            ct);

    private static bool IsSuccess(int status) => status is >= 200 and < 300;

    private static string RequiredHeader(HttpResponseHeaders headers, string name)
        => headers.TryGetValues(name, out var values)
            ? values.Single()
            : throw new WorkflowInvocationException(
                FailurePrefix + $"Backend 缺少必要 header：{name}");

    private static bool RequiredBooleanHeader(HttpResponseHeaders headers, string name)
        => bool.TryParse(RequiredHeader(headers, name), out var value)
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

    private sealed record BackendCommandResult(
        AgentProxyResponse Response,
        bool DispatchRequired,
        Guid? CommandId);
}
