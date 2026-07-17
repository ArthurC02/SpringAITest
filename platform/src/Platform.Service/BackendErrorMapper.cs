using Platform.Service.Exceptions;

namespace Platform.Service;

/// <summary>
/// backend 錯誤 → 對外同狀態碼例外的共用映射(SkillService、ConfigurationSetService 共用):
/// 400/403/404/409/422 各自映射到對外同狀態碼的例外,message 沿用 backend(不改寫);
/// 400 與 422 另外把 backend 的 fieldErrors 帶上,否則欄位級錯誤/引擎錯誤碼會在代理層被吞成空 map。
/// 422 復用 SkillValidationFailedException(全域處理裡它是唯一映射到 422 的載體)。
/// 其餘狀態碼 → WorkflowInvocationException(對外 502)。
/// </summary>
internal static class BackendErrorMapper
{
    public static async Task<Exception> MapErrorAsync(
        HttpResponseMessage resp, BackendClient backend, string failurePrefix, CancellationToken ct)
    {
        var status = (int)resp.StatusCode;
        if (status is not (400 or 403 or 404 or 409 or 422))
        {
            return new WorkflowInvocationException(failurePrefix + "HTTP " + status);
        }

        var error = await backend.ReadErrorAsync(resp, ct);
        var message = error.Message;
        if (string.IsNullOrWhiteSpace(message))
        {
            message = failurePrefix + "HTTP " + status;
        }

        return status switch
        {
            400 => new WorkflowBadInputException(message) { FieldErrors = error.FieldErrors },
            403 => new WorkflowForbiddenException(message),
            404 => new WorkflowNotFoundException(message),
            409 => new DownstreamConflictException(message),
            _ => new SkillValidationFailedException(message) { FieldErrors = error.FieldErrors },
        };
    }
}
