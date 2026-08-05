namespace Platform.Web.Errors;

internal static class RateLimitResponse
{
    internal const string Message = "請求過於頻繁，請稍後再試";

    internal static Task WriteAsync(HttpResponse response, CancellationToken ct = default)
        => ApiErrorWriter.WriteAsync(
            response,
            StatusCodes.Status429TooManyRequests,
            Message,
            ct);
}
