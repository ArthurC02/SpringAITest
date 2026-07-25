namespace Backend.Api.RuntimeDiscovery;

public sealed class InMemoryRuntimeBindingRepository : IRuntimeBindingRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<string, TenantRuntimeBinding> _items = new(StringComparer.Ordinal);
    public Task<TenantRuntimeBinding?> GetAsync(string tenantId, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_items.TryGetValue(tenantId, out var value) ? value : null);
    }
    public Task<TenantRuntimeBinding> PutAsync(string tenantId, TenantRuntimeBinding binding, CancellationToken ct)
    {
        lock (_gate) { _items[tenantId] = binding; return Task.FromResult(binding); }
    }
}
