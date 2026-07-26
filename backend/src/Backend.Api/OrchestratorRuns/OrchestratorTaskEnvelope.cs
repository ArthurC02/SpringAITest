using System.Text;
using System.Text.Json;

namespace Backend.Api.OrchestratorRuns;

internal sealed record ValidatedOrchestratorTaskEnvelope(string Canonical, string Objective);

/// <summary>Single fail-closed wire contract for D5 child task authority.</summary>
internal static class OrchestratorTaskEnvelope
{
    /// <summary>
    /// The whole child-request contract in one place: both repositories and the controller call
    /// it, so a permanently malformed child is a 400 everywhere.  A 409 would tell Workflow to
    /// retry forever, and a 500 would not be classified as permanent at all.
    /// </summary>
    public static (string TaskId, string RunKind, ValidatedOrchestratorTaskEnvelope Envelope) ValidateChild(OrchestratorChildCreateRequest request)
    {
        var task = request.TaskId?.Trim(); var kind = request.RunKind?.Trim();
        if (string.IsNullOrWhiteSpace(task) || task.Length > 128 || task.Any(char.IsControl) || request.Attempt < 1 || kind is not ("worker" or "verifier") || request.WriteIntent) throw new ArgumentException("Child task is invalid or not read-only");
        return (task, kind, Validate(request.TaskEnvelope));
    }

    public static ValidatedOrchestratorTaskEnvelope Validate(JsonElement? raw)
    {
        if (raw is not { ValueKind: JsonValueKind.Object } value || value.GetRawText().Length > 65_536) throw new ArgumentException("task_envelope is required and too large");
        var allowed = new HashSet<string>(new[] { "objective", "required_capabilities", "context", "context_ref", "context_provenance", "write_intent", "delegation_depth", "repair_of" }, StringComparer.Ordinal); var names = value.EnumerateObject().Select(x => x.Name).ToArray();
        if (names.Any(x => !allowed.Contains(x)) || !new[] { "objective", "required_capabilities", "context", "context_provenance", "write_intent", "delegation_depth" }.All(names.Contains)) throw new ArgumentException("task_envelope has unsupported fields");
        var objective = value.GetProperty("objective"); if (objective.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(objective.GetString()) || objective.GetString()!.Length > 16_384) throw new ArgumentException("task objective is invalid");
        if (value.GetProperty("write_intent").ValueKind != JsonValueKind.False || value.GetProperty("delegation_depth").ValueKind != JsonValueKind.Number || !value.GetProperty("delegation_depth").TryGetInt32(out var depth) || depth != 0) throw new ArgumentException("task write/delegation authority is invalid");
        var capabilities = value.GetProperty("required_capabilities"); if (capabilities.ValueKind != JsonValueKind.Array || capabilities.GetArrayLength() > 100 || capabilities.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(x.GetString()) || x.GetString()!.Length > 128) || capabilities.EnumerateArray().Select(x => x.GetString()).Distinct(StringComparer.Ordinal).Count() != capabilities.GetArrayLength()) throw new ArgumentException("task capabilities are invalid");
        var context = value.GetProperty("context"); if (context.ValueKind != JsonValueKind.Object || context.GetRawText().Length > 65_536 || context.EnumerateObject().Any(x => new[] { "messages", "conversation", "conversation_history", "memory", "jwt", "token" }.Contains(x.Name, StringComparer.OrdinalIgnoreCase))) throw new ArgumentException("task context is invalid");
        if (value.TryGetProperty("context_ref", out var contextRef) && (contextRef.ValueKind != JsonValueKind.Object || !contextRef.TryGetProperty("context_id", out var contextId) || contextId.ValueKind != JsonValueKind.String || !Guid.TryParse(contextId.GetString(), out _) || !contextRef.TryGetProperty("revision", out var contextRevision) || !contextRevision.TryGetInt32(out var revision) || revision < 1 || !contextRef.TryGetProperty("view_id", out var viewId) || viewId.ValueKind != JsonValueKind.String || !Guid.TryParse(viewId.GetString(), out _))) throw new ArgumentException("task context_ref is invalid");
        var provenance = value.GetProperty("context_provenance"); if (provenance.ValueKind != JsonValueKind.Array || provenance.GetArrayLength() != context.EnumerateObject().Count()) throw new ArgumentException("task provenance is incomplete");
        var contextKeys = context.EnumerateObject().Select(x => x.Name).ToHashSet(StringComparer.Ordinal); var provenanceKeys = new HashSet<string>(StringComparer.Ordinal); foreach (var item in provenance.EnumerateArray()) { if (item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Select(x => x.Name).ToHashSet(StringComparer.Ordinal).SetEquals(new[] { "context_key", "source_type", "source_id", "observed_at", "content_sha256" }) == false || item.GetProperty("context_key").ValueKind != JsonValueKind.String || !contextKeys.Contains(item.GetProperty("context_key").GetString()!) || !provenanceKeys.Add(item.GetProperty("context_key").GetString()!) || item.GetProperty("source_type").ValueKind != JsonValueKind.String || item.GetProperty("source_type").GetString() is not ("caller" or "context-tool" or "knowledge-source") || item.GetProperty("source_id").ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetProperty("source_id").GetString()) || item.GetProperty("observed_at").ValueKind != JsonValueKind.String || item.GetProperty("content_sha256").ValueKind != JsonValueKind.String || !System.Text.RegularExpressions.Regex.IsMatch(item.GetProperty("content_sha256").GetString()!, "^[0-9a-f]{64}$")) throw new ArgumentException("task provenance is invalid"); }
        if (!contextKeys.SetEquals(provenanceKeys) || value.TryGetProperty("repair_of", out var repair) && repair.ValueKind is not (JsonValueKind.Null or JsonValueKind.String)) throw new ArgumentException("task provenance is invalid");
        return new(Canonicalize(value), objective.GetString()!);
    }

    private static string Canonicalize(JsonElement value) { using var stream = new MemoryStream(); using (var w = new Utf8JsonWriter(stream)) { WriteCanonical(w, value); } return Encoding.UTF8.GetString(stream.ToArray()); }
    private static void WriteCanonical(Utf8JsonWriter w, JsonElement value) { switch (value.ValueKind) { case JsonValueKind.Object: w.WriteStartObject(); foreach (var p in value.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal)) { w.WritePropertyName(p.Name); WriteCanonical(w, p.Value); } w.WriteEndObject(); break; case JsonValueKind.Array: w.WriteStartArray(); foreach (var item in value.EnumerateArray()) WriteCanonical(w, item); w.WriteEndArray(); break; default: value.WriteTo(w); break; } }
}
