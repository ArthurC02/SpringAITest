namespace Backend.Api.Common;

/// <summary>
/// 統一錯誤回應形狀,與 platform 完全一致。
/// JSON:{ timestamp, status, code, message, correlationId, fieldErrors }。
/// code 穩定且機器可讀(message 可在地化);correlationId 是本次請求的不透明識別碼。
/// fieldErrors 永遠存在(非驗證錯誤時為空物件);key 用 camelCase 欄位名。
/// </summary>
public sealed record ApiError(
    DateTime Timestamp,
    int Status,
    string Code,
    string Message,
    string CorrelationId,
    Dictionary<string, string> FieldErrors);

/// <summary>
/// status → 穩定 code 的唯一對照表(02-spec §5)。
/// 契約鏡像:與 platform/src/Platform.Web/Errors/ 同名檔逐字相同,改任一邊須同步另一邊。
/// </summary>
public static class ApiErrorCodes
{
    public static string ForStatus(int status) => status switch
    {
        400 => "validation_failed",
        401 => "authentication_required",
        403 => "forbidden",
        404 => "not_found",
        409 => "version_conflict",
        413 => "payload_too_large",
        422 => "unprocessable_entity",
        428 => "precondition_required",
        429 => "rate_limited",
        500 => "internal_error",
        502 => "upstream_unavailable",
        _ => status >= 500 ? "internal_error" : "request_failed",
    };
}
