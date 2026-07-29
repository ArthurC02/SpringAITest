using Backend.Api.Common;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Api.PromptArtifacts;

/// <summary>
/// P1 canonical prompt artifact authority (plan 03 §3). ADMIN-only Builder management API, same
/// posture as <see cref="Agents.AgentController"/>: tenant-scoped, snake_case, and hidden entirely
/// behind <c>PROMPT_ARTIFACTS_ENABLED</c> in Program.cs (404 before auth while off).
///
/// Raw component content is protected: it is accepted on publish and never returned by any route
/// here — component projections carry only kind/revision/SHA/server-authored summary/created_at,
/// and a manifest holds references and catalog hashes, never prompt text (plan 03 §2).
/// </summary>
[ApiController]
[Route("api")]
[AdminOnly(Message)]
public sealed class PromptArtifactController(IPromptArtifactRepository repo) : ControllerBase
{
    private const string Message = "權限不足，無法存取 prompt artifacts";

    [HttpPost("prompt-components")]
    public async Task<ActionResult<PromptComponentResponse>> PublishComponent(
        [FromBody] PromptComponentPublishRequest request, CancellationToken ct)
    {
        if (!PromptArtifactContract.IsKind(request.Kind))
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                $"不支援的 prompt component kind：{request.Kind}");
        }

        var record = await repo.PublishComponentAsync(
            Request.RequireTenant(), request.Kind!, RequireContent(request.Content),
            Request.UserIdOrEmpty(), ct);
        return Ok(PromptComponentResponse.From(record));
    }

    [HttpGet("prompt-components")]
    public async Task<ActionResult<IReadOnlyList<PromptComponentResponse>>> ListComponents(CancellationToken ct)
    {
        var records = await repo.ListComponentsAsync(Request.RequireTenant(), ct);
        return Ok(records.Select(PromptComponentResponse.From).ToList());
    }

    [HttpGet("prompt-components/{kind}/{revision:int}")]
    public async Task<ActionResult<PromptComponentResponse>> GetComponent(
        string kind, int revision, CancellationToken ct)
    {
        var record = await repo.GetComponentAsync(Request.RequireTenant(), kind, revision, ct)
                     ?? throw NotFoundComponent(kind, revision);
        return Ok(PromptComponentResponse.From(record));
    }

    /// <summary>
    /// Create an immutable manifest revision. Every referenced component revision must exist for
    /// this tenant; a missing, cross-tenant, or unsupported-schema reference fails closed (422)
    /// and writes nothing. Republishing the current manifest content is an idempotent no-op.
    /// </summary>
    [HttpPost("prompt-manifests")]
    public async Task<ActionResult<PromptManifestResponse>> CreateManifest(
        [FromBody] PromptManifestCreateRequest request, CancellationToken ct)
    {
        var tenantId = Request.RequireTenant();
        if (request.SchemaVersion != PromptArtifactContract.SchemaVersion)
        {
            throw new ApiException(
                StatusCodes.Status422UnprocessableEntity,
                $"不支援的 prompt manifest schema_version：{request.SchemaVersion}"
                + $"（必須是 {PromptArtifactContract.SchemaVersion}）");
        }
        if (request.Components is not { Count: > 0 } components)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "components 不可為空");
        }

        foreach (var (kind, revision) in components)
        {
            if (!PromptArtifactContract.IsKind(kind))
            {
                throw new ApiException(
                    StatusCodes.Status400BadRequest, $"不支援的 prompt component kind：{kind}");
            }
            if (await repo.GetComponentAsync(tenantId, kind, revision, ct) is null)
            {
                throw new ApiException(
                    StatusCodes.Status422UnprocessableEntity,
                    $"找不到 prompt component revision：{kind}#{revision}");
            }
        }

        var canonical = PromptManifestCanonicalizer.Canonicalize(
            PromptArtifactContract.SchemaVersion,
            components,
            RequireCatalogHash(request.ToolCatalogHash, "tool_catalog_hash"),
            RequireCatalogHash(request.SkillCatalogHash, "skill_catalog_hash"));
        var record = await repo.CreateManifestAsync(tenantId, canonical, Request.UserIdOrEmpty(), ct);
        return Ok(PromptManifestResponse.From(record));
    }

    [HttpGet("prompt-manifests")]
    public async Task<ActionResult<IReadOnlyList<PromptManifestSummary>>> ListManifests(CancellationToken ct)
    {
        var records = await repo.ListManifestsAsync(Request.RequireTenant(), ct);
        return Ok(records
            .Select(r => new PromptManifestSummary(r.Revision, r.ManifestSha256, r.CreatedAt))
            .ToList());
    }

    [HttpGet("prompt-manifests/{revision:int}")]
    public async Task<ActionResult<PromptManifestResponse>> GetManifest(int revision, CancellationToken ct)
    {
        var record = await repo.GetManifestAsync(Request.RequireTenant(), revision, ct)
                     ?? throw NotFoundManifest(revision);
        return Ok(PromptManifestResponse.From(record));
    }

    /// <summary>Reconciliation lookup for future shadow comparison: canonical SHA → manifest revision.</summary>
    [HttpGet("prompt-manifests/by-sha/{sha}")]
    public async Task<ActionResult<PromptManifestResponse>> GetManifestBySha(string sha, CancellationToken ct)
    {
        var record = await repo.FindManifestBySha256Async(Request.RequireTenant(), sha, ct)
                     ?? throw NotFoundManifest(sha);
        return Ok(PromptManifestResponse.From(record));
    }

    /// <summary>Content is stored and hashed verbatim — never trimmed, or the artifact identity would shift.</summary>
    private static string RequireContent(string? content)
        => string.IsNullOrWhiteSpace(content) || content.Length > PromptArtifactContract.MaxContentLength
            ? throw new ApiException(
                StatusCodes.Status400BadRequest,
                $"content 不可為空,且不可超過 {PromptArtifactContract.MaxContentLength} 字元")
            : content;

    private static string RequireCatalogHash(string? value, string field)
    {
        value = value?.Trim();
        return string.IsNullOrWhiteSpace(value)
               || value.Length > PromptArtifactContract.MaxCatalogHashLength
               || value.Any(char.IsControl)
            ? throw new ApiException(StatusCodes.Status400BadRequest, $"{field} 不可為空")
            : value;
    }

    private static ApiException NotFoundComponent(string kind, int revision)
        => ApiErrors.NotFound(" prompt component", $"{kind}#{revision}");

    private static ApiException NotFoundManifest(object id)
        => ApiErrors.NotFound(" prompt manifest", id);
}
