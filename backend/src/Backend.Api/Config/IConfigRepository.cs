namespace Backend.Api.Config;

/// <summary>系統組態 key-value 存取(薄介面,供測試換 fake)。</summary>
public interface IConfigRepository
{
    Task<IReadOnlyList<ConfigItem>> ListAsync(CancellationToken ct);

    /// <summary>upsert 指定 key;回傳寫入後的項目(含更新時間)。</summary>
    Task<ConfigItem> UpsertAsync(string key, string value, CancellationToken ct);
}
