using System.Collections.Concurrent;
using Backend.Api.Skills;

namespace Backend.Api.Data.InMemory;

/// <summary>
/// Skill 儲存庫的行程記憶體實作(Lite 模式)。key = (租戶, 名稱) — 忠實模擬 DB 的 UNIQUE (tenant_id, name)
/// 與租戶過濾。忠實模擬三件事實:軟刪(enabled=false,列仍在;清單/單筆看不到)、revision 遞增、
/// skill_revision 永不刪且軟刪後仍查得到。稽核表以 lock 保護;時間戳走 DateTime.UtcNow。
/// </summary>
public sealed class InMemorySkillRepository : ISkillRepository
{
    private readonly ConcurrentDictionary<(string Tenant, string Name), Skill> _store = new();
    private readonly object _gate = new();

    /// <summary>
    /// Agent publish 在 Lite 模式必須把「解析 current revision」與「寫入 Agent pin」放在同一個
    /// critical section。只供同 assembly 的 InMemoryAgentRepository 協調，不是公開 repository 契約。
    /// </summary>
    internal object ReferenceSyncRoot => _gate;

    /// <summary>稽核表:只增不減(軟刪不動它)。</summary>
    private readonly List<(string Tenant, string Name, StoredSkillRevision Row)> _revisions = new();

    private static DateTime Now() => DateTime.UtcNow;

    public Task<IReadOnlyList<SkillInfo>> ListAsync(string tenantId, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<SkillInfo>>(
                _store.Where(e => e.Key.Tenant == tenantId).Select(e => e.Value).Where(s => s.Enabled)
                .OrderBy(s => s.Name, StringComparer.Ordinal)
                .Select(s => new SkillInfo(
                    s.Name, s.Description, s.RequiredRole, s.Enabled, s.CurrentRevision,
                    s.CreatedAt, s.UpdatedAt, s.Kind, s.SimpleForm))
                .ToList());
        }
    }

    public Task<Skill?> GetAsync(string tenantId, string name, CancellationToken ct)
    {
        lock (_gate)
        {
            var skill = _store.GetValueOrDefault((tenantId, name));
            // 軟刪後不可見(WHERE ... AND enabled)。
            return Task.FromResult(skill is { Enabled: true } ? skill : null);
        }
    }

    /// <summary>
    /// 忠實模擬 ON CONFLICT (tenant_id, name) DO UPDATE ... WHERE NOT skill.enabled:
    /// 同名仍啟用 → 0 列 → null(不寫 revision);同名已軟刪 → 復活並把 revision 接著加。
    /// </summary>
    public Task<Skill?> CreateAsync(string tenantId, Skill skill, string createdBy, CancellationToken ct)
    {
        lock (_gate)
        {
            var now = Now();
            if (_store.TryGetValue((tenantId, skill.Name), out var existing))
            {
                if (existing.Enabled)
                {
                    return Task.FromResult<Skill?>(null);
                }

                // 軟刪的名字可以重用:同一列復活,稽核鏈不斷號。
                // simple_form 忠實模擬 COALESCE(EXCLUDED, skill):復活不帶 simpleForm → 保留軟刪前的值。
                var revived = skill with
                {
                    Kind = "flow",
                    Package = null,
                    CurrentRevision = existing.CurrentRevision + 1,
                    Enabled = true,
                    CreatedAt = existing.CreatedAt,
                    UpdatedAt = now,
                    SimpleForm = skill.SimpleForm ?? existing.SimpleForm,
                };
                _store[(tenantId, skill.Name)] = revived;
                AddRevisionUnsafe(tenantId, revived, createdBy);
                return Task.FromResult<Skill?>(revived);
            }

            var stored = skill with
            {
                Kind = "flow", Package = null, CurrentRevision = 1, Enabled = true,
                CreatedAt = now, UpdatedAt = now,
            };
            _store[(tenantId, skill.Name)] = stored;
            AddRevisionUnsafe(tenantId, stored, createdBy);
            return Task.FromResult<Skill?>(stored);
        }
    }

    public Task<Skill?> UpdateAsync(string tenantId, string name, Skill skill, string updatedBy, CancellationToken ct)
    {
        lock (_gate)
        {
            // 已軟刪的 skill 不可經 PUT 復活(WHERE ... AND enabled)。
            if (!_store.TryGetValue((tenantId, name), out var existing) || !existing.Enabled)
            {
                return Task.FromResult<Skill?>(null);
            }

            // definition-only PUT 建立新的 flow 作者來源，舊匯入 package 已與新 definition 不一致，必須清除。
            // simple_form 忠實模擬 COALESCE(@SimpleForm, simple_form):進階編輯器不帶 → 保留既有,不清空。
            var stored = skill with
            {
                Name = name,
                Kind = "flow",
                Enabled = true,
                CurrentRevision = existing.CurrentRevision + 1,
                CreatedAt = existing.CreatedAt,
                UpdatedAt = Now(),
                Package = null,
                SimpleForm = skill.SimpleForm ?? existing.SimpleForm,
            };
            _store[(tenantId, name)] = stored;
            AddRevisionUnsafe(tenantId, stored, updatedBy);
            return Task.FromResult<Skill?>(stored);
        }
    }

    /// <summary>
    /// Agent Skill 匯入 upsert(建立 / 更新 / 復活),忠實模擬 Dapper 的 ON CONFLICT DO UPDATE(**無 WHERE**):
    /// 對仍啟用的 skill 也直接覆寫、永遠 bump revision。flow/agentic 匯入都保存原 package。
    /// definition + package + 兩個 hash 一併落地(package_sha256 記入 revision)。
    /// </summary>
    public Task<Skill?> ImportAsync(
        string tenantId, Skill skill, byte[]? package, string? packageSha256, string createdBy, CancellationToken ct)
    {
        lock (_gate)
        {
            // simple_form 不帶不清:import 與 restore 都不動表單狀態(對映 SQL 不列入該欄)。
            // 新建 → NULL;覆寫既有 → 保留既有值(忽略傳入 skill.SimpleForm,匯入品無表單狀態)。
            var now = Now();
            if (_store.TryGetValue((tenantId, skill.Name), out var existing))
            {
                var updated = skill with
                {
                    Package = package?.ToArray(),
                    CurrentRevision = existing.CurrentRevision + 1,
                    Enabled = true,
                    CreatedAt = existing.CreatedAt,
                    UpdatedAt = now,
                    SimpleForm = existing.SimpleForm,
                };
                _store[(tenantId, skill.Name)] = updated;
                AddRevisionUnsafe(tenantId, updated, createdBy, packageSha256);
                return Task.FromResult<Skill?>(updated);
            }

            var stored = skill with
            {
                Package = package?.ToArray(), CurrentRevision = 1, Enabled = true,
                CreatedAt = now, UpdatedAt = now,
                SimpleForm = null,
            };
            _store[(tenantId, skill.Name)] = stored;
            AddRevisionUnsafe(tenantId, stored, createdBy, packageSha256);
            return Task.FromResult<Skill?>(stored);
        }
    }

    /// <summary>軟刪:enabled=false;列與 revision 都留著。已停用/不存在 → false。</summary>
    public Task<bool> DeleteAsync(string tenantId, string name, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_store.TryGetValue((tenantId, name), out var existing) || !existing.Enabled)
            {
                return Task.FromResult(false);
            }

            _store[(tenantId, name)] = existing with { Enabled = false, UpdatedAt = Now() };
            return Task.FromResult(true);
        }
    }

    // ponytail: 05 §5 的 InMemory 遷移鏡射刻意不實作 — InMemorySkillRepository 不 seed 任何 skill
    // (不同於 InMemoryAuthRepository),lite 模式每次啟動皆空 store,永遠沒有底線舊名可遷移。
    // 遷移的事實來源是 Dapper 路徑(DbBootstrap 的 UPDATE),已由真 Postgres 測試背書。

    /// <summary>不過濾 enabled — 軟刪後歷史仍查得到;依 revision 遞減。</summary>
    public Task<IReadOnlyList<SkillRevisionInfo>> ListRevisionsAsync(
        string tenantId, string name, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<SkillRevisionInfo>>(
                _revisions.Where(r => r.Tenant == tenantId && r.Name == name)
                    .Select(r => ToInfo(r.Row)).OrderByDescending(r => r.Revision).ToList());
        }
    }

    public Task<StoredSkillRevision?> GetRevisionAsync(
        string tenantId, string name, int revision, CancellationToken ct)
    {
        lock (_gate)
        {
            StoredSkillRevision? row = _revisions.FirstOrDefault(
                r => r.Tenant == tenantId && r.Name == name && r.Row.Revision == revision).Row;
            return Task.FromResult<StoredSkillRevision?>(row);
        }
    }

    /// <summary>
    /// 呼叫端必須持有 <see cref="ReferenceSyncRoot"/>。enabled skill 與其 current immutable
    /// revision 必須同時存在才可固定；對應 Dapper 的 skill + skill_revision JOIN ... FOR SHARE。
    /// </summary>
    internal bool TryResolveEnabledCurrentRevisionUnsafe(
        string tenantId,
        string name,
        out int revision)
    {
        if (_store.TryGetValue((tenantId, name), out var skill)
            && skill.Enabled
            && _revisions.Any(r =>
                r.Tenant == tenantId
                && r.Name == name
                && r.Row.Revision == skill.CurrentRevision))
        {
            revision = skill.CurrentRevision;
            return true;
        }

        revision = 0;
        return false;
    }

    private void AddRevisionUnsafe(
        string tenantId, Skill stored, string createdBy, string? packageSha256 = null)
    {
        _revisions.Add((tenantId, stored.Name, new StoredSkillRevision(
            stored.CurrentRevision, stored.Definition, SkillHash.Sha256(stored.Definition),
            createdBy, Now(), stored.Kind, stored.Package?.ToArray(), packageSha256)));
    }

    private static SkillRevisionInfo ToInfo(StoredSkillRevision row) => new(
        row.Revision, row.Definition, row.DefinitionSha256, row.CreatedBy, row.CreatedAt,
        row.Kind, row.Package is not null, row.PackageSha256);
}
