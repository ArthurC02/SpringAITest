using System.Text.Json;
using Platform.Service.Dtos;

namespace Platform.Service.Abstractions;

/// <summary>
/// Skill CRUD 服務:純代理 backend /api/skills。ADMIN 把關全在 backend
/// (backend 403 → WorkflowForbiddenException → 對外 403,同 Config PUT 模式);
/// 定義的靜態驗證由 backend 轉呼叫引擎(backend 422 → SkillValidationFailedException → 對外 422)。
/// Skill 的「執行/驗證/目錄」不在此介面 — 那些直接打 workflow 引擎,見 IWorkflowService。
/// 讀取端點(list/get/revisions)原樣穿透 backend JSON(snake_case),不套 DTO 以免吞掉 backend 新增欄位。
/// </summary>
public interface ISkillService
{
    /// <summary>列出 Skill(backend 清單已不含 definition 內文);原樣穿透 backend JSON。</summary>
    Task<JsonElement> ListAsync(UserContext ctx, CancellationToken ct = default);

    /// <summary>取單一 Skill(含 definition);原樣穿透 backend JSON;backend 404 → WorkflowNotFoundException(對外 404)。</summary>
    Task<JsonElement> GetAsync(string name, UserContext ctx, CancellationToken ct = default);

    /// <summary>唯讀 revision 歷史(依 revision 遞減);原樣穿透 backend JSON;軟刪的 skill 其歷史仍查得到。</summary>
    Task<JsonElement> GetRevisionsAsync(string name, UserContext ctx, CancellationToken ct = default);

    /// <summary>
    /// 匯出 Skill 為 Claude Skill 格式 zip(原封轉回 backend 的 bytes,不反序列化)。
    /// backend 404 → WorkflowNotFoundException(對外 404);其餘非 2xx → 對外 502。
    /// </summary>
    Task<SkillExport> ExportAsync(string name, UserContext ctx, CancellationToken ct = default);

    /// <summary>建立 Skill;backend 409(同名) → DownstreamConflictException(對外 409)。</summary>
    Task<Skill> CreateAsync(SkillUpsert request, UserContext ctx, CancellationToken ct = default);

    /// <summary>更新 Skill(產生新 revision)。</summary>
    Task<Skill> UpdateAsync(string name, SkillUpsert request, UserContext ctx, CancellationToken ct = default);

    /// <summary>停用 Skill(軟刪,對外 204)。</summary>
    Task DeleteAsync(string name, UserContext ctx, CancellationToken ct = default);
}
