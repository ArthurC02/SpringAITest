namespace Backend.Api.Common;

/// <summary>
/// 內部憑證守門:除 /health 外,所有請求都必須帶 header X-Internal-Token 且與設定值相符。
/// backend 不對公網,呼叫者只有 platform 與 workflow;不符一律回 401 ApiError。
/// </summary>
public sealed class InternalTokenMiddleware
{
    public const string HeaderName = "X-Internal-Token";

    private readonly RequestDelegate _next;
    private readonly string _token;

    public InternalTokenMiddleware(RequestDelegate next, string token)
    {
        _next = next;
        _token = token;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // /health 免驗,供 compose depends_on 探活。
        if (context.Request.Path.Equals("/health", StringComparison.Ordinal))
        {
            await _next(context);
            return;
        }

        var provided = context.Request.Headers[HeaderName].ToString();
        if (!string.Equals(provided, _token, StringComparison.Ordinal))
        {
            await ApiErrorWriter.WriteAsync(context.Response, StatusCodes.Status401Unauthorized, "內部憑證無效");
            return;
        }

        await _next(context);
    }
}
