using System.Text.Json;
using System.Text.Json.Serialization;
using Backend.Api.Common;

namespace Backend.Api.Workflows;

public static class WorkflowCompilerContracts
{
    public const string Current = "d4-graph-ir-1";
}

/// <summary>
/// The designer persists product Graph IR, never React Flow state or executable source.  The
/// graph uses its published camelCase contract internally; the management envelope remains
/// snake_case with the rest of the backend authoring APIs.
/// </summary>
public sealed record WorkflowUpsert(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("kind")] string? Kind,
    [property: JsonPropertyName("definition")] JsonElement Definition,
    [property: JsonPropertyName("ui_metadata")] JsonElement UiMetadata);

public sealed record WorkflowPublishRequest(
    [property: JsonPropertyName("expected_draft_version")] long? ExpectedDraftVersion);

public sealed record WorkflowValidationError(
    [property: JsonPropertyName("field")] string Field,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("node_id")] string? NodeId = null,
    [property: JsonPropertyName("edge_id")] string? EdgeId = null);

public sealed record WorkflowValidationResponse(
    [property: JsonPropertyName("valid")] bool Valid,
    [property: JsonPropertyName("definition")] JsonElement? Definition,
    [property: JsonPropertyName("ui_metadata")] JsonElement? UiMetadata,
    [property: JsonPropertyName("errors")] IReadOnlyList<WorkflowValidationError> Errors);

public sealed record Workflow(
    Guid Id, string Name, string Kind, bool Enabled, long DraftVersion,
    long? DraftValidatedVersion, int? PublishedRevision, string DraftDefinition,
    string DraftUiMetadata, string DefinitionSha256, string UiMetadataSha256,
    DateTime CreatedAt, DateTime UpdatedAt, bool SystemOwned = false);

public sealed record WorkflowInfo(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("draft_version")] long DraftVersion,
    [property: JsonPropertyName("draft_validated_version")] long? DraftValidatedVersion,
    [property: JsonPropertyName("published_revision")] int? PublishedRevision,
    [property: JsonPropertyName("created_at")] DateTime CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTime UpdatedAt,
    [property: JsonPropertyName("system_owned")] bool SystemOwned);

public sealed record WorkflowResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("draft_version")] long DraftVersion,
    [property: JsonPropertyName("draft_validated_version")] long? DraftValidatedVersion,
    [property: JsonPropertyName("published_revision")] int? PublishedRevision,
    [property: JsonPropertyName("definition")]
    [property: JsonConverter(typeof(RawJsonConverter))] string Definition,
    [property: JsonPropertyName("ui_metadata")]
    [property: JsonConverter(typeof(RawJsonConverter))] string UiMetadata,
    [property: JsonPropertyName("created_at")] DateTime CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTime UpdatedAt,
    [property: JsonPropertyName("system_owned")] bool SystemOwned)
{
    public static WorkflowResponse From(Workflow value) => new(value.Id, value.Name, value.Kind,
        value.Enabled, value.DraftVersion, value.DraftValidatedVersion, value.PublishedRevision,
        value.DraftDefinition, value.DraftUiMetadata, value.CreatedAt, value.UpdatedAt, value.SystemOwned);
}

public sealed record WorkflowRevisionInfo(
    [property: JsonPropertyName("revision")] int Revision,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("definition_sha256")] string DefinitionSha256,
    [property: JsonPropertyName("ui_metadata_sha256")] string UiMetadataSha256,
    [property: JsonPropertyName("compiler_contract_version")] string CompilerContractVersion,
    [property: JsonPropertyName("created_by")] string CreatedBy,
    [property: JsonPropertyName("created_at")] DateTime CreatedAt);

public enum WorkflowWriteStatus { Success, NotFound, VersionConflict, Duplicate, SystemOwned }
public sealed record WorkflowWriteResult(WorkflowWriteStatus Status, Workflow? Workflow = null, int Revision = 0);

public sealed record CompilerValidationResult(
    string CanonicalDefinition, string CanonicalUiMetadata, string CompilerContractVersion,
    IReadOnlyList<WorkflowValidationError> Errors)
{
    public bool Valid => Errors.Count == 0;
}

public sealed record WorkflowToolInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("risk")] string Risk);
