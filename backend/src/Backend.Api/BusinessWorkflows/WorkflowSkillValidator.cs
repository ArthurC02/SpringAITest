using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Backend.Api.Common;
using Backend.Api.Skills;

namespace Backend.Api.BusinessWorkflows;

/// <summary>
/// 呼叫 workflow 引擎(:8001)的 POST /business-workflows/validate。契約:一律回 200,
/// 驗證結果在 body({valid, errors:[{code, message, line}]})— 錯誤不走 HTTP 狀態碼。
/// 因此任何非 200(含傳輸失敗)都是「驗證服務本身壞了」,不是「定義不合法」:
/// 對外回 502(不是 422)— 不得把引擎不可達誤判成使用者的定義有問題,更不得放行未驗證的定義。
/// </summary>
public sealed class WorkflowSkillValidator : ISkillValidator
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _internalToken;

    public WorkflowSkillValidator(HttpClient http, string baseUrl, string internalToken)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _internalToken = internalToken;
    }

    public async Task<SkillValidationResult> ValidateAsync(
        string definition, string tenantId, string? userId, string? role, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/business-workflows/validate")
        {
            Content = JsonContent.Create(new { definition }, options: JsonOpts),
        };
        req.UseInternalIdentity(_internalToken, tenantId, userId, role);

        var body = await _http.SendJsonAsync<ValidateBody>(req, Failure, JsonOpts, ct);

        // valid=true 卻沒帶 skill 中繼資料 → 引擎違反契約。此時 backend 無從得知 name/description,
        // 只能視為驗證服務故障(502);絕不猜測欄位值,也絕不放行寫入。
        if (body.Valid && body.Skill is null)
        {
            throw Failure("回應缺少 skill 中繼資料");
        }

        if (body.Valid)
        {
            ValidateSuccessContract(body.Skill!);
        }

        return new SkillValidationResult(
            body.Valid,
            body.Errors?.Select(e => new SkillValidationError(e.Code, e.Message, e.Line)).ToList()
                ?? new List<SkillValidationError>(),
            body.Skill is null
                ? null
                : new SkillMetadata(
                    body.Skill.Name,
                    body.Skill.Description ?? string.Empty,
                    // 規格 §3.1:required_role 選填,預設 USER。
                    string.IsNullOrWhiteSpace(body.Skill.RequiredRole) ? "USER" : body.Skill.RequiredRole,
                    // kind 為 additive(R4):舊引擎不帶 → 預設 flow。
                    string.IsNullOrWhiteSpace(body.Skill.Kind) ? "flow" : body.Skill.Kind));
    }

    private static ApiException Failure(string detail)
        => new(StatusCodes.Status502BadGateway, "Skill 驗證服務呼叫失敗：" + detail);

    /// <summary>
    /// valid=true 的成功路徑契約檢查(比照姊妹類 WorkflowSkillPackageValidator.ValidateSuccessContract)。
    /// 語法/編譯規則的權威仍在引擎;這裡守的是 backend 自己的資料完整性 —— 引擎回報的 metadata
    /// 一旦違約就會直接落地成壞資料,而 backend 沒有第二次機會發現。
    /// 「欄位缺席」與「欄位非法」不同類:缺 required_role/kind 仍走既有預設(USER / flow),
    /// 只有**明確給了非法值**才算違約。
    /// </summary>
    private static void ValidateSuccessContract(ValidateSkill skill)
    {
        static ApiException Violation(string detail) => Failure("引擎回應違反契約（" + detail + "）");

        if (string.IsNullOrWhiteSpace(skill.Name))
        {
            throw Violation("skill.name 必須是非空字串");
        }

        // definition-only 寫入不含 package,agentic 一律走 import(AGENTS.md)。此端點回報 agentic
        // 即為違約:放行會造出 kind=agentic 但 package=null 的列,之後 /package 404、
        // export 會用 flow 的打包器產出格式錯誤的 zip、restore 直接撞 409。
        if (!string.IsNullOrWhiteSpace(skill.Kind) && !string.Equals(skill.Kind, "flow", StringComparison.Ordinal))
        {
            throw Violation("skill.kind 必須是 flow（agentic 僅能經 import 建立）");
        }

        if (!string.IsNullOrWhiteSpace(skill.RequiredRole)
            && skill.RequiredRole is not ("USER" or "ADMIN"))
        {
            throw Violation("skill.required_role 必須是 USER 或 ADMIN");
        }
    }

    /// <summary>
    /// 引擎回應 body:{"valid": bool, "errors": [{"code","message","line"}],
    /// "skill": {"name","description","required_role","input_schema"}}(skill 僅 valid=true 時出現;
    /// input_schema 由引擎自持,backend 不需要 → 不映射)。
    /// </summary>
    private sealed record ValidateBody(
        [property: JsonPropertyName("valid")] bool Valid,
        [property: JsonPropertyName("errors")] List<ValidateError>? Errors,
        [property: JsonPropertyName("skill")] ValidateSkill? Skill);
}

/// <summary>
/// workflow 引擎兩個 skill 驗證端點(/skills/validate、/skills/validate-package)共用的回應片段。
/// 兩者的 errors/skill 形狀逐字相同,契約只有一份。
/// </summary>
internal sealed record ValidateError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("line")] int? Line);

internal sealed record ValidateSkill(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("required_role")] string? RequiredRole,
    [property: JsonPropertyName("kind")] string? Kind);
