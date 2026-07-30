namespace Backend.Api.Config;

/// <summary>系統組態 key-value 存取(薄介面,供測試換 fake)。每個租戶各有一份組態。</summary>
public interface IConfigRepository
{
    Task<IReadOnlyList<ConfigItem>> ListAsync(string tenantId, CancellationToken ct);

    /// <summary>讀本租戶的單一 key;不存在(含跨租戶)→ null。</summary>
    Task<ConfigItem?> GetAsync(string tenantId, string key, CancellationToken ct);

    /// <summary>upsert 本租戶的指定 key;回傳寫入後的項目(含更新時間)。</summary>
    Task<ConfigItem> UpsertAsync(string tenantId, string key, string value, CancellationToken ct);
}
