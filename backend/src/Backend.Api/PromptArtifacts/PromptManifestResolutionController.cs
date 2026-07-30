using System.Text;
using System.Text.Json.Nodes;
using Backend.Api.Common;
using Backend.Api.Skills;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Api.PromptArtifacts;

/// <summary>
/// Internal resolved-manifest route (plan 03 §3). Unlike <see cref="PromptArtifactController"/>,
/// this is deliberately NOT an ADMIN Builder route: Platform's and Workflow's deterministic prompt
/// assemblers call it at execution time (every request, not an authoring action), so it is gated
/// only by <c>PROMPT_ARTIFACTS_ENABLED</c> (404 while off, same middleware as the sibling
/// <c>prompt-manifests</c>/<c>prompt-components</c> routes) and tenant scoping — the global
/// <c>X-Internal-Token</c> middleware already covers the trust boundary.
///
/// THIS IS THE ONLY ROUTE IN THE SERVICE THAT RETURNS RAW PROMPT COMPONENT CONTENT. It exists
/// purely for service-to-service prompt composition and must NEVER be proxied by Platform to a
/// browser.
/// </summary>
[ApiController]
[Route("api")]
public sealed class PromptManifestResolutionController(IPromptArtifactRepository repo) : ControllerBase
{
    /// <summary>Manifest not found (including cross-tenant) → 404.</summary>
    [HttpGet("prompt-manifests/{revision:int}/resolved")]
    public async Task<ActionResult<PromptManifestResolvedResponse>> GetResolved(
        int revision, CancellationToken ct)
    {
        var tenantId = Request.RequireTenant();
        var manifest = await repo.GetManifestAsync(tenantId, revision, ct)
                       ?? throw ApiErrors.NotFound(" prompt manifest", revision);

        // Verify the stored canonical bytes against their own stored digest before parsing/serving
        // them — the same fail-closed posture as AgentCanonicalizer.ReadAuthoritativeDefinition and
        // SkillHash.MatchesSha256's other callers; this was previously the one artifact-read path in
        // the service that skipped it.
        if (!SkillHash.MatchesSha256(Encoding.UTF8.GetBytes(manifest.ManifestCanonical), manifest.ManifestSha256))
        {
            throw new InvalidOperationException(
                $"Stored prompt manifest hash mismatch: revision {revision}");
        }

        var parsed = JsonNode.Parse(manifest.ManifestCanonical)!.AsObject();
        var componentsNode = parsed["components"]!.AsObject();
        var components = new List<PromptResolvedComponent>(componentsNode.Count);
        // Enumeration order follows the canonical text (ordinal-sorted kind names by
        // PromptManifestCanonicalizer), so this is the manifest's own deterministic reference order.
        foreach (var (kind, revisionNode) in componentsNode)
        {
            var componentRevision = revisionNode!.GetValue<int>();
            var component = await repo.GetComponentContentAsync(tenantId, kind, componentRevision, ct)
                ?? throw new InvalidOperationException(
                    $"Prompt component referenced by manifest is missing: {kind}#{componentRevision}");
            if (!SkillHash.MatchesSha256(Encoding.UTF8.GetBytes(component.Content), component.ContentSha256))
            {
                throw new InvalidOperationException(
                    $"Stored prompt component hash mismatch: {kind}#{componentRevision}");
            }

            components.Add(new PromptResolvedComponent(
                kind, componentRevision, component.ContentSha256, component.Content));
        }

        return Ok(new PromptManifestResolvedResponse(
            manifest.Revision,
            manifest.ManifestSha256,
            parsed["schema_version"]!.GetValue<int>(),
            parsed["tool_catalog_hash"]!.GetValue<string>(),
            parsed["skill_catalog_hash"]!.GetValue<string>(),
            components));
    }
}
