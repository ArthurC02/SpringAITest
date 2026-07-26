using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Backend.Api.Agents;
using Backend.Api.Skills;

namespace Backend.Api.Contexts;

internal static class ContextCanonicalizer
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public const int MaxDefinitionBytes = 256 * 1024;
    public const int MaxEvidenceBytes = 65_536;

    public static string CanonicalizeDefinition(JsonElement? definition)
    {
        if (definition is not { ValueKind: JsonValueKind.Object }) throw new ArgumentException("definition must be a JSON object");
        var raw = definition.Value.GetRawText();
        if (Encoding.UTF8.GetByteCount(raw) > MaxDefinitionBytes) throw new ArgumentException("definition exceeds 256 KB");
        return AgentCanonicalizer.CanonicalizeDefinition(raw);
    }

    public static string CanonicalizeView(JsonElement? definition)
    {
        if (definition is not { ValueKind: JsonValueKind.Object }) throw new ArgumentException("view definition must be a JSON object");
        var raw = definition.Value.GetRawText();
        if (Encoding.UTF8.GetByteCount(raw) > MaxEvidenceBytes) throw new ArgumentException("view definition exceeds 64 KB");
        return AgentCanonicalizer.CanonicalizeDefinition(raw);
    }

    public static void ValidateEvidence(IEnumerable<ContextEvidenceInput> evidence)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in evidence)
        {
            if (string.IsNullOrWhiteSpace(item.SourceId) || string.IsNullOrWhiteSpace(item.SnapshotId)
                || string.IsNullOrWhiteSpace(item.ContentRef) || !IsSha(item.ContentHash))
                throw new ArgumentException("context evidence is invalid");
            if (Encoding.UTF8.GetByteCount(item.ContentRef) > 1_024 || !seen.Add($"{item.SnapshotId}\u001f{item.ContentRef}"))
                throw new ArgumentException("context evidence is duplicated or too large");
            foreach (var json in new[] { item.Scope, item.Observations, item.Lineage })
                if (json is { } value && value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.Null))
                    throw new ArgumentException("context evidence JSON is invalid");
        }
    }

    public static bool IsSha(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>02-spec §2: READY_WITH_ASSUMPTIONS is only granted when the envelope really carries
    /// the assumptions it was measured with, and they are visible in the view the context_ref points
    /// at (planner for a root revision, the single role view for an E3 delta).  An inconsistent
    /// candidate is an invalid submission, not a silently downgraded one.</summary>
    public static void ValidateAssumptions(string candidateCanonical, IReadOnlyList<ContextViewInput> views, int assumptionsCount)
    {
        using var document = JsonDocument.Parse(candidateCanonical);
        if (assumptionsCount < 1
            || !document.RootElement.TryGetProperty("assumptions", out var assumptions)
            || assumptions.ValueKind != JsonValueKind.Array || assumptions.GetArrayLength() != assumptionsCount)
            throw new ArgumentException("assumptions must be non-empty and match assumptions_count");
        var readyView = views.FirstOrDefault(x => x.ViewType?.Trim() == "planner") ?? views.FirstOrDefault();
        if (readyView?.Definition is not { ValueKind: JsonValueKind.Object } view
            || !view.TryGetProperty("assumptions", out var projected)
            || projected.ValueKind != JsonValueKind.Array || projected.GetArrayLength() < 1)
            throw new ArgumentException("assumptions must appear in the planner view");
    }

    public static void ValidateEvidencePin(ContextEvidenceInput evidence, string catalogSourceId, string adapterId, string evidenceType)
    {
        if (!string.Equals(evidence.EvidenceType, evidenceType, StringComparison.Ordinal)
            || evidence.Lineage is not { ValueKind: JsonValueKind.Object } lineage
            || !lineage.TryGetProperty("catalog_source_id", out var source) || source.ValueKind != JsonValueKind.String
            || !lineage.TryGetProperty("adapter_id", out var adapter) || adapter.ValueKind != JsonValueKind.String
            || !string.Equals(source.GetString(), catalogSourceId, StringComparison.Ordinal)
            || !string.Equals(adapter.GetString(), adapterId, StringComparison.Ordinal))
            throw new ArgumentException("Context evidence lineage does not match the selected source");
    }
    public static string Hash(string canonical) => SkillHash.Sha256(canonical);

    public static string PinAuthority(string candidateCanonical, string? selectedSourceId, string? adapterId)
    {
        var definition = JsonNode.Parse(candidateCanonical)!.AsObject();
        definition["context_authority"] = new JsonObject { ["selected_source_id"] = selectedSourceId, ["adapter_id"] = adapterId };
        return AgentCanonicalizer.CanonicalizeDefinition(definition.ToJsonString());
    }

    public static string ReadAuthoritative(byte[]? bytes, string? sha, string authority)
    {
        if (bytes is null || !SkillHash.MatchesSha256(bytes, sha)) throw new InvalidOperationException($"{authority} canonical hash mismatch");
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) throw new InvalidOperationException($"{authority} canonical bytes contain a BOM");
        try
        {
            var json = StrictUtf8.GetString(bytes);
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !string.Equals(AgentCanonicalizer.CanonicalizeDefinition(json), json, StringComparison.Ordinal))
                throw new InvalidOperationException($"{authority} canonical bytes are invalid");
            return json;
        }
        catch (Exception ex) when (ex is DecoderFallbackException or JsonException)
        { throw new InvalidOperationException($"{authority} canonical bytes are invalid", ex); }
    }
}
