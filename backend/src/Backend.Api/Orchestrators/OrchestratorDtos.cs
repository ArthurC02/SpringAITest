using System.Text.Json;
using System.Text.Json.Serialization;
using Backend.Api.Common;
using Backend.Api.Workflows;

namespace Backend.Api.Orchestrators;

public sealed record OrchestratorUpsert([property: JsonPropertyName("name")] string? Name, [property: JsonPropertyName("description")] string? Description, [property: JsonPropertyName("definition")] JsonElement Definition);
public sealed record OrchestratorPublishRequest([property: JsonPropertyName("expected_draft_version")] long? ExpectedDraftVersion);
public sealed record Orchestrator(Guid Id, string Name, string Description, bool Enabled, long DraftVersion, long? DraftValidatedVersion, int? PublishedRevision, string Definition, DateTime CreatedAt, DateTime UpdatedAt);
public sealed record OrchestratorInfo([property: JsonPropertyName("id")] Guid Id, [property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("description")] string Description, [property: JsonPropertyName("enabled")] bool Enabled, [property: JsonPropertyName("draft_version")] long DraftVersion, [property: JsonPropertyName("draft_validated_version")] long? DraftValidatedVersion, [property: JsonPropertyName("published_revision")] int? PublishedRevision, [property: JsonPropertyName("created_at")] DateTime CreatedAt, [property: JsonPropertyName("updated_at")] DateTime UpdatedAt);
public sealed record OrchestratorResponse([property: JsonPropertyName("id")] Guid Id, [property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("description")] string Description, [property: JsonPropertyName("enabled")] bool Enabled, [property: JsonPropertyName("draft_version")] long DraftVersion, [property: JsonPropertyName("draft_validated_version")] long? DraftValidatedVersion, [property: JsonPropertyName("published_revision")] int? PublishedRevision, [property: JsonPropertyName("definition")][property: JsonConverter(typeof(RawJsonConverter))] string Definition, [property: JsonPropertyName("created_at")] DateTime CreatedAt, [property: JsonPropertyName("updated_at")] DateTime UpdatedAt) { public static OrchestratorResponse From(Orchestrator x) => new(x.Id, x.Name, x.Description, x.Enabled, x.DraftVersion, x.DraftValidatedVersion, x.PublishedRevision, x.Definition, x.CreatedAt, x.UpdatedAt); }
public sealed record OrchestratorRevisionInfo([property: JsonPropertyName("revision")] int Revision, [property: JsonPropertyName("status")] string Status, [property: JsonPropertyName("definition_sha256")] string DefinitionSha256, [property: JsonPropertyName("created_by")] string CreatedBy, [property: JsonPropertyName("created_at")] DateTime CreatedAt);
public enum OrchestratorWriteStatus { Success, NotFound, VersionConflict, Duplicate }
public sealed record OrchestratorWriteResult(OrchestratorWriteStatus Status, Orchestrator? Orchestrator = null, int Revision = 0);
public interface IOrchestratorRepository
{
    Task<IReadOnlyList<OrchestratorInfo>> ListAsync(string tenant, CancellationToken ct); Task<Orchestrator?> GetAsync(string tenant, Guid id, CancellationToken ct);
    Task<OrchestratorWriteResult> CreateAsync(string tenant, string name, string description, string definition, string by, CancellationToken ct); Task<OrchestratorWriteResult> UpdateAsync(string tenant, Guid id, long version, string name, string description, string definition, CancellationToken ct);
    Task<IReadOnlyList<string>> ValidateReferencesAsync(string tenant, string definition, CancellationToken ct);
    Task<bool> MarkValidatedAsync(string tenant, Guid id, long version, string definition, CancellationToken ct); Task<OrchestratorWriteResult> PublishAsync(string tenant, Guid id, long version, string definition, string by, CancellationToken ct);
    Task<IReadOnlyList<OrchestratorRevisionInfo>> RevisionsAsync(string tenant, Guid id, CancellationToken ct); Task<string?> RevisionAsync(string tenant, Guid id, int revision, CancellationToken ct); Task<OrchestratorWriteResult> RestoreAsync(string tenant, Guid id, int revision, string definition, string by, CancellationToken ct); Task<bool> SetEnabledAsync(string tenant, Guid id, bool enabled, CancellationToken ct);
}
public static class OrchestratorCanonicalizer
{
    public const int MaxWorkers = 64, MaxContextTools = 128, MaxAudience = 256;
    public static string Canonicalize(JsonElement x) => Canonicalize(x.GetRawText());
    public static string Canonicalize(string x) => Backend.Api.Agents.AgentCanonicalizer.CanonicalizeDefinition(x);
    public static IReadOnlyList<string> Validate(string text)
    {
        var errors = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(text); var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return ["definition must be an object"];
            RejectUnknown(root, ["instructions", "policy", "workflow", "verifier", "workerPool", "workerPolicy", "context", "audience", "capabilities", "budgets"], "definition", errors);
            if (!root.TryGetProperty("instructions", out var instructions) || instructions.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(instructions.GetString())) errors.Add("definition.instructions is required");
            if (!root.TryGetProperty("policy", out var policy) || policy.ValueKind != JsonValueKind.Object) errors.Add("definition.policy must be an object");
            else
            {
                RejectUnknown(policy, ["dispatchMode", "joinPolicy", "repairPolicy", "aggregationPolicy", "denialPolicy"], "definition.policy", errors);
                ValidateEnum(policy, "dispatchMode", ["bounded-parallel"], "definition.policy", errors);
                ValidateEnum(policy, "joinPolicy", ["fail-fast", "allow-partial", "repair"], "definition.policy", errors);
                ValidateEnum(policy, "repairPolicy", ["redispatch", "fail"], "definition.policy", errors);
                ValidateEnum(policy, "aggregationPolicy", ["verified-only"], "definition.policy", errors);
                ValidateEnum(policy, "denialPolicy", ["fail-closed"], "definition.policy", errors);
            }
            if (!root.TryGetProperty("workerPolicy", out var workerPolicy) || workerPolicy.ValueKind != JsonValueKind.Object) errors.Add("definition.workerPolicy must be an object");
            else
            {
                RejectUnknown(workerPolicy, ["requiredAudience", "requiredCapabilities", "selection"], "definition.workerPolicy", errors);
                ValidateStringArray(workerPolicy, "requiredAudience", MaxAudience, errors, "definition.workerPolicy");
                ValidateStringArray(workerPolicy, "requiredCapabilities", MaxAudience, errors, "definition.workerPolicy");
                ValidateEnum(workerPolicy, "selection", ["pinned-only"], "definition.workerPolicy", errors);
            }
            ValidateRef(root, "workflow", "id", "revision", errors);
            ValidateRef(root, "verifier", "agentId", "revision", errors);
            if (root.TryGetProperty("workflow", out var workflow) && workflow.ValueKind == JsonValueKind.Object) RejectUnknown(workflow, ["id", "revision"], "definition.workflow", errors);
            if (root.TryGetProperty("verifier", out var verifier))
            {
                if (verifier.ValueKind == JsonValueKind.Object) RejectUnknown(verifier, ["agentId", "revision", "variant", "independent", "outputContract"], "definition.verifier", errors);
                if (!verifier.TryGetProperty("variant", out var variant) || variant.GetString() != "read-only") errors.Add("definition.verifier.variant must be read-only");
                if (!verifier.TryGetProperty("outputContract", out var contract) || contract.ValueKind != JsonValueKind.Object || !contract.TryGetProperty("type", out var type) || type.GetString() != "verification-report") errors.Add("definition.verifier.outputContract must be verification-report");
                else RejectUnknown(contract, ["type"], "definition.verifier.outputContract", errors);
                if (!verifier.TryGetProperty("independent", out var independent) || independent.ValueKind != JsonValueKind.True) errors.Add("definition.verifier.independent must be true");
            }
            if (!root.TryGetProperty("workerPool", out var workers) || workers.ValueKind != JsonValueKind.Array || workers.GetArrayLength() is < 1 or > MaxWorkers) errors.Add($"definition.workerPool must contain 1..{MaxWorkers} pinned workers");
            else foreach (var worker in workers.EnumerateArray()) { ValidateRefElement(worker, "workerPool", "agentId", "revision", errors); if (worker.ValueKind == JsonValueKind.Object) RejectUnknown(worker, ["agentId", "revision"], "definition.workerPool[]", errors); }
            if (root.TryGetProperty("verifier", out var verifierRef) && Guid.TryParse(verifierRef.GetProperty("agentId").GetString(), out var verifierId) && root.TryGetProperty("workerPool", out workers) && workers.ValueKind == JsonValueKind.Array && workers.EnumerateArray().Any(w => Guid.TryParse(w.GetProperty("agentId").GetString(), out var workerId) && workerId == verifierId)) errors.Add("definition.verifier must not appear in workerPool");
            if (!root.TryGetProperty("context", out var context) || context.ValueKind != JsonValueKind.Object) errors.Add("definition.context is required");
            else
            {
                RejectUnknown(context, ["readOnly", "allowedTools", "knowledgeSources"], "definition.context", errors);
                if (!context.TryGetProperty("readOnly", out var readOnly) || readOnly.ValueKind != JsonValueKind.True) errors.Add("definition.context.readOnly must be true");
                ValidateStringArray(context, "allowedTools", MaxContextTools, errors);
                ValidateStringArray(context, "knowledgeSources", MaxContextTools, errors);
            }
            ValidateStringArray(root, "audience", MaxAudience, errors); ValidateStringArray(root, "capabilities", MaxAudience, errors);
            if (!root.TryGetProperty("budgets", out var budgets) || budgets.ValueKind != JsonValueKind.Object) errors.Add("definition.budgets is required");
            else
            {
                var limits = new[] { ("maxContextRounds", 100), ("maxTasks", 1000), ("maxChildRuns", 1000), ("maxConcurrency", 64), ("maxRepairRounds", 100), ("tokenBudget", 10000000), ("timeoutSeconds", 86400) };
                RejectUnknown(budgets, limits.Select(x => x.Item1).ToArray(), "definition.budgets", errors);
                foreach (var (name, max) in limits) if (!budgets.TryGetProperty(name, out var value) || !value.TryGetInt32(out var number) || number < 1 || number > max) errors.Add($"definition.budgets.{name} must be 1..{max}");
                if (budgets.TryGetProperty("maxTasks", out var tasks) && tasks.TryGetInt32(out var maxTasks)
                    && budgets.TryGetProperty("maxChildRuns", out var children) && children.TryGetInt32(out var maxChildRuns)
                    && maxChildRuns < maxTasks + 1)
                {
                    // Each worker task consumes a child slot and the first verification pass
                    // needs one additional slot. Repairs are runtime-gated against the same
                    // authoritative ceiling rather than admitted by an impossible definition.
                    errors.Add("definition.budgets.maxChildRuns must be at least maxTasks + 1 for the verifier reserve");
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { errors.Add("definition must be valid typed JSON"); }
        return errors;
    }
    public static IReadOnlyList<OrchestratorAgentRef> AgentRefs(string text) { using var d = JsonDocument.Parse(text); var r = d.RootElement; var result = new List<OrchestratorAgentRef>(); var v = r.GetProperty("verifier"); result.Add(new(Guid.Parse(v.GetProperty("agentId").GetString()!), v.GetProperty("revision").GetInt32(), true)); foreach (var w in r.GetProperty("workerPool").EnumerateArray()) result.Add(new(Guid.Parse(w.GetProperty("agentId").GetString()!), w.GetProperty("revision").GetInt32(), false)); return result; }
    public static IReadOnlyList<string> ValidateContextTools(string text, IReadOnlyList<WorkflowToolInfo> catalog) { using var d = JsonDocument.Parse(text); var tools = d.RootElement.GetProperty("context").GetProperty("allowedTools").EnumerateArray().Select(x => x.GetString()!).ToArray(); var byName = catalog.GroupBy(x => x.Name, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.Ordinal); var errors = new List<string>(); foreach (var name in tools) { if (!byName.TryGetValue(name, out var matches) || matches.Length != 1) { errors.Add($"context tool {name} is unknown or ambiguous"); continue; } if (matches[0].Risk is not ("read" or "low")) errors.Add($"context tool {name} risk must be read or low"); } return errors; }
    private static void ValidateRef(JsonElement root, string field, string id, string revision, List<string> errors) { if (!root.TryGetProperty(field, out var value)) { errors.Add($"definition.{field} is required"); return; } ValidateRefElement(value, field, id, revision, errors); }
    private static void ValidateRefElement(JsonElement value, string field, string id, string revision, List<string> errors) { if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(id, out var idValue) || !Guid.TryParseExact(idValue.GetString(), "D", out _) || !value.TryGetProperty(revision, out var rev) || !rev.TryGetInt32(out var n) || n < 1) errors.Add($"definition.{field} must contain a canonical pinned {id}/revision"); }
    private static void ValidateStringArray(JsonElement root, string name, int max, List<string> errors, string path = "definition") { if (!root.TryGetProperty(name, out var values) || values.ValueKind != JsonValueKind.Array || values.GetArrayLength() > max || values.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(x.GetString())) || values.EnumerateArray().Select(x => x.GetString()).Distinct(StringComparer.Ordinal).Count() != values.GetArrayLength()) errors.Add($"{path}.{name} must be a bounded unique string array"); }
    private static void ValidateEnum(JsonElement root, string name, IReadOnlyCollection<string> values, string path, List<string> errors) { if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || !values.Contains(value.GetString()!)) errors.Add($"{path}.{name} must be one of: {string.Join(", ", values)}"); }
    private static void RejectUnknown(JsonElement value, IReadOnlyCollection<string> allowed, string path, List<string> errors) { foreach (var property in value.EnumerateObject()) if (!allowed.Contains(property.Name)) errors.Add($"{path}.{property.Name} is not allowed"); }
}
public sealed record OrchestratorAgentRef(Guid AgentId, int Revision, bool Verifier);
public static class OrchestratorReferencePolicy
{
    public const string SupportedCompilerContract = WorkflowCompilerContracts.Current;
    public static IReadOnlyList<string> ValidateAgentDefinition(string definition, OrchestratorAgentRef reference)
    {
        var errors = new List<string>(); using var doc = JsonDocument.Parse(definition); var root = doc.RootElement; var roles = root.GetProperty("execution_roles").EnumerateArray().Select(x => x.GetString()).ToArray();
        if (reference.Verifier)
        {
            if (!roles.Contains("verifier", StringComparer.Ordinal)) errors.Add("verifier Agent revision must have verifier execution role");
            if (root.GetProperty("allowed_tools").GetArrayLength() != 0) errors.Add("verifier Agent revision must be read-only");
            if (!root.TryGetProperty("output_contract", out var contract) || contract.ValueKind != JsonValueKind.Object || !contract.TryGetProperty("type", out var type) || type.GetString() != "verification-report") errors.Add("verifier Agent revision must output verification-report");
        }
        else if (!roles.Contains("worker", StringComparer.Ordinal)) errors.Add("worker Agent revision must have worker execution role");
        return errors;
    }
    public static IReadOnlyList<string> ValidateRuntimeWorkflowProjection(string definition, Guid? projectedId, int? projectedRevision)
    {
        using var doc = JsonDocument.Parse(definition);
        if (!doc.RootElement.TryGetProperty("runtime_workflow", out var reference)
           || !Guid.TryParse(reference.GetProperty("id").GetString(), out var canonicalId)
           || !reference.GetProperty("revision").TryGetInt32(out var canonicalRevision)
           || canonicalId != projectedId || canonicalRevision != projectedRevision)
            return ["Agent canonical runtime_workflow does not match immutable revision projection"];
        return [];
    }
    public static IReadOnlyList<string> ValidateVerifierWorkflowDefinition(string definition, string? compilerContract)
    {
        if (compilerContract != SupportedCompilerContract) return ["verifier Agent runtime Workflow compiler contract is unsupported or missing"];
        try
        {
            using var doc = JsonDocument.Parse(definition); var root = doc.RootElement; var errors = new List<string>();
            if (root.ValueKind != JsonValueKind.Object || root.GetProperty("schemaVersion").GetInt32() != 1 || root.GetProperty("kind").GetString() != "agent-runtime" || root.GetProperty("runtimeVariant").GetString() != "verifier") errors.Add("verifier Agent runtime Workflow must be compiled Graph IR v1 with runtimeVariant verifier");
            var governance = root.GetProperty("governance"); if (!governance.GetProperty("maxSteps").TryGetInt32(out var maxSteps) || maxSteps < 1 || !governance.GetProperty("maxConcurrency").TryGetInt32(out var concurrency) || concurrency < 1) errors.Add("verifier Agent runtime Workflow governance is invalid");
            var nodes = root.GetProperty("nodes").EnumerateArray().ToArray(); var ids = nodes.Select(x => x.GetProperty("id").GetString()!).ToArray(); if (ids.Length == 0 || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length) errors.Add("verifier Agent runtime Workflow nodes are missing or duplicated");
            var types = nodes.Select(x => x.GetProperty("type").GetString()!).ToArray(); foreach (var required in new[] { "start", "dependency_and_capability_preflight", "inject_authorized_context", "checkpoint", "bounded_agent_loop", "validate_structured_output", "bounded_repair_or_controlled_failure", "end" }) if (!types.Contains(required, StringComparer.Ordinal)) errors.Add($"verifier Agent runtime Workflow is missing required stage {required}");
            var allTypes = new List<string>(types); foreach (var node in nodes) if (node.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array) allTypes.AddRange(children.EnumerateArray().Select(x => x.GetProperty("type").GetString()!));
            foreach (var forbidden in new[] { "load_skill", "tool_policy_and_approval_gate", "tool_call_and_observation" }) if (allTypes.Contains(forbidden, StringComparer.Ordinal)) errors.Add($"verifier Agent runtime Workflow contains forbidden worker stage {forbidden}");
            var edges = root.GetProperty("edges").EnumerateArray().Select(x => (Source: x.GetProperty("source").GetProperty("nodeId").GetString()!, Target: x.GetProperty("target").GetProperty("nodeId").GetString()!)).ToArray(); if (edges.Any(x => !ids.Contains(x.Source, StringComparer.Ordinal) || !ids.Contains(x.Target, StringComparer.Ordinal))) errors.Add("verifier Agent runtime Workflow edge references an unknown node");
            if (types.Count(x => x == "start") != 1 || types.Count(x => x == "end") != 1) errors.Add("verifier Agent runtime Workflow requires exactly one start and end");
            else if (edges.All(x => ids.Contains(x.Source, StringComparer.Ordinal) && ids.Contains(x.Target, StringComparer.Ordinal)))
            {
                var start = nodes.Single(x => x.GetProperty("type").GetString() == "start").GetProperty("id").GetString()!; var end = nodes.Single(x => x.GetProperty("type").GetString() == "end").GetProperty("id").GetString()!;
                var forward = ids.ToDictionary(x => x, _ => new List<string>(), StringComparer.Ordinal); var reverse = ids.ToDictionary(x => x, _ => new List<string>(), StringComparer.Ordinal); foreach (var edge in edges) { forward[edge.Source].Add(edge.Target); reverse[edge.Target].Add(edge.Source); }
                if (!ids.ToHashSet(StringComparer.Ordinal).SetEquals(Reachable(start, forward)) || !ids.ToHashSet(StringComparer.Ordinal).SetEquals(Reachable(end, reverse))) errors.Add("verifier Agent runtime Workflow contains unreachable or dead-end nodes");
            }
            return errors;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException) { return ["verifier Agent runtime Workflow compiled definition is semantically invalid"]; }
    }
    private static HashSet<string> Reachable(string start, IReadOnlyDictionary<string, List<string>> graph) { var seen = new HashSet<string>(StringComparer.Ordinal); var pending = new Stack<string>(); pending.Push(start); while (pending.TryPop(out var current) && seen.Add(current)) foreach (var next in graph[current]) pending.Push(next); return seen; }
}
