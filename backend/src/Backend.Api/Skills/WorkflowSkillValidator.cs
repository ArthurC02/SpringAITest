using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Backend.Api.Common;

namespace Backend.Api.Skills;

/// <summary>
/// 呼叫 workflow 引擎(:8001)的 POST /skills/validate。契約:一律回 200,
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
        using var req = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/skills/validate")
        {
            // 強制 HTTP/1.1(避免下游 uvicorn 在 h2c 升級時掉 body;與 platform 對 workflow 的呼叫一致)。
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            Content = JsonContent.Create(new { definition }, options: JsonOpts),
        };

        req.Headers.TryAddWithoutValidation("X-Internal-Token", _internalToken);
        req.Headers.TryAddWithoutValidation(IdentityHeaders.TenantHeader, tenantId);
        req.Headers.TryAddWithoutValidation(IdentityHeaders.UserHeader, userId ?? string.Empty);
        req.Headers.TryAddWithoutValidation(IdentityHeaders.RoleHeader, role ?? string.Empty);

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ApiException(StatusCodes.Status502BadGateway, "Skill 驗證服務呼叫失敗：" + ex.Message);
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                throw new ApiException(
                    StatusCodes.Status502BadGateway, "Skill 驗證服務呼叫失敗：HTTP " + (int)resp.StatusCode);
            }

            ValidateBody? body;
            try
            {
                body = await resp.Content.ReadFromJsonAsync<ValidateBody>(JsonOpts, ct);
            }
            catch (Exception ex)
            {
                throw new ApiException(StatusCodes.Status502BadGateway, "Skill 驗證服務呼叫失敗：" + ex.Message);
            }

            if (body is null)
            {
                throw new ApiException(StatusCodes.Status502BadGateway, "Skill 驗證服務呼叫失敗：回應內容為空");
            }

            // valid=true 卻沒帶 skill 中繼資料 → 引擎違反契約。此時 backend 無從得知 name/description,
            // 只能視為驗證服務故障(502);絕不猜測欄位值,也絕不放行寫入。
            if (body.Valid && body.Skill is null)
            {
                throw new ApiException(
                    StatusCodes.Status502BadGateway, "Skill 驗證服務呼叫失敗：回應缺少 skill 中繼資料");
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
                        string.IsNullOrWhiteSpace(body.Skill.RequiredRole) ? "USER" : body.Skill.RequiredRole));
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

    private sealed record ValidateError(
        [property: JsonPropertyName("code")] string Code,
        [property: JsonPropertyName("message")] string? Message,
        [property: JsonPropertyName("line")] int? Line);

    private sealed record ValidateSkill(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("required_role")] string? RequiredRole);
}
