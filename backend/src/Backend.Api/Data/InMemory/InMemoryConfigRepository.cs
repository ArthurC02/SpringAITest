using System.Collections.Concurrent;
using Backend.Api.Config;

namespace Backend.Api.Data.InMemory;

/// <summary>
/// 系統組態 key-value 的行程記憶體實作(Lite 模式)。ConcurrentDictionary 已執行緒安全。
/// 以 (tenant, key) tuple 當複合鍵 — 不把 tenant 串進字串鍵,免得 key 本身含分隔符時跨租戶撞車
/// (對應 Dapper 版的複合主鍵 (tenant_id, key))。
/// </summary>
public sealed class InMemoryConfigRepository : IConfigRepository
{
    private readonly ConcurrentDictionary<(string Tenant, string Key), ConfigItem> _store = new();

    public Task<IReadOnlyList<ConfigItem>> ListAsync(string tenantId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ConfigItem>>(_store
            .Where(e => e.Key.Tenant == tenantId)
            .Select(e => e.Value)
            .OrderBy(i => i.Key, StringComparer.Ordinal)
            .ToList());

    public Task<ConfigItem?> GetAsync(string tenantId, string key, CancellationToken ct)
        => Task.FromResult(_store.TryGetValue((tenantId, key), out var item) ? item : null);

    public Task<ConfigItem> UpsertAsync(string tenantId, string key, string value, CancellationToken ct)
    {
        var item = new ConfigItem(key, value, DateTime.UtcNow);
        _store[(tenantId, key)] = item;
        return Task.FromResult(item);
    }
}
