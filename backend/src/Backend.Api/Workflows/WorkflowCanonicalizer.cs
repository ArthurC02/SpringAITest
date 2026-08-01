using System.Text.Json;
using System.Text.Json.Nodes;
using Backend.Api.Common;
using Backend.Api.Skills;

namespace Backend.Api.Workflows;

public static class WorkflowCanonicalizer
{
    public static string Canonicalize(JsonElement value) => Canonicalize(value.GetRawText());
    public static string Canonicalize(string value) => CanonicalJsonTree.Normalize(JsonNode.Parse(value))!.ToJsonString();
    public static string Hash(string value) => SkillHash.Sha256(value);

    public static IReadOnlyList<WorkflowValidationError> ValidateEnvelope(string kind, string definition, string uiMetadata)
    {
        var errors = new List<WorkflowValidationError>();
        if (kind is not ("orchestrator" or "agent-runtime"))
            errors.Add(new("kind", "kind must be orchestrator or agent-runtime"));
        try
        {
            using var graph = JsonDocument.Parse(definition);
            if (graph.RootElement.ValueKind != JsonValueKind.Object)
                errors.Add(new("definition", "definition must be a JSON object"));
            else
            {
                if (!graph.RootElement.TryGetProperty("schemaVersion", out var version) || version.ValueKind != JsonValueKind.Number)
                    errors.Add(new("definition.schemaVersion", "schemaVersion is required"));
                if (!graph.RootElement.TryGetProperty("kind", out var graphKind) || graphKind.GetString() != kind)
                    errors.Add(new("definition.kind", "Graph kind must match workflow kind"));
                if (!graph.RootElement.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
                    errors.Add(new("definition.nodes", "nodes is required"));
                if (!graph.RootElement.TryGetProperty("edges", out var edges) || edges.ValueKind != JsonValueKind.Array)
                    errors.Add(new("definition.edges", "edges is required"));
            }
            using var ui = JsonDocument.Parse(uiMetadata);
            if (ui.RootElement.ValueKind != JsonValueKind.Object)
                errors.Add(new("ui_metadata", "ui_metadata must be a JSON object"));
        }
        catch (JsonException) { errors.Add(new("definition", "definition and ui_metadata must be valid JSON")); }
        return errors;
    }

}
