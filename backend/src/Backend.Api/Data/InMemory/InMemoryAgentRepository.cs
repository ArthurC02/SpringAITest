using System.Text.Json.Nodes;
using Backend.Api.Agents;
using Backend.Api.Skills;

namespace Backend.Api.Data.InMemory;

/// <summary>
/// Agent aggregate 的行程記憶體實作(Lite 模式 / 測試 fake)。忠實模擬 Dapper 路徑的可觀測行為:
/// slug 於租戶內唯一(含已停用列)、draft optimistic concurrency、publish 固定 skill revision 與
/// definition hash、restore 產生新 revision 不改寫歷史、跨租戶不可見。稽核(revisions)只增不改。
/// </summary>
public sealed class InMemoryAgentRepository : IAgentRepository
{
    private readonly object _gate = new();
    private readonly List<Entry> _agents = new();
    private readonly InMemorySkillRepository _skills;

    public InMemoryAgentRepository(ISkillRepository skills)
    {
        _skills = skills as InMemorySkillRepository
            ?? throw new ArgumentException(
                "InMemoryAgentRepository 必須與 InMemorySkillRepository 共用一致性鎖",
                nameof(skills));
    }

    private static DateTime Now() => DateTime.UtcNow;

    public Task<IReadOnlyList<AgentInfo>> ListAsync(string tenantId, CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyList<AgentInfo> list = _agents
                .Where(a => a.Tenant == tenantId)
                .OrderBy(a => a.Slug, StringComparer.Ordinal)
                .Select(ToInfo)
                .ToList();
            return Task.FromResult(list);
        }
    }

    public Task<Agent?> GetAsync(string tenantId, Guid id, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(Find(tenantId, id)?.ToAgent());
        }
    }

    public Task<Agent?> CreateAsync(
        string tenantId, string slug, string name, string description,
        string canonicalDefinition, string definitionSha256, string createdBy, CancellationToken ct)
    {
        lock (_gate)
        {
            // slug 於租戶內唯一(含已停用列)— 對映 DB 的 UNIQUE(tenant_id, slug)。
            if (_agents.Any(a => a.Tenant == tenantId && a.Slug == slug))
            {
                return Task.FromResult<Agent?>(null);
            }

            var now = Now();
            var entry = new Entry
            {
                Id = Guid.NewGuid(),
                Tenant = tenantId,
                Slug = slug,
                Name = name,
                Description = description,
                Enabled = true,
                DraftVersion = 1,
                DraftValidatedVersion = null,
                PublishedRevision = null,
                DraftDefinition = canonicalDefinition,
                DraftDefinitionSha256 = definitionSha256,
                CreatedBy = createdBy,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _agents.Add(entry);
            return Task.FromResult<Agent?>(entry.ToAgent());
        }
    }

    public Task<AgentDraftResult> UpdateDraftAsync(
        string tenantId, Guid id, long expectedVersion, string name, string description,
        string canonicalDefinition, string definitionSha256, CancellationToken ct)
    {
        lock (_gate)
        {
            var entry = Find(tenantId, id);
            if (entry is null)
            {
                return Task.FromResult(new AgentDraftResult(AgentWriteStatus.NotFound, null));
            }

            if (entry.DraftVersion != expectedVersion)
            {
                return Task.FromResult(new AgentDraftResult(AgentWriteStatus.VersionConflict, null));
            }

            entry.Name = name;
            entry.Description = description;
            entry.DraftDefinition = canonicalDefinition;
            entry.DraftDefinitionSha256 = definitionSha256;
            entry.DraftVersion += 1;
            entry.DraftValidatedVersion = null; // 改過就要重新驗證
            entry.UpdatedAt = Now();
            return Task.FromResult(new AgentDraftResult(AgentWriteStatus.Success, entry.ToAgent()));
        }
    }

    public Task<bool> MarkValidatedAsync(
        string tenantId,
        Guid id,
        long version,
        string canonicalDefinition,
        string definitionSha256,
        CancellationToken ct)
    {
        lock (_gate)
        {
            var entry = Find(tenantId, id);
            if (entry is null || entry.DraftVersion != version)
            {
                return Task.FromResult(false);
            }

            entry.DraftDefinition = canonicalDefinition;
            entry.DraftDefinitionSha256 = definitionSha256;
            entry.DraftValidatedVersion = version;
            entry.UpdatedAt = Now();
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<AgentValidationError>> ValidateReferencesAsync(
        string tenantId, string canonicalDefinition, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_skills.ReferenceSyncRoot)
        {
            return Task.FromResult(ResolveReferencesUnsafe(tenantId, canonicalDefinition).Errors);
        }
    }

    public Task<AgentPublishResult> PublishAsync(
        string tenantId,
        Guid id,
        long expectedVersion,
        string canonicalDefinition,
        string definitionSha256,
        string createdBy,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // 固定 lock order：Skill snapshot → Agent aggregate。所有 Skill mutation 都持有同一把
        // ReferenceSyncRoot，因此 current revision/enable state 在解析到 Agent revision append 完成前
        // 不可能漂移；這是 Lite 路徑對 Dapper transaction + FOR SHARE 的等價 critical section。
        lock (_skills.ReferenceSyncRoot)
        {
            lock (_gate)
            {
                var entry = Find(tenantId, id);
                if (entry is null)
                {
                    return Task.FromResult(new AgentPublishResult(AgentWriteStatus.NotFound, 0));
                }

                if (entry.DraftVersion != expectedVersion
                    || entry.DraftValidatedVersion != expectedVersion)
                {
                    return Task.FromResult(new AgentPublishResult(
                        AgentWriteStatus.VersionConflict, 0));
                }

                var resolution = ResolveReferencesUnsafe(tenantId, canonicalDefinition);
                if (resolution.Errors.Count > 0)
                {
                    return Task.FromResult(new AgentPublishResult(
                        AgentWriteStatus.InvalidReference, 0, resolution.Errors));
                }

                entry.DraftDefinition = canonicalDefinition;
                entry.DraftDefinitionSha256 = definitionSha256;
                entry.UpdatedAt = Now();

                var (wfId, wfRev) = WorkflowRefOf(canonicalDefinition);
                var revision = AppendRevisionUnsafe(
                    entry, canonicalDefinition, definitionSha256, wfId, wfRev,
                    resolution.Bindings
                        .Select(b => new AgentRevisionSkillInfo(
                            b.SkillName, b.SkillRevision, b.Position, true))
                        .ToList(),
                    createdBy);
                // 清 validated:要求再驗證才能再發,避免同一 draft 重複 publish 產生重複 revision。
                entry.DraftValidatedVersion = null;
                return Task.FromResult(new AgentPublishResult(AgentWriteStatus.Success, revision));
            }
        }
    }

    public Task<IReadOnlyList<AgentRevisionInfo>> ListRevisionsAsync(string tenantId, Guid id, CancellationToken ct)
    {
        lock (_gate)
        {
            var entry = Find(tenantId, id);
            IReadOnlyList<AgentRevisionInfo> list = entry is null
                ? Array.Empty<AgentRevisionInfo>()
                : entry.Revisions.OrderByDescending(r => r.Revision).Select(r => r.ToInfo()).ToList();
            return Task.FromResult(list);
        }
    }

    public Task<string?> GetRevisionDefinitionAsync(
        string tenantId, Guid id, int revision, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var definition = Find(tenantId, id)?.Revisions
                .FirstOrDefault(r => r.Revision == revision)?
                .DefinitionSnapshot;
            return Task.FromResult(definition is null
                ? null
                : AgentCanonicalizer.CanonicalizeDefinition(definition));
        }
    }

    public Task<AgentPublishResult> RestoreAsync(
        string tenantId,
        Guid id,
        int revision,
        string canonicalDefinition,
        string definitionSha256,
        string createdBy,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var entry = Find(tenantId, id);
            if (entry is null)
            {
                return Task.FromResult(new AgentPublishResult(AgentWriteStatus.NotFound, 0));
            }

            var target = entry.Revisions.FirstOrDefault(r => r.Revision == revision);
            if (target is null)
            {
                return Task.FromResult(new AgentPublishResult(AgentWriteStatus.NotFound, 0));
            }

            // 舊 revision 與 pinned bindings 保持不可變；新 revision 使用 Workflow 本次重新
            // 驗證/正規化後的 definition/hash。
            var (workflowId, workflowRevision) = WorkflowRefOf(canonicalDefinition);
            var newRevision = AppendRevisionUnsafe(
                entry, canonicalDefinition, definitionSha256,
                workflowId, workflowRevision,
                target.Bindings.Select(b => b with { }).ToList(),
                createdBy);
            return Task.FromResult(new AgentPublishResult(AgentWriteStatus.Success, newRevision));
        }
    }

    public Task<bool> SetEnabledAsync(string tenantId, Guid id, bool enabled, CancellationToken ct)
    {
        lock (_gate)
        {
            var entry = Find(tenantId, id);
            if (entry is null)
            {
                return Task.FromResult(false);
            }

            entry.Enabled = enabled;
            entry.UpdatedAt = Now();
            return Task.FromResult(true);
        }
    }

    private Entry? Find(string tenantId, Guid id)
        => _agents.FirstOrDefault(a => a.Tenant == tenantId && a.Id == id);

    /// <summary>新增一筆 published revision,舊 published → superseded,並更新 published_revision 指標。</summary>
    private static int AppendRevisionUnsafe(
        Entry entry, string definitionSnapshot, string definitionSha256,
        Guid? workflowId, int? workflowRevision, List<AgentRevisionSkillInfo> bindings, string createdBy)
    {
        foreach (var r in entry.Revisions.Where(r => r.Status == "published"))
        {
            r.Status = "superseded";
        }

        var revision = (entry.PublishedRevision ?? 0) + 1;
        entry.Revisions.Add(new RevisionEntry
        {
            Revision = revision,
            Status = "published",
            DefinitionSnapshot = definitionSnapshot,
            DefinitionSha256 = definitionSha256,
            RuntimeWorkflowId = workflowId,
            RuntimeWorkflowRevision = workflowRevision,
            Bindings = bindings,
            CreatedBy = createdBy,
            CreatedAt = Now(),
        });
        entry.PublishedRevision = revision;
        entry.UpdatedAt = Now();
        return revision;
    }

    private static (Guid? Id, int? Revision) WorkflowRefOf(string canonicalDefinition)
    {
        var node = JsonNode.Parse(canonicalDefinition)?["runtime_workflow"];
        var id = Guid.TryParse(node?["id"]?.GetValue<string>(), out var g) ? g : (Guid?)null;
        var revision = node?["revision"]?.GetValue<int>();
        return (id, revision == 0 ? null : revision);
    }

    /// <summary>呼叫端必須持有 Skill ReferenceSyncRoot，確保 resolution 與 publish commit 同一快照。</summary>
    private ReferenceResolution ResolveReferencesUnsafe(
        string tenantId, string canonicalDefinition)
    {
        var errors = new List<AgentValidationError>();
        var resolved = new List<ResolvedSkillBinding>();
        var workflow = AgentCanonicalizer.WorkflowOf(canonicalDefinition);
        if (workflow.Id != AgentDefaults.RuntimeWorkflowId
            || workflow.Revision != AgentDefaults.RuntimeWorkflowRevision)
        {
            errors.Add(new AgentValidationError(
                "runtime_workflow",
                $"Lite 模式只存在 system-owned Default Agent-Runtime Workflow published rev{AgentDefaults.RuntimeWorkflowRevision}"));
        }

        var bindings = AgentCanonicalizer.SkillBindingsOf(canonicalDefinition);
        for (var position = 0; position < bindings.Count; position++)
        {
            var name = bindings[position].Skill!;
            if (!_skills.TryResolveEnabledCurrentRevisionUnsafe(
                    tenantId, name, out var currentRevision))
            {
                errors.Add(new AgentValidationError(
                    "skill_bindings",
                    $"Skill「{name}」沒有同 tenant、enabled、可固定的 persisted revision；"
                    + "builtin/catalog-only Skill 在 D1 不可綁定"));
                continue;
            }
            resolved.Add(new ResolvedSkillBinding(
                name, currentRevision, position, Guid.Empty));
        }

        return new ReferenceResolution(errors, resolved);
    }

    private static AgentInfo ToInfo(Entry e) => new(
        e.Id, e.Slug, e.Name, e.Description, e.Enabled, e.DraftVersion,
        e.DraftValidatedVersion, e.PublishedRevision, e.CreatedAt, e.UpdatedAt);

    private sealed class Entry
    {
        public Guid Id;
        public string Tenant = "";
        public string Slug = "";
        public string Name = "";
        public string Description = "";
        public bool Enabled;
        public long DraftVersion;
        public long? DraftValidatedVersion;
        public int? PublishedRevision;
        public string DraftDefinition = "{}";
        public string DraftDefinitionSha256 = "";
        public string CreatedBy = "";
        public DateTime CreatedAt;
        public DateTime UpdatedAt;
        public List<RevisionEntry> Revisions = new();

        public Agent ToAgent() => new(
            Id, Slug, Name, Description, Enabled, DraftVersion, DraftValidatedVersion,
            PublishedRevision, DraftDefinition, DraftDefinitionSha256, CreatedAt, UpdatedAt);
    }

    private sealed class RevisionEntry
    {
        public int Revision;
        public string Status = "published";
        public string DefinitionSnapshot = "{}";
        public string DefinitionSha256 = "";
        public Guid? RuntimeWorkflowId;
        public int? RuntimeWorkflowRevision;
        public List<AgentRevisionSkillInfo> Bindings = new();
        public string CreatedBy = "";
        public DateTime CreatedAt;

        public AgentRevisionInfo ToInfo() => new(
            Revision, Status, DefinitionSha256, RuntimeWorkflowId, RuntimeWorkflowRevision,
            Bindings.OrderBy(b => b.Position).ToList(), CreatedBy, CreatedAt);
    }

    private sealed record ReferenceResolution(
        IReadOnlyList<AgentValidationError> Errors,
        IReadOnlyList<ResolvedSkillBinding> Bindings);
}
