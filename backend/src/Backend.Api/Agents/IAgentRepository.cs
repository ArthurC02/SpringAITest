namespace Backend.Api.Agents;

/// <summary>
/// Agent aggregate 存取(薄介面,Dapper 與 InMemory 兩實作維持行為 parity — A-DATA-07/14/15)。
/// 每條查詢以 tenantId 過濾 — 租戶隔離是本介面契約,跨租戶一律「不存在」。
/// revisioned-aggregate 狀態機(draft/ETag/validate/publish/restore)由本介面兩實作共同實現,
/// 沿用 SkillRepository 的 revision/CTE 模式;D1 僅 Agent 消費,不為未來 Orchestrator/Workflow 預抽象。
/// </summary>
public interface IAgentRepository
{
    Task<IReadOnlyList<AgentInfo>> ListAsync(string tenantId, CancellationToken ct);

    /// <summary>
    /// Builder 管理讀取（含 draft 與 soft-disabled Agent）；不存在或跨租戶不可見回 null。
    /// Runtime 不得使用此方法判斷可執行性，開始 run 時必須另驗 enabled + published + audience。
    /// </summary>
    Task<Agent?> GetAsync(string tenantId, Guid id, CancellationToken ct);

    /// <summary>
    /// 建立(draft_version=1,draft_validated_version=null,尚無 published revision)。
    /// 同 tenant slug 重複 → 回 null(controller 映射 409);不同 tenant 可用相同 slug。
    /// </summary>
    Task<Agent?> CreateAsync(
        string tenantId, string slug, string name, string description,
        string canonicalDefinition, string definitionSha256, string createdBy, CancellationToken ct);

    /// <summary>
    /// 更新 draft(optimistic concurrency:僅當 draft_version==expectedVersion 才寫入)。
    /// 成功 → draft_version+1、draft_validated_version 清空(改過就要重新驗證)。
    /// 不存在 → NotFound;版本不符(stale ETag)→ VersionConflict(不覆蓋他人更新,A-DATA-08)。
    /// </summary>
    Task<AgentDraftResult> UpdateDraftAsync(
        string tenantId, Guid id, long expectedVersion, string name, string description,
        string canonicalDefinition, string definitionSha256, CancellationToken ct);

    /// <summary>把「version 這個 draft 已驗證」記下(僅當 draft_version 仍==version);draft 之後再改會清空它。</summary>
    Task<bool> MarkValidatedAsync(string tenantId, Guid id, long version, CancellationToken ct);

    /// <summary>
    /// 驗證 draft 的外部 references：Workflow 必須為 tenant/system 可見、enabled、agent-runtime 且
    /// 指定 revision 已發布；Skill 必須是同 tenant persisted、enabled 且 current revision 可固定。
    /// builtin/catalog-only Skill 沒有 backend immutable revision，明確不可綁定。
    /// </summary>
    Task<IReadOnlyList<AgentValidationError>> ValidateReferencesAsync(
        string tenantId, string canonicalDefinition, CancellationToken ct);

    /// <summary>
    /// 發布:同一交易確認 draft_version==expectedVersion 且 draft_validated_version==expectedVersion,
    /// 建立不可變 revision(status=published,舊 published → superseded),固定 skill bindings 與 definition hash。
    /// 版本不符或未驗證 → VersionConflict(不發布未驗證/漂移的內容,A-DATA-09)。
    /// </summary>
    Task<AgentPublishResult> PublishAsync(
        string tenantId, Guid id, long expectedVersion, string createdBy, CancellationToken ct);

    Task<IReadOnlyList<AgentRevisionInfo>> ListRevisionsAsync(string tenantId, Guid id, CancellationToken ct);

    /// <summary>
    /// rollback:把指定舊 revision 的快照(含固定的 skill bindings)重新發布為一個**新** revision,
    /// 不改寫歷史(A-DATA-06)。舊 published → superseded。Agent 或 revision 不存在 → NotFound。
    /// </summary>
    Task<AgentPublishResult> RestoreAsync(string tenantId, Guid id, int revision, string createdBy, CancellationToken ct);

    /// <summary>軟停用/啟用;不存在(含跨租戶)回 false。</summary>
    Task<bool> SetEnabledAsync(string tenantId, Guid id, bool enabled, CancellationToken ct);
}

/// <summary>draft 寫入結果(狀態 + 成功時的新狀態)。</summary>
public sealed record AgentDraftResult(AgentWriteStatus Status, Agent? Agent);
