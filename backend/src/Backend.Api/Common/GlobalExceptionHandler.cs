using Microsoft.AspNetCore.Diagnostics;

namespace Backend.Api.Common;

/// <summary>
/// 全域例外處理:ApiException 依自帶狀態碼映射,其餘一律 500。
/// </summary>
public sealed class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) => _logger = logger;

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (httpContext.Response.HasStarted)
        {
            return false;
        }

        var (status, message) = exception is ApiException api
            ? (api.Status, api.Message)
            : (StatusCodes.Status500InternalServerError, exception.Message);

        if (status >= 500)
        {
            // TraceIdentifier 同時是 body 的 correlationId —— 沒帶進 log 的話呼叫端回報的那串對不回任何一行。
            _logger.LogError(
                exception,
                "未預期的伺服器錯誤 [{CorrelationId}]：{訊息}",
                httpContext.TraceIdentifier,
                exception.Message);
        }
        // 刻意不把 5xx 訊息通用化:backend 只對 platform/workflow 這兩個內部呼叫端說話,
        // fail-closed 的原因(hash mismatch、契約違反)是它們需要的診斷資訊。對外的通用化
        // 由 platform 的 GlobalExceptionHandler 負責(02-spec §5 講的是 Platform public errors)。

        // 例外路徑上永遠帶不出 ETag:UseExceptionHandler 註冊的 ClearCacheHeaders 會在回應開始前
        // 把 ETag 清成 default。02-spec §5 要求的「409 帶最新 ETag」因此走 ApiErrors.VersionConflict
        // 直接回結果,不經這裡。
        // 只有 ApiException 可能帶欄位級錯誤(如 Skill 存檔 422 的引擎錯誤碼);其餘一律空 map。
        await ApiErrorWriter.WriteAsync(
            httpContext.Response, status, message, cancellationToken, (exception as ApiException)?.FieldErrors);
        return true;
    }
}
