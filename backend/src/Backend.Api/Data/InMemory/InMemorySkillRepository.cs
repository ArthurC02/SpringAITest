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

    /// <summary>稽核表:只增不減(軟刪不動它)。</summary>
    private readonly List<(string Tenant, string Name, SkillRevisionInfo Row)> _revisions = new();

    private static DateTime Now() => DateTime.UtcNow;

    public Task<IReadOnlyList<SkillInfo>> ListAsync(string tenantId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<SkillInfo>>(
            _store.Where(e => e.Key.Tenant == tenantId).Select(e => e.Value).Where(s => s.Enabled)
                .OrderBy(s => s.Name, StringComparer.Ordinal)
                .Select(s => new SkillInfo(
                    s.Name, s.Description, s.RequiredRole, s.Enabled, s.CurrentRevision, s.CreatedAt, s.UpdatedAt))
                .ToList());

    public Task<Skill?> GetAsync(string tenantId, string name, CancellationToken ct)
    {
        var skill = _store.GetValueOrDefault((tenantId, name));
        // 軟刪後不可見(WHERE ... AND enabled)。
        return Task.FromResult(skill is { Enabled: true } ? skill : null);
    }

    /// <summary>
    /// 忠實模擬 ON CONFLICT (tenant_id, name) DO UPDATE ... WHERE NOT skill.enabled:
    /// 同名仍啟用 → 0 列 → null(不寫 revision);同名已軟刪 → 復活並把 revision 接著加。
    /// </summary>
    public Task<Skill?> CreateAsync(string tenantId, Skill skill, string createdBy, CancellationToken ct)
    {
        var now = Now();

        if (_store.TryGetValue((tenantId, skill.Name), out var existing))
        {
            if (existing.Enabled)
            {
                return Task.FromResult<Skill?>(null);
            }

            // 軟刪的名字可以重用:同一列復活,稽核鏈不斷號。
            var revived = skill with
            {
                CurrentRevision = existing.CurrentRevision + 1,
                Enabled = true,
                CreatedAt = existing.CreatedAt,
                UpdatedAt = now,
            };
            _store[(tenantId, skill.Name)] = revived;
            AddRevision(tenantId, revived, createdBy);
            return Task.FromResult<Skill?>(revived);
        }

        var stored = skill with { CurrentRevision = 1, Enabled = true, CreatedAt = now, UpdatedAt = now };
        _store[(tenantId, skill.Name)] = stored;
        AddRevision(tenantId, stored, createdBy);
        return Task.FromResult<Skill?>(stored);
    }

    public Task<Skill?> UpdateAsync(string tenantId, string name, Skill skill, string updatedBy, CancellationToken ct)
    {
        // 已軟刪的 skill 不可經 PUT 復活(WHERE ... AND enabled)。
        if (!_store.TryGetValue((tenantId, name), out var existing) || !existing.Enabled)
        {
            return Task.FromResult<Skill?>(null);
        }

        // name 不可經 PUT 改變(以路由的 name 為準);updated_at 必變動;current_revision +1。
        var stored = skill with
        {
            Name = name,
            Enabled = true,
            CurrentRevision = existing.CurrentRevision + 1,
            CreatedAt = existing.CreatedAt,
            UpdatedAt = Now(),
        };
        _store[(tenantId, name)] = stored;
        AddRevision(tenantId, stored, updatedBy);
        return Task.FromResult<Skill?>(stored);
    }

    /// <summary>軟刪:enabled=false;列與 revision 都留著。已停用/不存在 → false。</summary>
    public Task<bool> DeleteAsync(string tenantId, string name, CancellationToken ct)
    {
        if (!_store.TryGetValue((tenantId, name), out var existing) || !existing.Enabled)
        {
            return Task.FromResult(false);
        }

        _store[(tenantId, name)] = existing with { Enabled = false, UpdatedAt = Now() };
        return Task.FromResult(true);
    }

    /// <summary>不過濾 enabled — 軟刪後歷史仍查得到;依 revision 遞減。</summary>
    public Task<IReadOnlyList<SkillRevisionInfo>> ListRevisionsAsync(
        string tenantId, string name, CancellationToken ct)
    {
        lock (_revisions)
        {
            return Task.FromResult<IReadOnlyList<SkillRevisionInfo>>(
                _revisions.Where(r => r.Tenant == tenantId && r.Name == name)
                    .Select(r => r.Row).OrderByDescending(r => r.Revision).ToList());
        }
    }

    private void AddRevision(string tenantId, Skill stored, string createdBy)
    {
        lock (_revisions)
        {
            _revisions.Add((tenantId, stored.Name, new SkillRevisionInfo(
                stored.CurrentRevision, stored.Definition, SkillHash.Sha256(stored.Definition),
                createdBy, Now())));
        }
    }
}
