using Platform.Service.Exceptions;
using Microsoft.AspNetCore.Diagnostics;

namespace Platform.Web.Errors;

/// <summary>
/// 全域例外處理:把 Service 層例外依對照表映射成 ApiError 與 HTTP 狀態碼。
/// 注意:WorkflowBadInput(下游 422)對外是 400;WorkflowInvocation 對外是 502。
/// </summary>
public sealed class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) => _logger = logger;

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        // 若回應已開始寫出(例如 SSE 串流途中出錯),無法再改寫 body,交回讓串流自然結束。
        if (httpContext.Response.HasStarted)
        {
            return false;
        }

        var (status, message) = Map(exception);
        if (status >= 500)
        {
            _logger.LogError(exception, "未預期的伺服器錯誤：{訊息}", exception.Message);
        }

        await ApiErrorWriter.WriteAsync(httpContext.Response, status, message, cancellationToken);
        return true;
    }

    private static (int Status, string Message) Map(Exception ex) => ex switch
    {
        TenantNotFoundException => (StatusCodes.Status404NotFound, ex.Message),
        UsernameTakenException => (StatusCodes.Status409Conflict, ex.Message),
        InvalidInviteCodeException => (StatusCodes.Status403Forbidden, ex.Message),
        InvalidCredentialsException => (StatusCodes.Status401Unauthorized, ex.Message),
        WorkflowNotFoundException => (StatusCodes.Status404NotFound, ex.Message),
        WorkflowForbiddenException => (StatusCodes.Status403Forbidden, ex.Message),
        WorkflowBadInputException => (StatusCodes.Status400BadRequest, ex.Message),
        DocumentNotFoundException => (StatusCodes.Status404NotFound, ex.Message),
        WorkflowInvocationException => (StatusCodes.Status502BadGateway, ex.Message),
        _ => (StatusCodes.Status500InternalServerError, ex.Message),
    };
}
