using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Backend.Api.BusinessWorkflows;
using System.Collections;
using Backend.Api.Common;
using YamlDotNet.Serialization;

namespace Backend.Api.Skills;

/// <summary>
/// 呼叫 workflow 引擎(:8001)的 POST /skills/validate-package(03-design §2.2)。
/// Request 為 **multipart**(D1:zip 是二進位,避免 base64 膨脹與兩種 transport 並存):
///   - `package`:zip file(application/zip)。
///   - `expected_name`:具名 import 時等於公開 route 的 {name}；server-derived import 省略。
///   - identity headers:X-Internal-Token / X-Tenant-Id / X-User-Id / X-User-Role。
/// 契約同 flow validate:一律回 200,結果在 body({valid, errors, skill, canonical_definition, package_manifest})。
/// 因此任何非 200 或傳輸失敗都是「驗證服務故障」→ 502,絕不放行未驗證的 package。
/// </summary>
public sealed class WorkflowSkillPackageValidator : ISkillPackageValidator
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private static readonly IDeserializer Yaml = new DeserializerBuilder().Build();

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _internalToken;

    public WorkflowSkillPackageValidator(HttpClient http, string baseUrl, string internalToken)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _internalToken = internalToken;
    }

    public async Task<SkillPackageValidationResult> ValidatePackageAsync(
        byte[] package, string fileName, string? expectedName,
        string tenantId, string? userId, string? role, CancellationToken ct)
    {
        var fileContent = new ByteArrayContent(package);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");

        var form = new MultipartFormDataContent
        {
            { fileContent, "package", fileName },
        };
        if (expectedName is not null)
        {
            form.Add(new StringContent(expectedName), "expected_name");
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/skills/validate-package")
        {
            Content = form,
        };
        req.UseInternalIdentity(_internalToken, tenantId, userId, role);

        var body = await _http.SendJsonAsync<ValidatePackageBody>(req, Failure, JsonOpts, ct);

        if (!body.Valid)
        {
            return new SkillPackageValidationResult(
                false,
                body.Errors?.Select(e => new SkillValidationError(e.Code, e.Message, e.Line)).ToList()
                    ?? new List<SkillValidationError>(),
                Skill: null,
                CanonicalDefinition: null);
        }

        ValidateSuccessContract(body, expectedName);
        var validatedSkill = body.Skill!;

        var meta = new SkillMetadata(
            validatedSkill.Name,
            // 與 definition-only 寫入同一個預設:缺席/空 → string.Empty(見 ValidateSuccessContract)。
            validatedSkill.Description ?? string.Empty,
            validatedSkill.RequiredRole!,
            validatedSkill.Kind!);

        return new SkillPackageValidationResult(
            true, Array.Empty<SkillValidationError>(), meta, body.CanonicalDefinition);
    }

    private static ApiException Failure(string detail)
        => new(StatusCodes.Status502BadGateway, "Skill 套件驗證服務呼叫失敗：" + detail);

    private static void ValidateSuccessContract(
        ValidatePackageBody body, string? expectedName)
    {
        static ApiException Violation(string detail) => Failure("引擎回應違反契約（" + detail + "）");

        if (body.Skill is null || string.IsNullOrWhiteSpace(body.CanonicalDefinition))
        {
            throw Violation("缺少 skill 中繼資料或 canonical_definition");
        }

        if (string.IsNullOrWhiteSpace(body.Skill.Name))
        {
            throw Violation("skill.name 必須是非空字串");
        }

        if (expectedName is not null
            && !string.Equals(body.Skill.Name, expectedName, StringComparison.Ordinal))
        {
            throw Violation($"skill.name 必須等於 expected_name '{expectedName}'");
        }

        // description 刻意不要求非空:definition-only 寫入允許缺席(WorkflowSkillValidator 預設 string.Empty),
        // SkillExporter 會把它匯出成 `description: ""`,再要求非空就變成「自己匯出的 zip 匯不回來」。
        // 寫入端不改成必填 —— 那會讓既有沒寫 description 的 flow skill 每次 PUT 都失敗(追溯破壞)。

        if (body.Skill.Kind is not ("flow" or "agentic"))
        {
            throw Violation("skill.kind 必須是 flow 或 agentic");
        }

        if (body.Skill.RequiredRole is not ("USER" or "ADMIN"))
        {
            throw Violation("skill.required_role 必須是 USER 或 ADMIN");
        }

        Dictionary<string, object?> canonical;
        try
        {
            canonical = ToMap(Yaml.Deserialize<object>(body.CanonicalDefinition));
        }
        catch (Exception ex)
        {
            throw Violation("canonical_definition 不是合法 YAML：" + ex.Message);
        }

        if (!string.Equals(
                Scalar(canonical.GetValueOrDefault("name")),
                body.Skill.Name,
                StringComparison.Ordinal))
        {
            throw Violation("canonical_definition.name 與 skill.name 不一致");
        }

        var canonicalKind = Scalar(canonical.GetValueOrDefault("kind"));
        var metadata = ToMap(canonical.GetValueOrDefault("metadata"));
        canonicalKind ??= Scalar(metadata.GetValueOrDefault("kind")) ?? "flow";
        if (!string.Equals(canonicalKind, body.Skill.Kind, StringComparison.Ordinal))
        {
            throw Violation("canonical_definition kind 與 skill.kind 不一致");
        }
    }

    private static Dictionary<string, object?> ToMap(object? value)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (value is not IDictionary dictionary)
        {
            return result;
        }

        foreach (DictionaryEntry item in dictionary)
        {
            if (item.Key is not null)
            {
                result[Convert.ToString(
                    item.Key, System.Globalization.CultureInfo.InvariantCulture)!] = item.Value;
            }
        }

        return result;
    }

    private static string? Scalar(object? value)
        => value as string;

    private sealed record ValidatePackageBody(
        [property: JsonPropertyName("valid")] bool Valid,
        [property: JsonPropertyName("errors")] List<ValidateError>? Errors,
        [property: JsonPropertyName("skill")] ValidateSkill? Skill,
        [property: JsonPropertyName("canonical_definition")] string? CanonicalDefinition);
}
