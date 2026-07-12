namespace Platform.Web.Errors;

/// <summary>
/// 統一錯誤回應形狀。JSON:{ timestamp, status, message, fieldErrors }。
/// fieldErrors 永遠存在(非驗證錯誤時為空物件);key 用 camelCase 欄位名。
/// </summary>
public sealed record ApiError(
    DateTime Timestamp,
    int Status,
    string Message,
    Dictionary<string, string> FieldErrors);
