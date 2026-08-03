namespace Platform.Web.Errors;

/// <summary>
/// 統一錯誤回應形狀。JSON:{ timestamp, status, code, message, correlationId, fieldErrors }。
/// code 穩定且機器可讀(message 可在地化);correlationId 是本次請求的不透明識別碼,
/// 讓 500 的固定訊息仍能對回內部 log。fieldErrors 永遠存在(非驗證錯誤時為空物件);key 用 camelCase 欄位名。
/// 與 backend/src/Backend.Api/Common/ 同名檔刻意保持一致,改任一邊須同步另一邊。
/// </summary>
public sealed record ApiError(
    DateTime Timestamp,
    int Status,
    string Code,
    string Message,
    string CorrelationId,
    Dictionary<string, string> FieldErrors);

/// <summary>
/// status → 穩定 code 的唯一對照表(02-spec §5)。P1 一個狀態碼一個 code 就夠用;
/// 之後各階段要加領域碼(例如 409 的 conversation_runtime_conflict)時,是在寫出點覆寫 code,
/// 不是改這張表 —— envelope 形狀不變。
/// 與 backend/src/Backend.Api/Common/ 同名檔刻意保持一致,改任一邊須同步另一邊。
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
