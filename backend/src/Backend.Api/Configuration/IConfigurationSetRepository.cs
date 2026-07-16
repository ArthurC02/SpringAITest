namespace Backend.Api.Configuration;

/// <summary>
/// Configuration Set 存取(薄介面,供測試換 fake)。每條查詢都以 tenantId 過濾 — 租戶隔離是本介面的契約
/// (跨租戶一律「不存在」)。「一租戶至多一 active」由 DB 部分唯一索引 uq_confset_active 兜底,
/// activate 以原子操作切換,不靠應用碼保唯一。
/// </summary>
public interface IConfigurationSetRepository
{
    Task<IReadOnlyList<ConfigurationSetInfo>> ListAsync(string tenantId, CancellationToken ct);

    /// <summary>取單筆(含 values);不存在(含跨租戶不可見)回 null。</summary>
    Task<ConfigurationSet?> GetAsync(string tenantId, Guid id, CancellationToken ct);

    /// <summary>取本租戶當前 is_active 的那筆(含 values);無 active 回 null(呼叫端回落全域預設)。</summary>
    Task<ConfigurationSet?> GetActiveAsync(string tenantId, CancellationToken ct);

    /// <summary>建立(is_active=false)。同租戶同名 → 回 null(由 controller 映射 409),不產生第二列。</summary>
    Task<ConfigurationSet?> CreateAsync(
        string tenantId, string name, IReadOnlyDictionary<string, object> values, string createdBy, CancellationToken ct);

    /// <summary>更新 name/values(不動 is_active);不存在(含跨租戶)回 null;改名撞既有名 → 拋 409。</summary>
    Task<ConfigurationSet?> UpdateAsync(
        string tenantId, Guid id, string name, IReadOnlyDictionary<string, object> values, CancellationToken ct);

    /// <summary>刪除(硬刪);不存在(含跨租戶)回 false。刪掉 active 者 → 該租戶變無 active,不自動改選。</summary>
    Task<bool> DeleteAsync(string tenantId, Guid id, CancellationToken ct);

    /// <summary>
    /// 啟用指定 set:原子地「本租戶其餘全 is_active=false,再目標 =true」。
    /// 回傳啟用後的完整 set;不存在(含跨租戶)回 null。並發啟用後恰好一個 active(DB 部分索引兜底)。
    /// </summary>
    Task<ConfigurationSet?> ActivateAsync(string tenantId, Guid id, CancellationToken ct);
}
