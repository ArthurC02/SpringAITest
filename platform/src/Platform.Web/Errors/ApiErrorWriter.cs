using System.Text.Json;

namespace Platform.Web.Errors;

/// <summary>把 ApiError 直接寫進 HttpResponse(給 JwtBearer 事件與全域例外處理共用)。</summary>
public static class ApiErrorWriter
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static async Task WriteAsync(HttpResponse response, int status, string message, CancellationToken ct = default)
    {
        var error = new ApiError(DateTime.UtcNow, status, message, new Dictionary<string, string>());
        response.StatusCode = status;
        // 401/403 等錯誤明確標記 charset=utf-8,確保中文訊息被正確解讀。
        await response.WriteAsJsonAsync(error, JsonOpts, contentType: "application/json; charset=utf-8", ct);
    }
}
