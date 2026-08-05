using Platform.Web.Errors;

namespace Platform.Web.Infrastructure;

internal sealed class AuthIpRateLimitMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, AuthRateLimiter rateLimiter)
    {
        if (AuthRequestPath.TryMatch(context.Request, out _)
            && !rateLimiter.TryAcquireClientIp(context.Connection.RemoteIpAddress))
        {
            await RateLimitResponse.WriteAsync(context.Response, context.RequestAborted);
            return;
        }

        await next(context);
    }
}
