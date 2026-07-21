using System.Collections.Concurrent;
using Backend.Api.Config;

namespace Backend.Api.Data.InMemory;

/// <summary>系統組態 key-value 的行程記憶體實作(Lite 模式)。ConcurrentDictionary 已執行緒安全。</summary>
public sealed class InMemoryConfigRepository : IConfigRepository
{
    private readonly ConcurrentDictionary<string, ConfigItem> _store = new();

    public Task<IReadOnlyList<ConfigItem>> ListAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ConfigItem>>(_store.Values.OrderBy(i => i.Key).ToList());

    public Task<ConfigItem> UpsertAsync(string key, string value, CancellationToken ct)
    {
        var item = new ConfigItem(key, value, DateTime.UtcNow);
        _store[key] = item;
        return Task.FromResult(item);
    }
}
