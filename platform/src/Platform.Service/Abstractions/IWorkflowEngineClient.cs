using System.Text.Json;
using Platform.Service.Dtos;

namespace Platform.Service.Abstractions;

/// <summary>整個 Workflow Python 引擎的 client：承載 Skill/Business Workflow 執行與驗證、
/// Business Rules、node/tool catalog；轉發內部身分 headers 並轉譯下游狀態碼。
/// 公開 artifact CRUD 不在此介面 — 那是 backend 的職責。</summary>
public interface IWorkflowEngineClient
{
    /// <summary>
    /// 執行指定 Skill(GET /skills 清單裡的內建或自訂 skill)。錯誤碼與 /skills/{name}/invoke 一致。
    /// 回應原樣穿透(JsonElement,不映射成 DTO):引擎的輸出鍵(output/trace/…)由引擎定義,
    /// 代理層若套 DTO,引擎新增欄位就會被靜默吃掉。
    /// </summary>
    Task<JsonElement> InvokeSkillAsync(
        string name, Dictionary<string, JsonElement> input, UserContext ctx, CancellationToken ct = default);

    /// <summary>P2–P5/C8 前透過 /skills/validate 保留的 Business Workflow 驗證相容別名；新呼叫端使用 /business-workflows/validate。
    /// 引擎一律回 200,驗證結果({valid, errors, skill})在 body — 原樣穿透。</summary>
    Task<JsonElement> ValidateSkillAsync(string definition, UserContext ctx, CancellationToken ct = default);

    /// <summary>對 Business Workflow YAML 做靜態驗證。</summary>
    Task<JsonElement> ValidateBusinessWorkflowAsync(
        string definition, UserContext ctx, CancellationToken ct = default);

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
