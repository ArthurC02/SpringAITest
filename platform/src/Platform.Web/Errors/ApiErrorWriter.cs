using Platform.Service;

namespace Platform.Web.Errors;

/// <summary>
/// 把 ApiError 直接寫進 HttpResponse(給 JwtBearer 事件與全域例外處理共用)。
/// 與 backend/src/Backend.Api/Common/ 同名檔刻意保持一致,改任一邊須同步另一邊。
/// </summary>
public static class ApiErrorWriter
{
    /// <summary>
    /// fieldErrors 為 null(絕大多數錯誤)時輸出空物件 — ApiError 形狀永遠是 6 個欄位。
    /// code 由狀態碼推出(<see cref="ApiErrorCodes.ForStatus"/>),correlationId 取 TraceIdentifier
    /// (與 log scope 同一個不透明 id,500 的固定訊息才對得回內部細節)。
    /// </summary>
    public static async Task WriteAsync(
        HttpResponse response,
        int status,
        string message,
        CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? fieldErrors = null)
    {
        // 契約鏡像:與 backend/src/Backend.Api/Common/ 同名檔為刻意重複(跨服務各自部署,無法共用 assembly)。修改 422/ApiError 格式化邏輯時務必同步另一邊。
        var correlationId = response.HttpContext.TraceIdentifier;
        var error = new ApiError(
            DateTime.UtcNow, status, ApiErrorCodes.ForStatus(status), message,
            correlationId,
            fieldErrors is null ? new Dictionary<string, string>() : new Dictionary<string, string>(fieldErrors));
        response.StatusCode = status;
        // 錯誤回應同時把 correlationId 放進 header:curl 使用者不解析 body 也拿得到追蹤編號
        // (同一串會出現在 backend/workflow 的日誌裡)。成功回應刻意不加(最小變更)。
        response.Headers[Infrastructure.CorrelationIdHeader.Name] = correlationId;
        // 401/403 等錯誤明確標記 charset=utf-8,確保中文訊息被正確解讀。
        await response.WriteAsJsonAsync(error, InternalRequest.Web, contentType: "application/json; charset=utf-8", ct);
    }
}
