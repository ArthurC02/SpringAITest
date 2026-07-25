using Backend.Api.Agents;
using Backend.Api.Skills;

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
