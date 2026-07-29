using Backend.Api.Skills;

namespace Backend.Api.PromptArtifacts;

/// <summary>
/// Lite-mode / test parity for the prompt artifact authority. Same tenant scoping, idempotency and
/// immutability semantics as <see cref="PromptArtifactRepository"/>; raw content stays in the
/// private entry and is never part of any returned record.
/// </summary>
public sealed class InMemoryPromptArtifactRepository : IPromptArtifactRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Tenant, string Kind), List<ComponentEntry>> _components = new();
    private readonly Dictionary<string, List<PromptManifestRecord>> _manifests = new();

    public Task<PromptComponentRecord> PublishComponentAsync(
        string tenantId, string kind, string content, string createdBy, CancellationToken ct)
    {
        lock (_gate)
        {
            var sha = SkillHash.Sha256(content);
            if (!_components.TryGetValue((tenantId, kind), out var revisions))
            {
                revisions = [];
                _components[(tenantId, kind)] = revisions;
            }

            var current = revisions.Count == 0 ? null : revisions[^1];
            if (current is not null && string.Equals(current.Record.ContentSha256, sha, StringComparison.Ordinal))
            {
                return Task.FromResult(current.Record);
            }

            var entry = new ComponentEntry(
                content,
                new PromptComponentRecord(
                    kind, (current?.Record.Revision ?? 0) + 1, sha, content.Length, createdBy, DateTime.UtcNow));
            revisions.Add(entry);
            return Task.FromResult(entry.Record);
        }
    }

    public Task<IReadOnlyList<PromptComponentRecord>> ListComponentsAsync(string tenantId, CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyList<PromptComponentRecord> result = _components
                .Where(kv => kv.Key.Tenant == tenantId)
                .OrderBy(kv => kv.Key.Kind, StringComparer.Ordinal)
                .SelectMany(kv => kv.Value.OrderByDescending(e => e.Record.Revision).Select(e => e.Record))
                .ToList();
            return Task.FromResult(result);
        }
    }

    public Task<PromptComponentRecord?> GetComponentAsync(
        string tenantId, string kind, int revision, CancellationToken ct)
    {
        lock (_gate)
        {
            var record = _components.TryGetValue((tenantId, kind), out var revisions)
                ? revisions.FirstOrDefault(e => e.Record.Revision == revision)?.Record
                : null;
            return Task.FromResult(record);
        }
    }

    public Task<PromptManifestRecord> CreateManifestAsync(
        string tenantId, string manifestCanonical, string createdBy, CancellationToken ct)
    {
        lock (_gate)
        {
            var sha = SkillHash.Sha256(manifestCanonical);
            if (!_manifests.TryGetValue(tenantId, out var revisions))
            {
                revisions = [];
                _manifests[tenantId] = revisions;
            }

            var current = revisions.Count == 0 ? null : revisions[^1];
            if (current is not null && string.Equals(current.ManifestSha256, sha, StringComparison.Ordinal))
            {
                return Task.FromResult(current);
            }

            var record = new PromptManifestRecord(
                (current?.Revision ?? 0) + 1, manifestCanonical, sha, createdBy, DateTime.UtcNow);
            revisions.Add(record);
            return Task.FromResult(record);
        }
    }

    public Task<IReadOnlyList<PromptManifestRecord>> ListManifestsAsync(string tenantId, CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyList<PromptManifestRecord> result = _manifests.TryGetValue(tenantId, out var revisions)
                ? revisions.OrderByDescending(r => r.Revision).ToList()
                : [];
            return Task.FromResult(result);
        }
    }

    public Task<PromptManifestRecord?> GetManifestAsync(string tenantId, int revision, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(_manifests.TryGetValue(tenantId, out var revisions)
                ? revisions.FirstOrDefault(r => r.Revision == revision)
                : null);
        }
    }

    public Task<PromptManifestRecord?> FindManifestBySha256Async(
        string tenantId, string manifestSha256, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(_manifests.TryGetValue(tenantId, out var revisions)
                ? revisions.OrderByDescending(r => r.Revision)
                    .FirstOrDefault(r => string.Equals(r.ManifestSha256, manifestSha256, StringComparison.Ordinal))
                : null);
        }
    }

    private sealed record ComponentEntry(string Content, PromptComponentRecord Record);
}
