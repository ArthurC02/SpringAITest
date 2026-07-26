using System.Text.Json;
using Backend.Api.Contexts;

namespace Backend.Api.Data.InMemory;

/// <summary>Lite-mode Context Store.  The lock preserves the database's immutable revisions and one-active-policy invariant.</summary>
public sealed class InMemoryContextRepository(
    InMemoryRagRepository? rag = null,
    IReadOnlyList<ContextSourceCatalogEntry>? contextSources = null,
    IReadOnlyCollection<string>? policyTenants = null) : IContextRepository, IContextAuthorityRegistry, IContextTaskLocalRegistry
{
    private sealed record StoredView(ContextViewResponse Response, byte[] Canonical, string Sha);
    private sealed record Revision(ContextRevisionResponse Response, byte[] Canonical, string Sha, IReadOnlyList<ContextEvidenceInput> Evidence, IReadOnlyList<StoredView> Views);
    private sealed record RootAuthority(byte[] Canonical, string Sha);
    private readonly object _store = new();
    private readonly Dictionary<(string Tenant, Guid ContextId, int Revision), Revision> _revisions = new();
    private readonly Dictionary<(string Tenant, Guid ViewId), StoredView> _views = new();
    private readonly Dictionary<string, ContextPolicyResponse> _policies = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Tenant, string User, Guid Root), RootAuthority> _rootSnapshots = new();
    private readonly HashSet<(string Tenant, Guid Root, Guid Context)> _taskContexts = [];
    private readonly IReadOnlyCollection<string> _policyTenants = policyTenants ?? ["demo-a", "demo-b"];

    public void RegisterRoot(string tenantId, string userId, Guid rootRunId, string canonicalSnapshot)
    { lock (_store) { var canonical = Backend.Api.Agents.AgentCanonicalizer.CanonicalizeDefinition(canonicalSnapshot); _rootSnapshots[(tenantId, userId, rootRunId)] = new(System.Text.Encoding.UTF8.GetBytes(canonical), ContextCanonicalizer.Hash(canonical)); } }

    /// <summary>Lite-mode stand-in for the `uq_context_policy_active` partial unique index: a tenant
    /// that already has an active policy rejects a second one instead of silently replacing it.</summary>
    public void SeedActivePolicy(string tenantId, JsonElement values, IReadOnlyList<ContextSourceCatalogEntry> sources)
    {
        lock (_store)
        {
            if (Policy(tenantId) is not null) throw new ArgumentException("tenant already has an active context policy");
            var now = DateTime.UtcNow;
            _policies[tenantId] = new(Guid.NewGuid(), "default", values, now, now, sources);
        }
    }
    public void RegisterTaskContext(string tenantId, Guid rootRunId, Guid contextId)
    { lock (_store) _taskContexts.Add((tenantId, rootRunId, contextId)); }

    public Task<ContextPolicyResponse?> GetActivePolicyAsync(string tenantId, CancellationToken ct)
    {
        lock (_store) { var policy = Policy(tenantId); if (policy is not null) ReadinessEvaluator.ValidatePolicy(policy.Values); return Task.FromResult(policy); }
    }

    public Task<ContextStoredRevision> CreateRevisionAsync(string tenantId, string userId, Guid contextId, ContextRevisionSubmitRequest request, CancellationToken ct)
    {
        var candidateCanonical = ContextCanonicalizer.CanonicalizeDefinition(request.Definition);
        var evidence = request.Evidence ?? Array.Empty<ContextEvidenceInput>();
        ContextCanonicalizer.ValidateEvidence(evidence);
        var views = request.Views ?? Array.Empty<ContextViewInput>();
        if (views.GroupBy(x => x.ViewType, StringComparer.Ordinal).Any(x => string.IsNullOrWhiteSpace(x.Key) || x.Count() != 1)) throw new ArgumentException("context views are invalid");
        lock (_store)
        {
            var policy = Policy(tenantId) ?? throw new ContextPolicyUnavailableException();
            if (_revisions.Keys.Any(x => x.ContextId == contextId && x.Tenant != tenantId)) throw new ArgumentException("context_id belongs to another tenant");
            // Root ownership is tenant + caller scoped, same as the PostgreSQL authority.
            string? rootSnapshot = null;
            if (request.RootRunId is Guid rootId)
            {
                if (!_rootSnapshots.TryGetValue((tenantId, userId, rootId), out var storedRoot)) throw new ArgumentException("root_run_id is not an owned root run");
                rootSnapshot = ContextCanonicalizer.ReadAuthoritative(storedRoot.Canonical, storedRoot.Sha, $"Root run {rootId:D}");
            }
            ContextSourceCatalogEntry? selectedSource = null;
            if (evidence.Count > 0)
            {
                if (rootSnapshot is null) throw new ArgumentException("context evidence requires an owned root run");
                using var authorityDocument = JsonDocument.Parse(rootSnapshot);
                var authority = authorityDocument.RootElement.GetProperty("authority");
                var allowed = authority.GetProperty("knowledge_sources").EnumerateArray().Select(x => x.GetString()!).ToHashSet(StringComparer.Ordinal);
                var tools = authority.GetProperty("context_tools").EnumerateArray().Select(x => x.GetString()!).ToHashSet(StringComparer.Ordinal);
                selectedSource = SelectSource(policy, tools);
                if (selectedSource is null || selectedSource.EvidenceType != "document") throw new ArgumentException("Context evidence adapter is unavailable or unauthorized");
                foreach (var item in evidence)
                {
                    ContextCanonicalizer.ValidateEvidencePin(item, selectedSource.SourceId, selectedSource.AdapterId, selectedSource.EvidenceType);
                    if (!Guid.TryParseExact(item.SourceId, "D", out var documentId) || !allowed.Contains(item.SourceId!) || !TryContentRef(item.ContentRef!, out var refDocument, out var chunkId) || documentId != refDocument || rag is null || !rag.ContextEvidenceMatchesAsync(tenantId, documentId, chunkId, item.ContentHash!, ct).GetAwaiter().GetResult()) throw new ArgumentException("Context evidence is not authorized by the root snapshot");
                }
            }
            var revision = _revisions.Keys.Where(x => x.Tenant == tenantId && x.ContextId == contextId).Select(x => x.Revision).DefaultIfEmpty().Max() + 1;
            var decision = ReadinessEvaluator.Evaluate(evidence, policy.Values, request.Measurements, selectedSource?.SourceId);
            if (decision.Status == ContextStatuses.ReadyWithAssumptions) ContextCanonicalizer.ValidateAssumptions(candidateCanonical, views, request.Measurements!.AssumptionsCount);
            var storedViews = views.Select(view =>
            {
                var viewId = Guid.NewGuid(); var definition = ContextCanonicalizer.CanonicalizeView(view.Definition);
                var dto = new ContextViewResponse(viewId, contextId, revision, view.ViewType!.Trim(), JsonDocument.Parse(definition).RootElement.Clone());
                var storedView = new StoredView(dto, System.Text.Encoding.UTF8.GetBytes(definition), ContextCanonicalizer.Hash(definition));
                _views[(tenantId, viewId)] = storedView; return storedView;
            }).ToList();
            var readyView = storedViews.FirstOrDefault(x => x.Response.ViewType == "planner") ?? storedViews.FirstOrDefault();
            var now = DateTime.UtcNow;
            selectedSource ??= rootSnapshot is null ? null : SelectSource(policy, JsonDocument.Parse(rootSnapshot).RootElement.GetProperty("authority").GetProperty("context_tools").EnumerateArray().Select(x => x.GetString()!).ToHashSet(StringComparer.Ordinal));
            var canonical = ContextCanonicalizer.PinAuthority(candidateCanonical, selectedSource?.SourceId, selectedSource?.AdapterId);
            var response = new ContextRevisionResponse(contextId, revision, request.RootRunId, decision.Status, decision.Readiness, decision.Unmet, policy.Id, JsonDocument.Parse(canonical).RootElement.Clone(), request.AsOf ?? now, now, request.ExpiresAt, ContextStatuses.IsReady(decision.Status) && readyView is not null ? new(contextId, revision, readyView.Response.ViewId) : null, selectedSource?.SourceId, selectedSource?.AdapterId);
            var stored = new Revision(response, System.Text.Encoding.UTF8.GetBytes(canonical), ContextCanonicalizer.Hash(canonical), evidence.ToList(), storedViews);
            _revisions[(tenantId, contextId, revision)] = stored;
            return Task.FromResult(ToStored(stored));
        }
    }

    public Task<ContextRevisionResponse?> GetRevisionAsync(string tenantId, Guid contextId, int revision, CancellationToken ct)
    {
        lock (_store) { var value = _revisions.GetValueOrDefault((tenantId, contextId, revision)); return Task.FromResult(value is null ? null : ToStored(value).Revision); }
    }

    public Task<ContextViewResponse?> GetViewAsync(string tenantId, Guid viewId, CancellationToken ct)
    {
        lock (_store) { var value = _views.GetValueOrDefault((tenantId, viewId)); return Task.FromResult(value is null ? null : ReadView(value)); }
    }

    public Task<ContextStoredRevision?> GetLatestReadyForRunAsync(string tenantId, string userId, Guid rootRunId, CancellationToken ct)
    {
        lock (_store)
        {
            if (!_rootSnapshots.ContainsKey((tenantId, userId, rootRunId))) return Task.FromResult<ContextStoredRevision?>(null);
            var found = _revisions.Where(x => x.Key.Tenant == tenantId && x.Value.Response.RootRunId == rootRunId && !_taskContexts.Contains((tenantId, rootRunId, x.Key.ContextId)) && ContextStatuses.IsReady(x.Value.Response.Status))
                .OrderByDescending(x => x.Value.Response.CreatedAt).Select(x => x.Value).FirstOrDefault();
            return Task.FromResult(found is null ? null : ToStored(found));
        }
    }

    private ContextPolicyResponse? Policy(string tenant)
    {
        if (_policies.TryGetValue(tenant, out var policy)) return policy;
        if (!_policyTenants.Contains(tenant, StringComparer.Ordinal)) return null;
        var now = DateTime.UtcNow;
        var sources = contextSources ?? [new("backend_documents", "document", "knowledge-source", "server-owned", "backend.retrieval_search", true, true, 15, 2)];
        var values = JsonSerializer.SerializeToElement(new
        {
            readiness = new { ready_threshold = 0.85m, assumptions_min = 0.70m, optional_failure_penalty = 0.10m },
            bootstrap_requirements = new[] { new { name = "document", evidence_type = "document", mandatory = true } },
            source_requirements = sources.Select((source, index) => new { source_id = source.SourceId, required = index == 0 }).ToArray(),
            source_precedence = sources.Select(source => source.SourceId).ToArray(),
        });
        policy = new(Guid.NewGuid(), "default", values, now, now, sources);
        _policies[tenant] = policy;
        return policy;
    }
    private static ContextStoredRevision ToStored(Revision stored)
    {
        var canonical = ContextCanonicalizer.ReadAuthoritative(stored.Canonical, stored.Sha, $"Context {stored.Response.ContextId:D} revision {stored.Response.Revision}");
        using var document = JsonDocument.Parse(canonical);
        var authority = document.RootElement.GetProperty("context_authority");
        if ((authority.GetProperty("selected_source_id").ValueKind == JsonValueKind.Null ? null : authority.GetProperty("selected_source_id").GetString()) != stored.Response.SelectedSourceId
            || (authority.GetProperty("adapter_id").ValueKind == JsonValueKind.Null ? null : authority.GetProperty("adapter_id").GetString()) != stored.Response.AdapterId) throw new InvalidOperationException("Context authority pin mismatch");
        var response = stored.Response with { Definition = JsonDocument.Parse(canonical).RootElement.Clone() };
        var views = stored.Views.Select(ReadView).ToList();
        return new(response, stored.Evidence, views, response.AdapterId);
    }
    private static ContextViewResponse ReadView(StoredView stored)
        => stored.Response with { Definition = JsonDocument.Parse(ContextCanonicalizer.ReadAuthoritative(stored.Canonical, stored.Sha, $"Context view {stored.Response.ViewId:D}")).RootElement.Clone() };
    private static ContextSourceCatalogEntry? SelectSource(ContextPolicyResponse policy, IReadOnlySet<string> tools)
    {
        if (!policy.Values.TryGetProperty("source_precedence", out var precedence) || precedence.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in precedence.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String))
        { var source = policy.Sources?.FirstOrDefault(x => x.SourceId == item.GetString() && x.Enabled && tools.Contains(x.AdapterId)); if (source is not null) return source; }
        return null;
    }

    private static bool TryContentRef(string value, out Guid documentId, out Guid chunkId)
    { documentId = default; chunkId = default; const string prefix = "document://"; const string marker = "#chunk/"; if (!value.StartsWith(prefix, StringComparison.Ordinal)) return false; var split = value.IndexOf(marker, StringComparison.Ordinal); return split > prefix.Length && Guid.TryParseExact(value[prefix.Length..split], "D", out documentId) && Guid.TryParseExact(value[(split + marker.Length)..], "D", out chunkId); }
}
