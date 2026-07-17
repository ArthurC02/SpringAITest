namespace Backend.Api.Common;

/// <summary>
/// 解析呼叫者經 header 轉發的身分:X-Tenant-Id(租戶 code)、X-User-Id(username)、X-User-Role。
/// backend 不自行驗 JWT,身分由已通過內部憑證的上游(platform/workflow)如實轉發。
/// </summary>
public static class IdentityHeaders
{
    public const string TenantHeader = "X-Tenant-Id";
    public const string UserHeader = "X-User-Id";
    public const string RoleHeader = "X-User-Role";

    public static string? TenantId(this HttpRequest request) => Value(request, TenantHeader);

    public static string? UserId(this HttpRequest request) => Value(request, UserHeader);

    /// <summary>UserId 缺標頭時的空字串回落(寫入用途:AddedBy/CreatedBy 等欄位不接受 null)。</summary>
    public static string UserIdOrEmpty(this HttpRequest request) => Value(request, UserHeader) ?? string.Empty;

    public static string? UserRole(this HttpRequest request) => Value(request, RoleHeader);

    /// <summary>需要租戶的端點:缺 X-Tenant-Id 直接 400。</summary>
    public static string RequireTenant(this HttpRequest request) =>
        Value(request, TenantHeader) ?? throw new ApiException(StatusCodes.Status400BadRequest, "缺少租戶識別標頭：X-Tenant-Id");

    private static string? Value(HttpRequest request, string name)
    {
        var v = request.Headers[name].ToString();
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }
}
