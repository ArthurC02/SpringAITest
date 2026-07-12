namespace Platform.Web.Dtos;

/// <summary>登入 HTTP 回應 body。JSON:{ token, username, role, tenantCode }。</summary>
public sealed record LoginResponse(string Token, string Username, string Role, string TenantCode);
