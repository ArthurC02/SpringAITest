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
            _logger.LogError(exception, "未預期的伺服器錯誤：{訊息}", exception.Message);
        }

        // 只有 ApiException 可能帶欄位級錯誤(如 Skill 存檔 422 的引擎錯誤碼);其餘一律空 map。
        await ApiErrorWriter.WriteAsync(
            httpContext.Response, status, message, cancellationToken, (exception as ApiException)?.FieldErrors);
        return true;
    }
}
