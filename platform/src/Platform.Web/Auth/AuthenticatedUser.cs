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
    /// 且 userId = username(不是數字 id)。capabilities 取自 JWT claim,無則為 null(下游不帶 header)。
    /// </summary>
    public static UserContext ToUserContext(this ClaimsPrincipal principal)
    {
        var user = principal.ToAuthenticatedUser();
        return new UserContext(
            user.Username,
            user.TenantCode,
            user.Role,
            principal.GetCapabilities(),
            principal.GetGroups());
    }

    /// <summary>
    /// 解析 backend 簽發的 capabilities claim(例如 workflow.manage)。
    /// backend 以「每個 capability 一個同名 claim」簽發（JWT payload 是 JSON 陣列）；
    /// 每個 claim 必須是單一、無空白的 tag，格式不符即忽略，不能把一個 grant 擴張成多個權限。
    /// 無 capability 回 null(不是空集合)——讓 UserContext 與純三段身分的等值語意一致,下游也不帶 header。
    /// 這一份解析是 platform 對 backend capabilities claim 契約的單一事實來源(改格式兩端同步)。
    /// </summary>
    public static IReadOnlyList<string>? GetCapabilities(this ClaimsPrincipal principal)
    {
        var caps = principal.FindAll("capabilities")
            .Select(c => c.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsWhiteSpace))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return caps.Length == 0 ? null : caps;
    }

    /// <summary>
    /// Parse repeated signed group claims as one atomic set. If any claim is malformed or
    /// the bounded count is exceeded, discard the entire group set rather than forwarding
    /// a valid-looking subset to downstream audience checks.
    /// </summary>
    public static IReadOnlyList<string>? GetGroups(this ClaimsPrincipal principal)
    {
        var claims = principal.FindAll("groups")
            .Select(claim => claim.Value)
            .ToArray();
        if (claims.Length == 0)
        {
            return null;
        }
        if (claims.Length > UserGroupContract.MaxGroups
            || claims.Distinct(StringComparer.Ordinal).Count() != claims.Length)
        {
            return null;
        }

        var groups = claims
            .OrderBy(group => group, StringComparer.Ordinal)
            .ToArray();
        return UserGroupContract.IsCanonicalGroupSet(groups)
            ? groups
            : null;
    }

    /// <summary>principal 是否具備某個 capability(fail-closed:缺 claim 即 false)。</summary>
    public static bool HasCapability(this ClaimsPrincipal principal, string capability)
        => principal.GetCapabilities()?.Contains(capability, StringComparer.Ordinal) ?? false;

    /// <summary>
    /// 取得可作為聊天記憶、持久化與租戶隔離邊界的身分。JWT 通過簽章驗證不代表其身分 claims
    /// 一定完整；缺少 subject 或 tenantCode 時絕不可退化成共用的空白 key。
    /// </summary>
    public static UserContext? ToUsableChatUserContext(this ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var user = principal.ToUserContext();
        return string.IsNullOrWhiteSpace(user.UserId) || string.IsNullOrWhiteSpace(user.TenantCode)
            ? null
            : user;
    }
}
