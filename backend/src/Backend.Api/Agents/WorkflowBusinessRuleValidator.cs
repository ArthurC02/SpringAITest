using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Backend.Api.Common;

namespace Backend.Api.Agents;

/// <summary>
/// Thin client for Workflow's deterministic Rule Validator. A validation result (including valid=false)
/// is a successful HTTP 200 response. Transport, non-2xx, malformed JSON, or a contract-incomplete valid
/// response fail closed as 502 so an unavailable engine can never authorize publish.
/// </summary>
public sealed class WorkflowBusinessRuleValidator : IBusinessRuleValidator
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _internalToken;

    public WorkflowBusinessRuleValidator(HttpClient http, string baseUrl, string internalToken)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _internalToken = internalToken;
    }

    public async Task<BusinessRuleValidationResult> ValidateAsync(
        string gate,
        JsonElement ruleSet,
        BusinessRuleReferenceCatalog referenceCatalog,
        string tenantId,
        string? userId,
        string? role,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, _baseUrl + "/business-rules/validate")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            Content = JsonContent.Create(new { gate, ruleSet, referenceCatalog }, options: JsonOpts),
        };
        request.Headers.TryAddWithoutValidation("X-Internal-Token", _internalToken);
        request.Headers.TryAddWithoutValidation(IdentityHeaders.TenantHeader, tenantId);
        request.Headers.TryAddWithoutValidation(IdentityHeaders.UserHeader, userId ?? string.Empty);
        request.Headers.TryAddWithoutValidation(IdentityHeaders.RoleHeader, role ?? string.Empty);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Failure(ex.Message);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw Failure("HTTP " + (int)response.StatusCode);
            }

            ValidateBody? body;
            try
            {
                body = await response.Content.ReadFromJsonAsync<ValidateBody>(JsonOpts, ct);
            }
            catch (Exception ex)
            {
                throw Failure(ex.Message);
            }

            if (body is null)
            {
                throw Failure("回應內容為空");
            }
            if (body.Valid
                && (body.CanonicalRuleSet is null
                    || body.CanonicalRuleSet.Value.ValueKind != JsonValueKind.Object
                    || !body.CanonicalRuleSet.Value.TryGetProperty("version", out var version)
                    || version.ValueKind != JsonValueKind.Number
                    || !version.TryGetInt32(out var versionNumber)
                    || versionNumber != 1
                    || !body.CanonicalRuleSet.Value.TryGetProperty("rules", out var rules)
                    || rules.ValueKind != JsonValueKind.Array))
            {
                throw Failure("valid=true 但 canonicalRuleSet 缺少支援的 version/rules envelope");
            }
            if (!body.Valid && (body.Errors is null || body.Errors.Count == 0))
            {
                throw Failure("valid=false 但缺少 errors");
            }

            return new BusinessRuleValidationResult(
                body.Valid,
                body.CanonicalRuleSet?.Clone(),
                (IReadOnlyList<BusinessRuleValidationError>?)body.Errors?.Select(e => new BusinessRuleValidationError(
                    e.Path ?? string.Empty,
                    e.Code ?? "rule_invalid",
                    e.Message ?? "Business Rule 驗證失敗")).ToList()
                ?? Array.Empty<BusinessRuleValidationError>());
        }
    }

    private static ApiException Failure(string detail)
        => new(StatusCodes.Status502BadGateway, "Business Rule 驗證服務呼叫失敗：" + detail);

    private sealed record ValidateBody(
        [property: JsonPropertyName("valid")] bool Valid,
        [property: JsonPropertyName("canonicalRuleSet")] JsonElement? CanonicalRuleSet,
        [property: JsonPropertyName("errors")] IReadOnlyList<ValidateError>? Errors);

    private sealed record ValidateError(
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("code")] string? Code,
        [property: JsonPropertyName("message")] string? Message);
}
