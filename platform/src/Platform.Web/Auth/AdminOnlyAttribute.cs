using Microsoft.AspNetCore.Mvc.Filters;
using Platform.Service.Exceptions;

namespace Platform.Web.Auth;

/// <summary>
/// Platform 本地 ADMIN authorization filter。Authorization filter 早於 resource filter/model binding，
/// 因此 USER 即使送超大或畸形 multipart 也固定 403，不會先解析 body 或洩漏上傳限制。
/// Backend 仍保留自己的 ADMIN boundary；這層是 gateway 的 auth-first 防護。
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class AdminOnlyAttribute : Attribute, IAuthorizationFilter
{
    private readonly string _message;

    public AdminOnlyAttribute(string message = "權限不足，無法存取 Skill") => _message = message;

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        if (!string.Equals(
                context.HttpContext.User.ToAuthenticatedUser().Role,
                "ADMIN",
                StringComparison.Ordinal))
        {
            throw new WorkflowForbiddenException(_message);
        }
    }
}
