using System.Security.Claims;
using Platform.Service.Dtos;

namespace Platform.Web.Auth;

/// <summary>從 JWT 主體組出的已認證使用者(欄位順序:username、role、tenantCode)。</summary>
public sealed record AuthenticatedUser(string Username, string Role, string TenantCode);

/// <summary><see cref="ClaimsPrincipal"/> 的便利擴充。</summary>
public static class ClaimsPrincipalExtensions
{
    public static AuthenticatedUser ToAuthenticatedUser(this ClaimsPrincipal principal)
    {
        var username = principal.FindFirstValue("sub") ?? string.Empty;
        var role = principal.FindFirstValue("role") ?? string.Empty;
        var tenantCode = principal.FindFirstValue("tenantCode") ?? string.Empty;
        return new AuthenticatedUser(username, role, tenantCode);
    }

    /// <summary>
    /// 組出傳給下游的 UserContext。注意順序與 AuthResult 不同:(userId, tenantCode, role);
    /// 且 userId = username(不是數字 id)。
    /// </summary>
    public static UserContext ToUserContext(this ClaimsPrincipal principal)
    {
        var user = principal.ToAuthenticatedUser();
        return new UserContext(user.Username, user.TenantCode, user.Role);
    }
}
