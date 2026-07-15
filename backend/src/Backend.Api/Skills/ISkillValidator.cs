namespace Backend.Api.Skills;

/// <summary>引擎回報的單一驗證錯誤:code = 靜態驗證錯誤碼(unknown_node/unbounded_loop/…),line 為 YAML 行號(可能為 null)。</summary>
public sealed record SkillValidationError(string Code, string? Message, int? Line);

/// <summary>
/// 引擎自 YAML 解析出的 skill 中繼資料(僅 valid=true 時提供)。
/// backend 用它當儲存鍵與欄位值 — 這樣 backend 不必裝 YAML parser,YAML 的解析只有引擎一個實作。
/// </summary>
public sealed record SkillMetadata(string Name, string Description, string RequiredRole);

/// <summary>引擎驗證結果。valid=false → backend 拒絕存檔並回 422(errors 進 fieldErrors)。</summary>
public sealed record SkillValidationResult(
    bool Valid, IReadOnlyList<SkillValidationError> Errors, SkillMetadata? Skill);

/// <summary>
/// Skill 定義的靜態驗證。**驗證的唯一事實來源是引擎**(workflow :8001):backend 不自行實作任何
/// 驗證規則、也不解析 YAML,只把 definition 原文送去 POST /skills/validate,
/// 並把結果(錯誤碼 or skill 中繼資料)映射成 ApiError / DB 欄位。
/// 薄介面,供測試換 fake(不引入 mocking 套件)。
/// </summary>
public interface ISkillValidator
{
    /// <summary>驗證 definition;身分標頭如實轉發(引擎的租戶/角色語意與 backend 一致)。</summary>
    Task<SkillValidationResult> ValidateAsync(
        string definition, string tenantId, string? userId, string? role, CancellationToken ct);
}
