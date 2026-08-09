using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Platform.Service.Abstractions;
using Platform.Service.Dtos;
using Platform.Service.Exceptions;
using Platform.Service.Options;

namespace Platform.Service;

/// <summary>
/// D6 transport-neutral adapter. Backend remains the selection, authorization and durable-run
/// authority; Platform only resolves, allocates, best-effort kicks Workflow, then reads the
/// caller-safe terminal result.
///
/// Polling has no local cap on this side: <see cref="PollAsync"/> loops until the run reaches
/// waiting_input or a terminal status, or until the caller's <c>CancellationToken</c> fires
/// (client disconnect detaches from polling and deliberately does not cancel the durable run).
/// Termination of an otherwise stuck run therefore depends on Backend's deadline being turned
/// into a terminal status by Workflow's recovery sweep — not on any timeout kept here.
/// </summary>
public sealed class AgentChatRuntime(
    BackendClient backend,
    HttpClient workflow,
    WorkflowOptions workflowOptions,
    AgentChatOptions options,
    ILogger<AgentChatRuntime> logger) : IAgentChatRuntime
{
    // ponytail: fixed interval, no backoff. A poll costs one HTTP round trip plus one Backend read,
    // and the user never feels the difference -- the LLM's own seconds-scale latency swallows it --
    // so 500ms simply spends an order of magnitude fewer Backend requests per run than the 100ms it
    // replaces. If the canary widens and polling is still the bottleneck, the upgrade path is staged
    // backoff or a push channel, not another turn of this dial.
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    public async Task<AgentResponse?> RunAsync(
        string message, string conversationId, Guid? requestedOrchestratorId,
        IChatIdentityAccessor identity, string? logicalAttemptId = null,
        CancellationToken ct = default)
    {
        var user = identity.CurrentUser;
        if (user is null || !options.IsCanaryTenant(user.TenantCode))
        {
            if (requestedOrchestratorId is not null)
                throw new WorkflowNotFoundException("Orchestrator is unavailable");
            return null;
        }
        // RuntimeDiscovery canonicalizes these wire fields with Trim() before both start and
        // resume.  Replay fingerprints and opaque attempt scope must use that exact form.
        message = message.Trim();
        conversationId = conversationId.Trim();

        // Do not derive an attempt from message content: identical messages
        // must remain distinct commands unless the client explicitly retries.
        var attempt = string.IsNullOrWhiteSpace(logicalAttemptId)
            ? Guid.NewGuid().ToString("N")
            : logicalAttemptId;
        // Commands are independently idempotent.  Never reuse a root-start key for a
        // resume/cancel command: that would make a key's durable lineage ambiguous.
        var chatKey = NewIdempotencyKey("chat", user, conversationId, attempt);
        var resumeKey = NewIdempotencyKey("resume", user, conversationId, attempt);
        var switchKey = NewIdempotencyKey("switch", user, conversationId, attempt);
        if (!string.IsNullOrWhiteSpace(logicalAttemptId))
        {
            var replay = await TryGetAttemptReplayAsync(resumeKey, conversationId, requestedOrchestratorId, message, user, ct)
                ?? await TryGetAttemptReplayAsync(chatKey, conversationId, requestedOrchestratorId, message, user, ct);
            if (replay is { } prior)
            {
                var priorRun = RequiredObject(prior, "run");
                SetMetadata(identity, priorRun);
                // A lost response can race the best-effort kick.  Only re-kick the command
                // whose durable idempotency record matched; waiting and terminal runs are
                // observations, never fresh commands.
                var status = RequiredString(priorRun, "status");
                if (status is "queued" or "running")
                    await DispatchBestEffortAsync(
                        RequiredGuid(priorRun, "id"), RequiredGuid(prior, "command_id"), user, ct);
                return await PollAsync(RequiredGuid(priorRun, "id"), user, ct);
            }
        }
        var active = await TryGetActiveAsync(conversationId, user, ct);
        if (active is { } activeRoot)
        {
            var activeRun = RequiredObject(activeRoot, "run");
            var activeOrchestratorId = RequiredGuid(activeRun, "orchestrator_id");
            var sameSelection = requestedOrchestratorId is null
                || requestedOrchestratorId == activeOrchestratorId;
            if (sameSelection)
            {
                SetMetadata(identity, activeRun);
                var activeStatus = RequiredString(activeRun, "status");
                if (activeStatus == "waiting_input")
                {
                    var resumed = await ResumeAsync(
                        RequiredGuid(activeRun, "id"), message, user, resumeKey, ct);
                    return await DispatchAndPollAsync(resumed, identity, user, ct);
                }
                return await PollAsync(RequiredGuid(activeRun, "id"), user, ct);
            }

            await CancelAsync(RequiredGuid(activeRun, "id"), user, switchKey, ct);
        }

        var resolved = await ResolveAsync(requestedOrchestratorId, user, ct);
        if (resolved.Mode == "legacy")
        {
            if (requestedOrchestratorId is not null)
                throw new DownstreamConflictException("Explicit Orchestrator cannot resolve to legacy mode");
            return null;
        }
        if (resolved.OrchestratorId is null)
            throw new WorkflowInvocationException("Runtime resolver returned an invalid Orchestrator");
        var accepted = await PostJsonAsync(
            "/api/chat-runs",
            new
            {
                message,
                conversation_id = conversationId,
                orchestrator_id = resolved.OrchestratorId,
            },
            user,
            chatKey,
            ct);

        return await DispatchAndPollAsync(accepted, identity, user, ct);
    }

    private async Task<AgentResponse> DispatchAndPollAsync(
        JsonElement accepted, IChatIdentityAccessor identity, UserContext user, CancellationToken ct)
    {
        var acceptedRun = ParseAccepted(accepted);
        identity.TurnMetadata = acceptedRun.Metadata;
        await DispatchBestEffortAsync(acceptedRun.RunId, acceptedRun.CommandId, user, ct);
        return await PollAsync(acceptedRun.RunId, user, ct);
    }

    private async Task<AgentResponse> PollAsync(Guid runId, UserContext user, CancellationToken ct)
    {
        while (true)
        {
            var state = await GetJsonAsync($"/api/chat-runs/{runId:D}", user, ct);
            var run = RequiredObject(state, "run");
            var status = RequiredString(run, "status");
            switch (status)
            {
                case "queued":
                case "running":
                    await Task.Delay(PollInterval, ct);
                    continue;
                case "waiting_input":
                    return new AgentResponse(new ChatMessage(
                        ChatRole.Assistant, RenderClarification(run)));
                case "completed":
                    return new AgentResponse(new ChatMessage(ChatRole.Assistant, RenderResult(run)));
                case "failed":
                case "cancelled":
                case "timed_out":
                    throw new WorkflowInvocationException($"Root Orchestrator ended with {status}");
                default:
                    throw new WorkflowInvocationException("Root Orchestrator returned an invalid state");
            }
        }
    }

    private async Task<JsonElement?> TryGetActiveAsync(
        string conversationId, UserContext user, CancellationToken ct)
    {
        using var request = backend.BuildRequest(
            HttpMethod.Get,
            "/api/chat-runs/active?conversation_id=" + Uri.EscapeDataString(conversationId),
            user);
        using var response = await backend.SendAsync(
            request,
            ex => new WorkflowInvocationException("Agent chat Backend unavailable", ex),
            ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        return await ReadSuccessfulJsonAsync(response, ct);
    }

    private async Task<JsonElement?> TryGetAttemptReplayAsync(
        string attemptKey, string conversationId, Guid? requestedOrchestratorId, string message,
        UserContext user, CancellationToken ct)
    {
        using var request = backend.BuildRequest(HttpMethod.Post, "/api/chat-runs/replay", user, new
        {
            conversation_id = conversationId,
            orchestrator_id = requestedOrchestratorId,
            message,
        });
        request.Headers.TryAddWithoutValidation("Idempotency-Key", attemptKey);
        using var response = await backend.SendAsync(
            request,
            ex => new WorkflowInvocationException("Agent chat Backend unavailable", ex),
            ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        return await ReadSuccessfulJsonAsync(response, ct);
    }

    private Task<JsonElement> ResumeAsync(
        Guid runId, string input, UserContext user, string attemptKey, CancellationToken ct) =>
        PostJsonAsync(
            $"/api/chat-runs/{runId:D}/resume",
            new { input },
            user,
            attemptKey,
            ct);

    private async Task CancelAsync(
        Guid runId, UserContext user, string attemptKey, CancellationToken ct)
    {
        using var request = backend.BuildRequest(
            HttpMethod.Post,
            $"/api/orchestrator-runs/{runId:D}/cancel",
            user,
            new { reason = "orchestrator switched by caller" });
        request.Headers.TryAddWithoutValidation(
            "Idempotency-Key", attemptKey);
        using var response = await backend.SendAsync(
            request,
            ex => new WorkflowInvocationException("Agent chat cancel unavailable", ex),
            ct);
        if (!response.IsSuccessStatusCode)
            throw await MapBackendErrorAsync(response, ct);
    }

    private async Task<(string Mode, Guid? OrchestratorId)> ResolveAsync(
        Guid? requested, UserContext user, CancellationToken ct)
    {
        var json = await PostJsonAsync(
            "/api/runtime-discovery/resolve",
            new { orchestrator_id = requested },
            user,
            null,
            ct);
        var mode = RequiredString(json, "mode");
        if (mode == "legacy")
            return (mode, null);
        var orchestrator = RequiredObject(json, "orchestrator");
        return (mode, RequiredGuid(orchestrator, "id"));
    }

    private async Task<JsonElement> PostJsonAsync(
        string path, object body, UserContext user, string? idempotencyKey, CancellationToken ct)
    {
        using var request = backend.BuildRequest(HttpMethod.Post, path, user, body);
        if (idempotencyKey is not null)
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        return await SendJsonAsync(request, ct);
    }

    private Task<JsonElement> GetJsonAsync(string path, UserContext user, CancellationToken ct) =>
        SendJsonAsync(backend.BuildRequest(HttpMethod.Get, path, user), ct);

    private async Task<JsonElement> SendJsonAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using (request)
        using (var response = await backend.SendAsync(
                   request,
                   ex => new WorkflowInvocationException("Agent chat Backend unavailable", ex),
                   ct))
        {
            return await ReadSuccessfulJsonAsync(response, ct);
        }
    }

    private async Task<JsonElement> ReadSuccessfulJsonAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
            throw await MapBackendErrorAsync(response, ct);
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new WorkflowInvocationException("Agent chat Backend returned invalid JSON", ex);
        }
    }

    private Task<Exception> MapBackendErrorAsync(
        HttpResponseMessage response, CancellationToken ct)
        => BackendErrorMapper.MapErrorAsync(response, backend, "Agent chat Backend ", ct);

    private static (Guid RunId, Guid CommandId, ChatTurnMetadata Metadata) ParseAccepted(JsonElement root)
    {
        var run = RequiredObject(root, "run");
        var runId = RequiredGuid(run, "id");
        return (
            runId,
            RequiredGuid(root, "command_id"),
            new ChatTurnMetadata(
                RequiredGuid(run, "orchestrator_id"),
                RequiredInt(run, "orchestrator_revision"),
                RequiredGuid(run, "workflow_id"),
                RequiredInt(run, "workflow_revision"),
                runId));
    }

    private Task DispatchBestEffortAsync(
        Guid runId, Guid commandId, UserContext user, CancellationToken ct)
        => InternalRequest.KickBestEffortAsync(
            workflow,
            workflowOptions.BaseUrl.TrimEnd('/') + $"/orchestrator-runs/{runId:D}/dispatch",
            workflowOptions.InternalToken,
            user,
            new { command_id = commandId.ToString("D"), context = new { } },
            logger,
            $"D6 Root Workflow dispatch for run {runId:D}",
            ct);

    private static string RenderResult(JsonElement run)
    {
        if (!run.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
            throw new WorkflowInvocationException("Completed Root Orchestrator has no result");
        if (result.TryGetProperty("aggregate", out var aggregate))
        {
            if (aggregate.ValueKind == JsonValueKind.Object
                && aggregate.TryGetProperty("answer", out var answer)
                && answer.ValueKind == JsonValueKind.String)
                return answer.GetString() ?? string.Empty;
            return aggregate.GetRawText();
        }
        return result.GetRawText();
    }

    private static string RenderClarification(JsonElement run)
    {
        if (run.TryGetProperty("result", out var result)
            && result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("clarification", out var questions)
            && questions.ValueKind == JsonValueKind.Array)
        {
            foreach (var question in questions.EnumerateArray())
            {
                if (question.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(question.GetString()))
                    return question.GetString()!;
            }
        }
        throw new WorkflowInvocationException(
            "Root Orchestrator is waiting without a clarification question");
    }

    private static void SetMetadata(IChatIdentityAccessor identity, JsonElement run)
    {
        identity.TurnMetadata = new ChatTurnMetadata(
            RequiredGuid(run, "orchestrator_id"),
            RequiredInt(run, "orchestrator_revision"),
            RequiredGuid(run, "workflow_id"),
            RequiredInt(run, "workflow_revision"),
            RequiredGuid(run, "id"));
    }

    private static string NewIdempotencyKey(
        string operation, UserContext user, string scope, string attempt)
    {
        // Backend caps Idempotency-Key at 128 characters. Hash every
        // caller-derived component (including the client logical attempt), so
        // keys remain scoped/idempotent without exposing identity or a long
        // conversation ID in an HTTP header.
        var material = string.Concat(
            operation, '\0', user.TenantCode, '\0', user.UserId, '\0', scope,
            '\0', attempt);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return $"{operation}:sha256:{Convert.ToHexStringLower(digest)}";
    }


    private static JsonElement RequiredObject(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : throw new WorkflowInvocationException($"Agent chat response lacks {property}");

    private static string RequiredString(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new WorkflowInvocationException($"Agent chat response lacks {property}");

    private static Guid RequiredGuid(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value) && value.TryGetGuid(out var id)
            ? id
            : throw new WorkflowInvocationException($"Agent chat response lacks {property}");

    private static int RequiredInt(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value) && value.TryGetInt32(out var number)
            ? number
            : throw new WorkflowInvocationException($"Agent chat response lacks {property}");
}
