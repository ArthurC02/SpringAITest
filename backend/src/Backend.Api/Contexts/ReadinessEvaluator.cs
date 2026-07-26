using System.Text.Json;

namespace Backend.Api.Contexts;

/// <summary>Pure Backend authority. Missing/malformed policy never receives program defaults.</summary>
public static class ReadinessEvaluator
{
    public static void ValidatePolicy(JsonElement policy) => _ = PolicyRules.Parse(policy);
    public static (string Status, decimal Readiness, IReadOnlyList<string> Unmet) Evaluate(
        IReadOnlyList<ContextEvidenceInput> evidence, JsonElement policy, ContextObjectiveMeasurements? measurements,
        string? selectedSourceId = null)
    {
        var rules = PolicyRules.Parse(policy);
        // A bad candidate is the caller's fault (400); ContextPolicyInvalidException is reserved for a
        // malformed server-owned policy (422), which is not something the submitter can fix by retrying.
        if (measurements is null || measurements.ContextRound < 1 || measurements.MaxContextRounds < 1
            || measurements.ContextRound > measurements.MaxContextRounds || measurements.AssumptionsCount < 0)
            throw new ArgumentException("Objective context measurements are missing or invalid");

        var failures = measurements.SourceFailures ?? Array.Empty<ContextSourceFailure>();
        if (failures.Any(x => string.IsNullOrWhiteSpace(x.SourceId) || string.IsNullOrWhiteSpace(x.FailureCode)
                              || !rules.Sources.ContainsKey(x.SourceId!)))
            return (ContextStatuses.BlockedByPolicy, 0m, rules.Requirements.Where(x => x.Mandatory).Select(x => x.Name).ToArray());
        var failedSources = failures.Select(x => x.SourceId!).ToHashSet(StringComparer.Ordinal);
        var unmet = rules.Requirements.Where(x => x.Mandatory && !evidence.Any(e => e.EvidenceType == x.EvidenceType)).Select(x => x.Name)
            .Concat(rules.Sources.Where(x => x.Value.Required
                && (evidence.Count == 0 || selectedSourceId != x.Key || failedSources.Contains(x.Key))).Select(x => "source:" + x.Key))
            .Distinct(StringComparer.Ordinal).ToArray();
        if ((measurements.PolicyViolations?.Count ?? 0) > 0) return (ContextStatuses.BlockedByPolicy, 0m, unmet);
        if (measurements.CriticalAmbiguity) return (ContextStatuses.NeedsClarification, Score(evidence, rules, failures), unmet);

        var readiness = Score(evidence, rules, failures);
        if (unmet.Length > 0)
            return measurements.DeadlineExhausted || measurements.ContextRound >= measurements.MaxContextRounds
                ? (ContextStatuses.InsufficientData, readiness, unmet)
                : (ContextStatuses.NeedMoreContext, readiness, unmet);
        if (readiness >= rules.ReadyThreshold) return (ContextStatuses.Ready, readiness, Array.Empty<string>());
        if (readiness >= rules.AssumptionsMin && measurements.AssumptionsCount > 0)
            return (ContextStatuses.ReadyWithAssumptions, readiness, Array.Empty<string>());
        return measurements.DeadlineExhausted || measurements.ContextRound >= measurements.MaxContextRounds
            ? (ContextStatuses.InsufficientData, readiness, Array.Empty<string>())
            : (ContextStatuses.NeedMoreContext, readiness, Array.Empty<string>());
    }

    private static decimal Score(IReadOnlyList<ContextEvidenceInput> evidence, PolicyRules rules, IReadOnlyList<ContextSourceFailure> failures)
    {
        if (evidence.Count == 0) return 0m;
        var coveredRequirements = rules.Requirements.Count(requirement => evidence.Any(item => item.EvidenceType == requirement.EvidenceType));
        var objective = (decimal)coveredRequirements / rules.Requirements.Count;
        var optionalFailures = failures.Count(x => rules.Sources[x.SourceId!].Required == false);
        return Math.Clamp(objective - optionalFailures * rules.OptionalFailurePenalty, 0m, 1m);
    }

    private sealed record Requirement(string Name, string EvidenceType, bool Mandatory);
    private sealed record SourceRule(bool Required);
    private sealed record PolicyRules(decimal ReadyThreshold, decimal AssumptionsMin, decimal OptionalFailurePenalty, IReadOnlyList<Requirement> Requirements, IReadOnlyDictionary<string, SourceRule> Sources)
    {
        /// <summary>context_policy.values is per-key allow-listed like configuration_set: an unknown key
        /// is a rejected policy, never a silently ignored one.</summary>
        private static readonly IReadOnlySet<string> AllowedKeys = new HashSet<string>(StringComparer.Ordinal)
            { "readiness", "bootstrap_requirements", "source_requirements", "source_precedence" };

        public static PolicyRules Parse(JsonElement policy)
        {
            try
            {
                if (policy.ValueKind != JsonValueKind.Object || policy.EnumerateObject().Any(x => !AllowedKeys.Contains(x.Name))) throw new JsonException();
                var readiness = policy.GetProperty("readiness");
                var ready = readiness.GetProperty("ready_threshold").GetDecimal();
                var assumptions = readiness.GetProperty("assumptions_min").GetDecimal();
                var optionalPenalty = readiness.GetProperty("optional_failure_penalty").GetDecimal();
                if (ready is <= 0 or > 1 || assumptions is < 0 or > 1 || assumptions >= ready || optionalPenalty is < 0 or > 1) throw new JsonException();
                var requirements = policy.GetProperty("bootstrap_requirements").EnumerateArray().Select(x => new Requirement(x.GetProperty("name").GetString()!, x.GetProperty("evidence_type").GetString()!, x.GetProperty("mandatory").GetBoolean())).ToArray();
                var sources = policy.GetProperty("source_requirements").EnumerateArray().ToDictionary(x => x.GetProperty("source_id").GetString()!, x => new SourceRule(x.GetProperty("required").GetBoolean()), StringComparer.Ordinal);
                if (requirements.Length == 0 || sources.Count == 0 || requirements.Any(x => string.IsNullOrWhiteSpace(x.Name) || string.IsNullOrWhiteSpace(x.EvidenceType))) throw new JsonException();
                return new(ready, assumptions, optionalPenalty, requirements, sources);
            }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or JsonException or ArgumentException)
            { throw new ContextPolicyInvalidException("Active context policy is malformed", ex); }
        }
    }
}

public sealed class ContextPolicyInvalidException(string message, Exception? inner = null) : Exception(message, inner);
