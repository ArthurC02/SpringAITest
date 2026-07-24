using System.Text.Json;
using Platform.Service.Dtos;

namespace Platform.Service.Abstractions;

/// <summary>Skill 引擎服務:代理下游 Python(:8001)。轉發 4 個 X-* header 並轉譯下游狀態碼。
/// Skill 的 CRUD 不在此介面 — 那是 backend 的職責,見 ISkillService。</summary>
public interface IWorkflowService
{
    /// <summary>
    /// 執行指定 Skill(GET /skills 清單裡的內建或自訂 skill)。錯誤碼與 /workflows/{name}/invoke 一致。
    /// 回應原樣穿透(JsonElement,不映射成 DTO):引擎的輸出鍵(output/trace/…)由引擎定義,
    /// 代理層若套 DTO,引擎新增欄位就會被靜默吃掉。
    /// </summary>
    Task<JsonElement> InvokeSkillAsync(
        string name, Dictionary<string, JsonElement> input, UserContext ctx, CancellationToken ct = default);

    /// <summary>對一份 skill 定義跑靜態驗證(無副作用,前端編輯器即時校驗用)。
    /// 引擎一律回 200,驗證結果({valid, errors, skill})在 body — 原樣穿透。</summary>
    Task<JsonElement> ValidateSkillAsync(string definition, UserContext ctx, CancellationToken ct = default);

    /// <summary>可執行 skill 目錄:引擎合併內建(repo 檔案)+ 自訂(來自 backend),每筆帶 source 徽章。</summary>
    Task<JsonElement> GetSkillCatalogAsync(UserContext ctx, CancellationToken ct = default);

    /// <summary>節點目錄(唯讀):名稱/版本/reads/writes/requires_tools 契約。</summary>
    Task<JsonElement> GetNodeCatalogAsync(UserContext ctx, CancellationToken ct = default);

    /// <summary>
    /// 工具目錄(唯讀):Workflow Tool Registry 的安全 authoring metadata
    /// (name/kind/description/risk/returns)，不含 endpoint、token 或 executable implementation。
    /// </summary>
    Task<JsonElement> GetToolCatalogAsync(UserContext ctx, CancellationToken ct = default);

    /// <summary>Business Rule fact catalog filtered from Workflow's single registry-owned catalog response.</summary>
    Task<JsonElement> GetBusinessRuleFactsAsync(UserContext ctx, CancellationToken ct = default);

    /// <summary>Business Rule action catalog filtered from Workflow's single registry-owned catalog response.</summary>
    Task<JsonElement> GetBusinessRuleActionsAsync(UserContext ctx, CancellationToken ct = default);

    /// <summary>Validate and canonicalize a typed Rule AST. valid=false remains an HTTP-success body.</summary>
    Task<JsonElement> ValidateBusinessRulesAsync(
        BusinessRuleValidateRequest request, UserContext ctx, CancellationToken ct = default);

    /// <summary>Run Workflow's production evaluator with supplied dry-run facts; never calls real tools.</summary>
    Task<JsonElement> SimulateBusinessRulesAsync(
        BusinessRuleSimulateRequest request, UserContext ctx, CancellationToken ct = default);
}
