using System.Text.Json;
using Platform.Service.Dtos;

namespace Platform.Service.Abstractions;

/// <summary>
/// Agent Skill 管理與 P2–P5/C8 前 public flow compatibility actions 的 backend /api/skills 純代理。
/// ADMIN 把關全在 backend
/// (backend 403 → WorkflowForbiddenException → 對外 403,同 Config PUT 模式);
/// 定義的靜態驗證由 backend 轉呼叫引擎(backend 422 → SkillValidationFailedException → 對外 422)。
/// Skill 的「執行/驗證/目錄」不在此介面 — 那些直接打 workflow 引擎,見 IWorkflowEngineClient。
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
    /// 回復指定 revision；backend 重新驗證 snapshot 並新增 revision。回應原樣穿透 Skill JSON。
    /// </summary>
    Task<JsonElement> RestoreRevisionAsync(
        string name, int revision, UserContext ctx, CancellationToken ct = default);

    /// <summary>
    /// 匯出 Skill 為 Claude Skill 格式 zip(原封轉回 backend 的 bytes,不反序列化)。
    /// backend 404 → WorkflowNotFoundException(對外 404);其餘非 2xx → 對外 502。
    /// </summary>
    Task<SkillExport> ExportAsync(string name, UserContext ctx, CancellationToken ct = default);

    /// <summary>
    /// Agent Skill 匯入(ADMIN):以上傳檔案的 bytes + 檔名重建乾淨的 multipart,代理到 backend
    /// POST /api/skills/{name}/import(backend 以 form-binding 讀 Request.Form.Files["package"])。
    /// 不代理原始 Request.Body:那條路徑在 platform 的 [ApiController] MVC pipeline 下 body 已被排空 → backend 收到空 multipart。
    /// 改由 Web 層用 IFormFile 表單繫結拿到檔案位元組,本層以 MultipartFormDataContent 重新編碼(新 boundary,合法 HTTP)。
    /// 重用 export 的授權/錯誤機制(Bearer→身分 header 轉譯、BackendErrorMapper、ApiError 穿透、global 401)。
    /// 回應原樣穿透 backend 的 Skill JSON(含 additive kind),不套 DTO 以免吞掉欄位;
    /// backend 403(非 ADMIN)→ 對外 403、422 → 套件驗證失敗、其餘非 2xx → 502。
    /// backend 內部端點 GET /api/skills/{name}/package 刻意不代理(維持內部限定)。
    /// </summary>
    Task<JsonElement> ImportAsync(
        string name, byte[] package, string fileName, UserContext ctx, CancellationToken ct = default);

    /// <summary>
    /// Server-derived 匯入：代理 POST /api/skills/import，不送 client name，由 backend/workflow
    /// 從 package canonical metadata 推導並驗證名稱。
    /// </summary>
    Task<JsonElement> ImportAsync(
        byte[] package, string fileName, UserContext ctx, CancellationToken ct = default);

    /// <summary>過渡窗口的 flow definition-only 建立；P5 C8 gate 完成前保留。</summary>
    Task<Skill> CreateAsync(SkillUpsert request, UserContext ctx, CancellationToken ct = default);

    /// <summary>過渡窗口的 flow definition-only 更新；P5 C8 gate 完成前保留。</summary>
    Task<Skill> UpdateAsync(string name, SkillUpsert request, UserContext ctx, CancellationToken ct = default);

    /// <summary>停用 Skill(軟刪,對外 204)。</summary>
    Task DeleteAsync(string name, UserContext ctx, CancellationToken ct = default);
}
