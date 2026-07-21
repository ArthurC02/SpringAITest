using Backend.Api.Common;
using Backend.Api.Configuration;

namespace Backend.Api.Data.InMemory;

/// <summary>
/// Configuration Set 儲存庫的行程記憶體實作(Lite 模式)。忠實模擬 DB 的 UNIQUE (tenant_id, name)、
/// 租戶過濾、「一租戶至多一 active」、刪 active 後不自動改選、values 僅在單筆回傳。
/// DB 靠部分唯一索引 uq_confset_active 兜底「至多一 active」;InMemory 改以 lock 內原子的
/// 「先關其餘再開目標」維持同一不變量。時間戳走 DateTime.UtcNow。
/// </summary>
public sealed class InMemoryConfigurationSetRepository : IConfigurationSetRepository
{
    private sealed record Entry(
        Guid Id, string Tenant, string Name, bool IsActive,
        Dictionary<string, object> Values, string CreatedBy, DateTime CreatedAt, DateTime UpdatedAt);

    private readonly Dictionary<Guid, Entry> _store = new();

    private static DateTime Now() => DateTime.UtcNow;

    private static ConfigurationSet ToDto(Entry e) => new(
        e.Id, e.Name, e.IsActive, new Dictionary<string, object>(e.Values), e.CreatedBy, e.CreatedAt, e.UpdatedAt);

    public Task<IReadOnlyList<ConfigurationSetInfo>> ListAsync(string tenantId, CancellationToken ct)
    {
        lock (_store)
        {
            return Task.FromResult<IReadOnlyList<ConfigurationSetInfo>>(
                _store.Values.Where(e => e.Tenant == tenantId).OrderBy(e => e.Name, StringComparer.Ordinal)
                    .Select(e => new ConfigurationSetInfo(e.Id, e.Name, e.IsActive, e.CreatedAt, e.UpdatedAt))
                    .ToList());
        }
    }

    public Task<ConfigurationSet?> GetAsync(string tenantId, Guid id, CancellationToken ct)
    {
        lock (_store)
        {
            var e = _store.GetValueOrDefault(id);
            return Task.FromResult(e is not null && e.Tenant == tenantId ? ToDto(e) : null);
        }
    }

    public Task<ConfigurationSet?> GetActiveAsync(string tenantId, CancellationToken ct)
    {
        lock (_store)
        {
            var e = _store.Values.FirstOrDefault(x => x.Tenant == tenantId && x.IsActive);
            return Task.FromResult(e is not null ? ToDto(e) : null);
        }
    }

    public Task<ConfigurationSet?> CreateAsync(
        string tenantId, string name, IReadOnlyDictionary<string, object> values, string createdBy, CancellationToken ct)
    {
        lock (_store)
        {
            if (_store.Values.Any(e => e.Tenant == tenantId && e.Name == name))
            {
                return Task.FromResult<ConfigurationSet?>(null); // UNIQUE (tenant_id, name)
            }

            var now = Now();
            var entry = new Entry(
                Guid.NewGuid(), tenantId, name, false,
                new Dictionary<string, object>(values), createdBy, now, now);
            _store[entry.Id] = entry;
            return Task.FromResult<ConfigurationSet?>(ToDto(entry));
        }
    }

    public Task<ConfigurationSet?> UpdateAsync(
        string tenantId, Guid id, string name, IReadOnlyDictionary<string, object> values, CancellationToken ct)
    {
        lock (_store)
        {
            if (!_store.TryGetValue(id, out var e) || e.Tenant != tenantId)
            {
                return Task.FromResult<ConfigurationSet?>(null);
            }

            if (_store.Values.Any(x => x.Tenant == tenantId && x.Name == name && x.Id != id))
            {
                throw new ApiException(409, "Configuration Set 名稱已存在：" + name);
            }

            var updated = e with { Name = name, Values = new Dictionary<string, object>(values), UpdatedAt = Now() };
            _store[id] = updated;
            return Task.FromResult<ConfigurationSet?>(ToDto(updated));
        }
    }

    public Task<bool> DeleteAsync(string tenantId, Guid id, CancellationToken ct)
    {
        lock (_store)
        {
            if (_store.TryGetValue(id, out var e) && e.Tenant == tenantId)
            {
                _store.Remove(id);
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }
    }

    public Task<ConfigurationSet?> ActivateAsync(string tenantId, Guid id, CancellationToken ct)
    {
        lock (_store)
        {
            if (!_store.TryGetValue(id, out var target) || target.Tenant != tenantId)
            {
                return Task.FromResult<ConfigurationSet?>(null);
            }

            foreach (var e in _store.Values.Where(x => x.Tenant == tenantId && x.IsActive && x.Id != id).ToList())
            {
                _store[e.Id] = e with { IsActive = false, UpdatedAt = Now() };
            }

            var activated = target with { IsActive = true, UpdatedAt = Now() };
            _store[id] = activated;
            return Task.FromResult<ConfigurationSet?>(ToDto(activated));
        }
    }
}
