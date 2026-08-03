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
            // 原始例外/下游細節只進 log,永不回給客戶端(避免洩漏內部拓撲/堆疊)。
            // TraceIdentifier 同時是 body 的 correlationId —— 沒帶進 log 的話使用者回報的那串對不回任何一行。
            _logger.LogError(
                exception,
                "未預期的伺服器錯誤 [{CorrelationId}]：{訊息}",
                httpContext.TraceIdentifier,
                exception.Message);
            message = status == StatusCodes.Status502BadGateway
                ? "上游服務暫時無法使用，請稍後再試"
                : "伺服器發生錯誤，請稍後再試";
        }

        await ApiErrorWriter.WriteAsync(
            httpContext.Response, status, message, cancellationToken, FieldErrorsOf(exception));
        return true;
    }

    /// <summary>帶得動欄位級錯誤的兩種例外:代理 backend 400(欄位驗證)與 422(引擎的 skill 錯誤碼);其餘一律空 map。</summary>
    private static IReadOnlyDictionary<string, string>? FieldErrorsOf(Exception ex) => ex switch
    {
        WorkflowBadInputException bad => bad.FieldErrors,
        SkillValidationFailedException invalid => invalid.FieldErrors,
        _ => null,
    };

    private static (int Status, string Message) Map(Exception ex) => ex switch
    {
        TenantNotFoundException => (StatusCodes.Status404NotFound, ex.Message),
        UsernameTakenException => (StatusCodes.Status409Conflict, ex.Message),
        InvalidInviteCodeException => (StatusCodes.Status403Forbidden, ex.Message),
        InvalidCredentialsException => (StatusCodes.Status401Unauthorized, ex.Message),
        WorkflowNotFoundException => (StatusCodes.Status404NotFound, ex.Message),
        WorkflowForbiddenException => (StatusCodes.Status403Forbidden, ex.Message),
        WorkflowBadInputException => (StatusCodes.Status400BadRequest, ex.Message),
        WorkflowPayloadTooLargeException => (StatusCodes.Status413PayloadTooLarge, ex.Message),
        SkillValidationFailedException => (StatusCodes.Status422UnprocessableEntity, ex.Message),
        DocumentNotFoundException => (StatusCodes.Status404NotFound, ex.Message),
        DownstreamConflictException => (StatusCodes.Status409Conflict, ex.Message),
        WorkflowInvocationException => (StatusCodes.Status502BadGateway, ex.Message),
        _ => (StatusCodes.Status500InternalServerError, ex.Message),
    };
}
