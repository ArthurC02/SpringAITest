using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Backend.Api.Agents;
using Backend.Api.Common;

namespace Backend.Api.PromptArtifacts;

/// <summary>
/// P1 canonical prompt manifest bounds (plan 03 §2/§3). The manifest is an identity artifact:
/// it references explicit component revisions and catalog hashes, never prompt content itself.
/// </summary>
public static class PromptArtifactContract
{
    /// <summary>Composition schema version. A manifest that does not name exactly this fails closed.</summary>
    public const int SchemaVersion = 1;

    /// <summary>Raw component text shares the Agent <c>system_prompt</c> ceiling — same kind of authored prompt text.</summary>
    public const int MaxContentLength = AgentExecutionContract.MaxSystemPromptLength;

    public const int MaxCatalogHashLength = 128;

    /// <summary>The closed component-kind enumeration; anything else is rejected at the trust boundary.</summary>
    public static readonly IReadOnlyList<string> Kinds =
        ["governance_frame", "guard", "routing", "summary", "persona", "memory_policy"];

    public static bool IsKind(string? kind)
        => kind is not null && Kinds.Contains(kind, StringComparer.Ordinal);
}

/// <summary>
/// Canonical manifest serialization. Reuses <see cref="AgentCanonicalizer.CanonicalizeDefinition"/>
/// (recursive ordinal key ordering) so this service keeps exactly one canonical JSON implementation
/// — plan 03 §3.1 forbids a third one next to it and Workflow's <c>canonical_json.py</c>.
/// </summary>
public static class PromptManifestCanonicalizer
{
    public static string Canonicalize(
        int schemaVersion,
        IReadOnlyDictionary<string, int> components,
        string toolCatalogHash,
        string skillCatalogHash)
    {
        var referenced = new JsonObject();
        foreach (var (kind, revision) in components)
        {
            referenced[kind] = revision;
        }

        return AgentCanonicalizer.CanonicalizeDefinition(new JsonObject
        {
            ["schema_version"] = schemaVersion,
            ["components"] = referenced,
            ["tool_catalog_hash"] = toolCatalogHash,
            ["skill_catalog_hash"] = skillCatalogHash,
        }.ToJsonString());
    }
}

/// <summary>Publish a component revision. <c>content</c> is protected raw text and is never echoed back.</summary>
public sealed record PromptComponentPublishRequest(
    [property: JsonPropertyName("kind")] string? Kind,
    [property: JsonPropertyName("content")] string? Content);

/// <summary>
/// Create a manifest revision. <c>components</c> maps component kind → explicit revision integer;
/// <c>latest</c> (or any non-integer) fails model binding, which is the point — plan 03 §2 bans it.
/// Catalog hashes are caller-supplied identity strings; P1 stores them without resolving their origin.
/// </summary>
public sealed record PromptManifestCreateRequest(
    [property: JsonPropertyName("schema_version")] int? SchemaVersion,
    [property: JsonPropertyName("components")] IReadOnlyDictionary<string, int>? Components,
    [property: JsonPropertyName("tool_catalog_hash")] string? ToolCatalogHash,
    [property: JsonPropertyName("skill_catalog_hash")] string? SkillCatalogHash);

/// <summary>Stored component revision. <c>content</c> itself never leaves the repository layer.</summary>
public sealed record PromptComponentRecord(
    string Kind,
    int Revision,
    string ContentSha256,
    int ContentLength,
    string CreatedBy,
    DateTime CreatedAt);

/// <summary>Stored manifest revision; <c>ManifestCanonical</c> is the exact hashed canonical text.</summary>
public sealed record PromptManifestRecord(
    int Revision,
    string ManifestCanonical,
    string ManifestSha256,
    string CreatedBy,
    DateTime CreatedAt);

/// <summary>Immutable manifest identity pinned into a published Agent snapshot.</summary>
public sealed record PromptManifestPin(int Revision, string Sha256);

/// <summary>
/// Component projection for every list/read route. Deliberately has no content field: the raw text
/// is protected and the only server-authored description is <c>summary</c>, built from metadata.
/// </summary>
public sealed record PromptComponentResponse(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("revision")] int Revision,
    [property: JsonPropertyName("content_sha256")] string ContentSha256,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("created_at")] DateTime CreatedAt)
{
    public static PromptComponentResponse From(PromptComponentRecord record) => new(
        record.Kind,
        record.Revision,
        record.ContentSha256,
        $"{record.Kind} revision {record.Revision} ({record.ContentLength} chars)",
        record.CreatedAt);
}

/// <summary>Manifest projection. The canonical manifest carries only references/hashes, no prompt text.</summary>
public sealed record PromptManifestResponse(
    [property: JsonPropertyName("revision")] int Revision,
    [property: JsonPropertyName("manifest_sha256")] string ManifestSha256,
    [property: JsonPropertyName("manifest")]
    [property: JsonConverter(typeof(RawJsonConverter))] string Manifest,
    [property: JsonPropertyName("created_at")] DateTime CreatedAt)
{
    public static PromptManifestResponse From(PromptManifestRecord record)
        => new(record.Revision, record.ManifestSha256, record.ManifestCanonical, record.CreatedAt);
}

/// <summary>Manifest list item (no manifest body).</summary>
public sealed record PromptManifestSummary(
    [property: JsonPropertyName("revision")] int Revision,
    [property: JsonPropertyName("manifest_sha256")] string ManifestSha256,
    [property: JsonPropertyName("created_at")] DateTime CreatedAt);
