using Microsoft.Agents.AI;
using Platform.Service.Dtos;

namespace Platform.Service.Abstractions;

/// <summary>Shared D6 brain used behind both legacy chat transports. A null response means
/// the server resolver selected legacy mode; explicit selections never return null.</summary>
public interface IAgentChatRuntime
{
    Task<AgentResponse?> RunAsync(
        string message,
        string conversationId,
        Guid? requestedOrchestratorId,
        IChatIdentityAccessor identity,
        string? logicalAttemptId = null,
        CancellationToken ct = default);
}
