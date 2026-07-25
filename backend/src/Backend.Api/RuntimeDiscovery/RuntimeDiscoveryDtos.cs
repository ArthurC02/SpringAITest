using System.Text.Json.Serialization;
using Backend.Api.OrchestratorRuns;

namespace Backend.Api.RuntimeDiscovery;

/// <summary>
/// D6's USER-facing boundary.  It deliberately excludes drafts, definitions, audience
/// rules and any other Builder material.
/// </summary>
public sealed record RuntimeOrchestratorSummary(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("revision")] int Revision,
    [property: JsonPropertyName("capabilities")] IReadOnlyList<string> Capabilities);

public sealed record RuntimeOrchestratorListResponse(
    [property: JsonPropertyName("orchestrators")] IReadOnlyList<RuntimeOrchestratorSummary> Orchestrators);

public sealed record RuntimeResolveRequest(
    [property: JsonPropertyName("orchestrator_id")] Guid? OrchestratorId = null);

public sealed record RuntimeResolveResponse(
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("orchestrator")] RuntimeOrchestratorSummary? Orchestrator = null);

public sealed record TenantRuntimeBinding(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("default_orchestrator_id")] Guid? DefaultOrchestratorId,
    [property: JsonPropertyName("default_orchestrator_revision")] int? DefaultOrchestratorRevision,
    [property: JsonPropertyName("canary_user_ids")] IReadOnlyList<string> CanaryUserIds);

public sealed record TenantRuntimeBindingUpsert(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("default_orchestrator_id")] Guid? DefaultOrchestratorId,
    [property: JsonPropertyName("default_orchestrator_revision")] int? DefaultOrchestratorRevision,
    [property: JsonPropertyName("canary_user_ids")] IReadOnlyList<string>? CanaryUserIds);

public sealed record ChatRunStartRequest(
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("conversation_id")] string? ConversationId,
    [property: JsonPropertyName("orchestrator_id")] Guid? OrchestratorId = null);

public sealed record ChatRunResponse(
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("run")] OrchestratorRunResponse Run,
    [property: JsonPropertyName("command_id")] Guid? CommandId,
    [property: JsonPropertyName("replayed")] bool Replayed = false);

public interface IRuntimeBindingRepository
{
    Task<TenantRuntimeBinding?> GetAsync(string tenantId, CancellationToken ct);
    Task<TenantRuntimeBinding> PutAsync(string tenantId, TenantRuntimeBinding binding, CancellationToken ct);
}
