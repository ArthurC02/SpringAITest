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

    /// <summary>
    /// 原子保存 Workflow 正規化後的 canonical definition/hash，並把「version 這個 draft 已驗證」記下。
    /// 僅當 draft_version 仍==version 才成功；不增加 version，因為 canonicalization 是同一份使用者
    /// draft 的確定性表示，不是另一筆 author edit。draft 之後再改仍會清空 validated version。
    /// </summary>
    Task<bool> MarkValidatedAsync(
        string tenantId,
        Guid id,
        long version,
        string canonicalDefinition,
        string definitionSha256,
        CancellationToken ct);

    /// <summary>
    /// 驗證 draft 的外部 references：Workflow 必須為 tenant/system 可見、enabled、agent-runtime 且
    /// 指定 revision 已發布；Skill 必須是同 tenant persisted、enabled 且 current revision 可固定。
    /// builtin/catalog-only Skill 沒有 backend immutable revision，明確不可綁定。
    /// </summary>
    Task<IReadOnlyList<AgentValidationError>> ValidateReferencesAsync(
        string tenantId, string canonicalDefinition, CancellationToken ct);

    /// <summary>
    /// 發布:同一交易確認 draft_version==expectedVersion 且 draft_validated_version==expectedVersion,
    /// 寫入 Workflow 本次重新驗證的 canonical definition/hash，建立不可變 revision
    /// (status=published,舊 published → superseded)，並消耗 validated version。
    /// 版本不符或未驗證 → VersionConflict(不發布未驗證/漂移的內容,A-DATA-09)。
    /// </summary>
    Task<AgentPublishResult> PublishAsync(
        string tenantId,
        Guid id,
        long expectedVersion,
        string canonicalDefinition,
        string definitionSha256,
        string createdBy,
        CancellationToken ct);

    Task<IReadOnlyList<AgentRevisionInfo>> ListRevisionsAsync(string tenantId, Guid id, CancellationToken ct);

    /// <summary>
    /// 取回指定 immutable revision 的完整 canonical definition（含由 pinned rows 重建的 skill_bindings）。
    /// restore 在建立新 revision 前用它呼叫 Workflow Rule validator；不存在或跨租戶回 null。
    /// </summary>
    Task<string?> GetRevisionDefinitionAsync(
        string tenantId, Guid id, int revision, CancellationToken ct);

    /// <summary>
    /// rollback:用 Workflow 重新驗證/正規化後的 definition 建立一個**新** revision，並原封複製
    /// 指定舊 revision 的 pinned skill bindings；不改寫歷史(A-DATA-06)。舊 published → superseded。
    /// Agent 或 revision 不存在 → NotFound。
    /// </summary>
    Task<AgentPublishResult> RestoreAsync(
        string tenantId,
        Guid id,
        int revision,
        string canonicalDefinition,
        string definitionSha256,
        string createdBy,
        CancellationToken ct);

    /// <summary>軟停用/啟用;不存在(含跨租戶)回 false。</summary>
    Task<bool> SetEnabledAsync(string tenantId, Guid id, bool enabled, CancellationToken ct);
}

/// <summary>draft 寫入結果(狀態 + 成功時的新狀態)。</summary>
public sealed record AgentDraftResult(AgentWriteStatus Status, Agent? Agent);
