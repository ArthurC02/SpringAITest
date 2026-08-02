using Backend.Api.Workflows;

namespace Backend.Api.Data.InMemory;

/// <summary>Lite/test parity implementation for the Workflow draft/revision aggregate.</summary>
public sealed class InMemoryWorkflowRepository : IWorkflowRepository
{
    private readonly Lock _gate = new();
    private readonly List<Entry> _entries = new();
    private static DateTime Now() => DateTime.UtcNow;
    internal Lock ReferenceSyncRoot => _gate;
    internal bool HasActivePublishedUnsafe(string tenant, Guid id, int revision)
    { var e = Find(tenant, id); return e is not null && e.Kind == "orchestrator" && e.Enabled && e.Published == revision && e.Revisions.Any(r => r.Number == revision && r.Status == "published"); }
    internal (string Definition, string Contract)? GetVisibleAgentRuntimeRevisionUnsafe(string tenant, Guid id, int revision)
    { var e = _entries.FirstOrDefault(x => x.Id == id && (x.Tenant == tenant || x.Tenant == Backend.Api.Agents.AgentDefaults.SystemTenant) && x.Enabled && x.Kind == "agent-runtime"); var row = e?.Revisions.FirstOrDefault(x => x.Number == revision); return row is null ? null : (row.Definition, row.Contract); }

    public Task<IReadOnlyList<WorkflowInfo>> ListAsync(string tenantId, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<WorkflowInfo>>(_entries.Where(x => x.Tenant == tenantId)
            .OrderBy(x => x.Name, StringComparer.Ordinal).Select(x => x.Info()).ToArray());
    }
    public Task<Workflow?> GetAsync(string tenantId, Guid id, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(Find(tenantId, id)?.Model());
    }
    public Task<WorkflowWriteResult> CreateAsync(string tenantId, string name, string kind, string definition, string uiMetadata, string createdBy, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_entries.Any(x => x.Tenant == tenantId && x.Name == name)) return Task.FromResult(new WorkflowWriteResult(WorkflowWriteStatus.Duplicate));
            var now = Now(); var entry = new Entry { Id = Guid.NewGuid(), Tenant = tenantId, Name = name, Kind = kind, Definition = definition, UiMetadata = uiMetadata, CreatedAt = now, UpdatedAt = now };
            _entries.Add(entry); return Task.FromResult(new WorkflowWriteResult(WorkflowWriteStatus.Success, entry.Model()));
        }
    }
    public Task<WorkflowWriteResult> UpdateDraftAsync(string tenantId, Guid id, long expectedVersion, string name, string definition, string uiMetadata, CancellationToken ct)
    {
        lock (_gate)
        {
            var e = Find(tenantId, id); if (e is null) return Task.FromResult(new WorkflowWriteResult(WorkflowWriteStatus.NotFound));
            if (e.SystemOwned) return Task.FromResult(new WorkflowWriteResult(WorkflowWriteStatus.SystemOwned));
            if (e.DraftVersion != expectedVersion) return Task.FromResult(new WorkflowWriteResult(WorkflowWriteStatus.VersionConflict));
            e.Name = name; e.Definition = definition; e.UiMetadata = uiMetadata; e.DraftVersion++; e.Validated = null; e.UpdatedAt = Now();
            return Task.FromResult(new WorkflowWriteResult(WorkflowWriteStatus.Success, e.Model()));
        }
    }
    public Task<bool> MarkValidatedAsync(string tenantId, Guid id, long version, string definition, string uiMetadata, CancellationToken ct)
    {
        lock (_gate)
        { var e = Find(tenantId, id); if (e is null || e.SystemOwned || e.DraftVersion != version) return Task.FromResult(false); e.Definition = definition; e.UiMetadata = uiMetadata; e.Validated = version; e.UpdatedAt = Now(); return Task.FromResult(true); }
    }
    public Task<WorkflowWriteResult> PublishAsync(string tenantId, Guid id, long expectedVersion, string definition, string uiMetadata, string compilerContractVersion, string createdBy, CancellationToken ct)
    {
        lock (_gate)
        {
            var e = Find(tenantId, id); if (e is null) return Task.FromResult(new WorkflowWriteResult(WorkflowWriteStatus.NotFound));
            if (e.SystemOwned) return Task.FromResult(new WorkflowWriteResult(WorkflowWriteStatus.SystemOwned));
            if (e.DraftVersion != expectedVersion || e.Validated != expectedVersion) return Task.FromResult(new WorkflowWriteResult(WorkflowWriteStatus.VersionConflict));
            if (e.Definition != definition || e.UiMetadata != uiMetadata) return Task.FromResult(new WorkflowWriteResult(WorkflowWriteStatus.VersionConflict));
            foreach (var r in e.Revisions.Where(x => x.Status == "published")) r.Status = "superseded";
            var revision = (e.Published ?? 0) + 1; e.Revisions.Add(new Revision { Number = revision, Definition = definition, UiMetadata = uiMetadata, DefinitionHash = WorkflowCanonicalizer.Hash(definition), UiHash = WorkflowCanonicalizer.Hash(uiMetadata), Contract = compilerContractVersion, CreatedBy = createdBy, CreatedAt = Now() });
            e.Published = revision; e.Validated = null; e.UpdatedAt = Now(); return Task.FromResult(new WorkflowWriteResult(WorkflowWriteStatus.Success, e.Model(), revision));
        }
    }
    public Task<IReadOnlyList<WorkflowRevisionInfo>> ListRevisionsAsync(string tenantId, Guid id, CancellationToken ct)
    { lock (_gate) { var e = Find(tenantId, id); return Task.FromResult<IReadOnlyList<WorkflowRevisionInfo>>(e?.Revisions.OrderByDescending(x => x.Number).Select(x => new WorkflowRevisionInfo(x.Number, x.Status, x.DefinitionHash, x.UiHash, x.Contract, x.CreatedBy, x.CreatedAt)).ToArray() ?? Array.Empty<WorkflowRevisionInfo>()); } }
    public Task<(string Definition, string UiMetadata)?> GetRevisionAsync(string tenantId, Guid id, int revision, CancellationToken ct)
    { lock (_gate) { var r = Find(tenantId, id)?.Revisions.FirstOrDefault(x => x.Number == revision); return Task.FromResult(r is null ? ((string, string)?)null : (r.Definition, r.UiMetadata)); } }
    public Task<WorkflowWriteResult> RestoreAsync(string tenantId, Guid id, int revision, string definition, string uiMetadata, string compilerContractVersion, string createdBy, CancellationToken ct)
    {
        lock (_gate) { var e = Find(tenantId, id); if (e is null || e.Revisions.All(x => x.Number != revision)) return Task.FromResult(new WorkflowWriteResult(WorkflowWriteStatus.NotFound)); if (e.SystemOwned) return Task.FromResult(new WorkflowWriteResult(WorkflowWriteStatus.SystemOwned)); foreach (var r in e.Revisions.Where(x => x.Status == "published")) r.Status = "superseded"; var next = (e.Published ?? 0) + 1; e.Revisions.Add(new Revision { Number = next, Definition = definition, UiMetadata = uiMetadata, DefinitionHash = WorkflowCanonicalizer.Hash(definition), UiHash = WorkflowCanonicalizer.Hash(uiMetadata), Contract = compilerContractVersion, CreatedBy = createdBy, CreatedAt = Now() }); e.Published = next; e.UpdatedAt = Now(); return Task.FromResult(new WorkflowWriteResult(WorkflowWriteStatus.Success, e.Model(), next)); }
    }
    public Task<bool> SetEnabledAsync(string tenantId, Guid id, bool enabled, CancellationToken ct)
    { lock (_gate) { var e = Find(tenantId, id); if (e is null || e.SystemOwned) return Task.FromResult(false); e.Enabled = enabled; e.UpdatedAt = Now(); return Task.FromResult(true); } }
    private Entry? Find(string tenant, Guid id) => _entries.FirstOrDefault(x => x.Tenant == tenant && x.Id == id);
    private sealed class Entry { public Guid Id; public string Tenant = ""; public string Name = ""; public string Kind = ""; public bool Enabled = true; public long DraftVersion = 1; public long? Validated; public int? Published; public string Definition = "{}"; public string UiMetadata = "{}"; public DateTime CreatedAt; public DateTime UpdatedAt; public bool SystemOwned = false; public List<Revision> Revisions = new(); public Workflow Model() => new(Id, Name, Kind, Enabled, DraftVersion, Validated, Published, Definition, UiMetadata, WorkflowCanonicalizer.Hash(Definition), WorkflowCanonicalizer.Hash(UiMetadata), CreatedAt, UpdatedAt, SystemOwned); public WorkflowInfo Info() => new(Id, Name, Kind, Enabled, DraftVersion, Validated, Published, CreatedAt, UpdatedAt, SystemOwned); }
    private sealed class Revision { public int Number; public string Status = "published"; public string Definition = "{}"; public string UiMetadata = "{}"; public string DefinitionHash = ""; public string UiHash = ""; public string Contract = ""; public string CreatedBy = ""; public DateTime CreatedAt; }
}
