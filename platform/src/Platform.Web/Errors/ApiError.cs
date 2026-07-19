namespace Platform.Web.Errors;

/// <summary>
/// 統一錯誤回應形狀。JSON:{ timestamp, status, message, fieldErrors }。
/// fieldErrors 永遠存在(非驗證錯誤時為空物件);key 用 camelCase 欄位名。
/// 與 backend/src/Backend.Api/Common/ 同名檔刻意保持一致,改任一邊須同步另一邊。
/// </summary>
public sealed record ApiError(
    DateTime Timestamp,
    int Status,
    string Message,
    Dictionary<string, string> FieldErrors);
