using Backend.Api.Agents;
using Backend.Api.Skills;
using System.Text.Json;

namespace Backend.Api.AgentRuns;

internal sealed record PublishedAgentSnapshotSource(
    Guid AgentId,
    string Name,
    int Revision,
    string Definition,
    string DefinitionSha256,
    Guid WorkflowId,
    int WorkflowRevision,
    IReadOnlyList<AgentRevisionSkillInfo> SkillBindings);

internal sealed record SkillSnapshotSource(
    Guid SkillId,
    string Name,
    string Description,
    int Revision,
    string Kind,
    string Definition,
    string DefinitionSha256,
    string? PackageSha256);

internal sealed record WorkflowSnapshotSource(
    Guid WorkflowId,
    int Revision,
    int SchemaVersion,
    string Definition,
    string DefinitionSha256,
    string CompilerContractVersion);

/// <summary>Immutable root/task provenance embedded in every D5 child snapshot.</summary>
internal sealed record OrchestratorChildSnapshotProvenance(
    Guid RootRunId,
    string TaskId,
    int Attempt);

/// <summary>
/// Narrow Lite/test seam. Production Dapper child creation remains atomic in
/// <c>OrchestratorRunRepository</c> and is never routed through direct-run creation.
/// </summary>
internal interface IOrchestratorChildRunRepository
{
    Task<AgentRunWriteResult> CreateOrchestratorChildAsync(
        string tenantId,
        string userId,
        string role,
        IReadOnlyCollection<string> groups,
        IReadOnlyCollection<string> capabilityClaims,
        PublishedAgentSnapshotSource agent,
        WorkflowSnapshotSource workflow,
        OrchestratorChildSnapshotProvenance provenance,
        string runKind,
        int tokenCap,
        JsonElement taskEnvelope,
        string idempotencyKey,
        CancellationToken ct);
}
