using System.Text.Json;

namespace Backend.Api.Common;

/// <summary>把 ApiError 直接寫進 HttpResponse(給 internal-token middleware 與全域例外處理共用)。</summary>
public static class ApiErrorWriter
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    // 契約鏡像:與 platform/src/Platform.Web/Errors/ 同名檔為刻意重複(跨服務各自部署,無法共用 assembly)。
    // 修改 422/ApiError 格式化邏輯時務必同步另一邊。
    public static async Task WriteAsync(
        HttpResponse response, int status, string message, CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? fieldErrors = null)
    {
        var error = new ApiError(
            DateTime.UtcNow, status, message,
            fieldErrors is null ? new Dictionary<string, string>() : new Dictionary<string, string>(fieldErrors));
        response.StatusCode = status;
        // 明確標記 charset=utf-8,確保中文訊息被正確解讀。
        await response.WriteAsJsonAsync(error, JsonOpts, contentType: "application/json; charset=utf-8", ct);
    }
}
