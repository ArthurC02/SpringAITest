using System.Text.Json;
using System.Text.Json.Nodes;
using Backend.Api.Agents;
using Backend.Api.Skills;

namespace Backend.Api.Contexts;

public static class ContextTaskEnvelopeProjection
{
    public static JsonElement? ApplyIfAvailable(JsonElement? callerEnvelope, ContextStoredRevision? stored, string viewType)
    {
        if (stored is not null) return Apply(callerEnvelope, stored, viewType);
        if (callerEnvelope is { ValueKind: JsonValueKind.Object } raw && raw.TryGetProperty("context_ref", out _)) throw new ArgumentException("Caller context_ref has no server-owned READY revision");
        return callerEnvelope;
    }

    public static JsonElement Apply(JsonElement? callerEnvelope, ContextStoredRevision stored, string viewType)
    {
        if (callerEnvelope is not { ValueKind: JsonValueKind.Object } raw || stored.Revision.ContextRef is not { } contextRef || string.IsNullOrWhiteSpace(stored.AdapterId)) throw new ArgumentException("A server-owned Context projection is required");
        if (viewType is not ("worker" or "verifier" or "synthesizer")) throw new ArgumentException("Context view role is invalid");
        var view = stored.Views.FirstOrDefault(x => x.ViewType == viewType) ?? throw new ArgumentException($"Context {viewType} view is unavailable");
        var roleContextRef = contextRef with { ViewId = view.ViewId };
        var envelope = JsonNode.Parse(raw.GetRawText())!.AsObject();
        if (envelope["context_ref"] is JsonObject expected)
        {
            var expectedId = expected["context_id"]?.GetValue<string>(); var expectedRevision = expected["revision"]?.GetValue<int>();
            if (expectedId != contextRef.ContextId.ToString("D") || expectedRevision != contextRef.Revision) throw new ArgumentException("task context_ref does not match the server projection");
        }
        envelope["context_ref"] = JsonSerializer.SerializeToNode(roleContextRef);
        envelope["context"] = JsonNode.Parse(view.Definition.GetRawText());
        var provenance = new JsonArray();
        foreach (var item in view.Definition.EnumerateObject())
        {
            provenance.Add(JsonSerializer.SerializeToNode(new { context_key = item.Name, source_type = "context-tool", source_id = stored.AdapterId, observed_at = stored.Revision.CreatedAt.ToString("O"), content_sha256 = SkillHash.Sha256(item.Value.GetRawText()) }));
        }
        envelope["context_provenance"] = provenance;
        return JsonSerializer.SerializeToElement(envelope);
    }
}
