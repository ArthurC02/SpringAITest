namespace Backend.Api.Skills;

/// <summary>Skill 存取(薄介面,供測試換 fake)。每條查詢都以 tenantId 過濾 — 租戶隔離是本介面的契約。
/// 清單與單筆只看得到 enabled 的 skill(軟刪後即不可見);skill_revision 永不刪、且軟刪後仍查得到。</summary>
public interface ISkillRepository
{
    Task<IReadOnlyList<SkillInfo>> ListAsync(string tenantId, CancellationToken ct);

    /// <summary>取單筆;不存在(含跨租戶不可見、已軟刪)回 null。</summary>
    Task<Skill?> GetAsync(string tenantId, string name, CancellationToken ct);

    /// <summary>
    /// 建立(current_revision = 1,同時寫入 revision 1 的稽核列)。
    /// 同名且**仍啟用** → 回 null(由 controller 映射 409),且不產生任何 revision。
    /// 同名但**已軟刪** → 沿用該列復活:enabled=true、current_revision 接著加(稽核鏈不斷號、名稱可重用)。
    /// </summary>
    Task<Skill?> CreateAsync(string tenantId, Skill skill, string createdBy, CancellationToken ct);

    /// <summary>更新(name 不變,current_revision +1,同時寫入該版的稽核列);不存在或已軟刪回 null。</summary>
    Task<Skill?> UpdateAsync(string tenantId, string name, Skill skill, string updatedBy, CancellationToken ct);

    /// <summary>
    /// Agent Skill 匯入(P0):以「建立 / 更新 / 復活」upsert 語意在**單一交易**內寫入
    /// definition + metadata + package(匯入的 flow/agentic 都保存原始 zip bytes)+ 兩個 hash，
    /// 並把 package snapshot 寫入 revision，供 server-side restore。
    /// 與 CreateAsync 不同:import 對既有(仍啟用)skill 也直接更新(不回 null),永遠 bump revision。
    /// package/packageSha256 為 null → definition-only flow。
    /// </summary>
    Task<Skill?> ImportAsync(
        string tenantId, Skill skill, byte[]? package, string? packageSha256, string createdBy, CancellationToken ct);

    /// <summary>軟刪(enabled=false);不存在(含跨租戶不可見、已軟刪)回 false。revision 保留。</summary>
    Task<bool> DeleteAsync(string tenantId, string name, CancellationToken ct);

    /// <summary>
    /// 該 skill 的所有 revision,依 revision 遞減。**不過濾 enabled** — 軟刪後歷史仍查得到(稽核紅線)。
    /// 查無此 skill(含跨租戶)→ 空清單;每個 skill 建立時必寫 revision 1,故「空清單」等同「不存在」。
    /// </summary>
    Task<IReadOnlyList<SkillRevisionInfo>> ListRevisionsAsync(string tenantId, string name, CancellationToken ct);

    /// <summary>取得一筆完整 revision（含內部 package snapshot）；不存在或跨租戶回 null。</summary>
    Task<StoredSkillRevision?> GetRevisionAsync(
        string tenantId, string name, int revision, CancellationToken ct);
}
