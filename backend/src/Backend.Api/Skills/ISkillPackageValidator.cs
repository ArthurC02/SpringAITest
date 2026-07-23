namespace Backend.Api.Skills;

/// <summary>
/// Workflow 引擎對 agentic/flow package 的驗證結果(internal validate-package 回應的映射)。
/// valid=false → Errors 進 422 fieldErrors,零副作用。valid=true → Skill 中繼資料(含 kind)與
/// CanonicalDefinition(由引擎產生的權威 YAML 投影;flow 為 SKILL.md 內嵌之定義原文)必存在。
/// package_sha256 由 backend 對實際儲存的原始 bytes 自行計算(不取用引擎 manifest),故此處不映射 manifest。
/// </summary>
public sealed record SkillPackageValidationResult(
    bool Valid,
    IReadOnlyList<SkillValidationError> Errors,
    SkillMetadata? Skill,
    string? CanonicalDefinition);

/// <summary>
/// Agent Skill package 的結構/語意驗證。**唯一事實來源是 workflow 引擎**(:8001 的 POST /skills/validate-package):
/// backend 不自行解析 zip 或 SKILL.md frontmatter,只以 multipart 轉送原始 bytes；具名 import
/// 另帶 expected_name，server-derived import 則省略該欄位，
/// 並把結果映射成 ApiError / 儲存欄位。薄介面,供測試換 fake(不引入 mocking 套件)。
/// </summary>
public interface ISkillPackageValidator
{
    /// <summary>
    /// 以 multipart 送 package bytes 給引擎驗證；expectedName 非 null 時才加入 expected_name。
    /// 引擎不可達 / 非 200 / 逾時 → 拋 ApiException(502)(不得誤判成使用者的 package 不合法,也不得放行)。
    /// </summary>
    Task<SkillPackageValidationResult> ValidatePackageAsync(
        byte[] package, string fileName, string? expectedName,
        string tenantId, string? userId, string? role, CancellationToken ct);
}
