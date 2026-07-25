using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Platform.Service.Abstractions;

namespace Platform.Service;

/// <summary>Shared-brain switch placed inside ChatTurnRecorder for both Chat and AG-UI.
/// Transport/session/memory/persistence wrappers therefore remain unchanged.</summary>
public sealed class AgentChatRoutingAgent(
    AIAgent legacy,
    IServiceScopeFactory scopeFactory) : DelegatingAIAgent(legacy)
{
    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options,
        CancellationToken cancellationToken)
    {
        var list = messages.ToList();
        var response = await TryRunAsync(list, cancellationToken);
        return response ?? await InnerAgent.RunAsync(list, session, options, cancellationToken);
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var list = messages.ToList();
        var response = await TryRunAsync(list, cancellationToken);
        if (response is null)
        {
            await foreach (var update in InnerAgent.RunStreamingAsync(list, session, options, cancellationToken))
                yield return update;
            yield break;
        }
        yield return new AgentResponseUpdate(ChatRole.Assistant, response.Text ?? string.Empty);
    }

    private async Task<AgentResponse?> TryRunAsync(
        IReadOnlyList<ChatMessage> messages, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var identity = scope.ServiceProvider.GetRequiredService<IChatIdentityAccessor>();
        var runtime = scope.ServiceProvider.GetRequiredService<IAgentChatRuntime>();
        var (_, conversationId) = identity.DeriveMemoryKeys();
        var userMessage = messages.LastOrDefault(item => item.Role == ChatRole.User);
        var message = userMessage?.Text ?? string.Empty;
        // Chat transports supply an inbound Idempotency-Key through the
        // accessor. AG-UI normally has no such header, but retains each user
        // wire MessageId across a logical retry.
        var logicalAttemptId = identity.LogicalAttemptId ?? userMessage?.MessageId;
        return await runtime.RunAsync(
            message, conversationId, identity.RequestedOrchestratorId, identity,
            logicalAttemptId, ct);
    }
}
