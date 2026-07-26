using System.Text.Json;
using Backend.Api.Skills;
using Backend.Api.OrchestratorRuns;

namespace Backend.Api.Contexts;

public static class ContextAcquireProjection
{
    public static OrchestratorContextAcquireResponse? Build(ContextStoredRevision stored)
    {
        if (stored.Revision.ContextRef is not { } contextRef || string.IsNullOrWhiteSpace(stored.AdapterId)) return null;
        var view = stored.Views.FirstOrDefault(x => x.ViewId == contextRef.ViewId);
        if (view is null) return null;
        var contextRefElement = JsonSerializer.SerializeToElement(contextRef);
        var context = JsonSerializer.SerializeToElement(new { context_ref = contextRefElement, view = view.Definition });
        var now = stored.Revision.CreatedAt.ToString("O");
        var provenance = new[]
        {
            JsonSerializer.SerializeToElement(new { context_key = "context_ref", source_type = "context-tool", source_id = stored.AdapterId, observed_at = now, content_sha256 = SkillHash.Sha256(Backend.Api.Agents.AgentCanonicalizer.CanonicalizeDefinition(contextRefElement.GetRawText())) }),
            JsonSerializer.SerializeToElement(new { context_key = "view", source_type = "context-tool", source_id = stored.AdapterId, observed_at = now, content_sha256 = SkillHash.Sha256(Backend.Api.Agents.AgentCanonicalizer.CanonicalizeDefinition(view.Definition.GetRawText())) }),
        };
        return new(true, context, provenance, Array.Empty<string>());
    }
}
