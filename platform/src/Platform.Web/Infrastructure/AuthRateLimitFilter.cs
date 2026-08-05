using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Platform.Service.Dtos;
using Platform.Web.Errors;

namespace Platform.Web.Infrastructure;

internal sealed class AuthRateLimitFilter(AuthRateLimiter rateLimiter) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        context.ActionArguments.TryGetValue("request", out var request);
        AuthRequestPath.TryMatch(context.HttpContext.Request, out var operation);
        var allowed = request switch
        {
            LoginRequest login => rateLimiter.TryAcquireLoginAccount(login.Username),
            RegisterRequest register => rateLimiter.TryAcquireRegisterAccount(
                register.TenantCode,
                register.Username),
            _ when operation == AuthWriteOperation.Login
                => rateLimiter.TryAcquireLoginAccount(username: null),
            _ => rateLimiter.TryAcquireRegisterAccount(tenantCode: null, username: null),
        };

        if (!allowed)
        {
            await RateLimitResponse.WriteAsync(
                context.HttpContext.Response,
                context.HttpContext.RequestAborted);
            context.Result = new EmptyResult();
            return;
        }

        await next();
    }
}
