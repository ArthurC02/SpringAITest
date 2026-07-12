using System.Security.Claims;

namespace Platform.Web.Auth;

/// <summary>JWT 驗證。platform 不再簽發 token(由 backend 簽發),只保留驗證供 Bearer 中介軟體使用。</summary>
public interface IJwtService
{
    /// <summary>驗證 backend 簽發的 token 並回傳 principal;無效/過期/竄改會丟出例外。</summary>
    ClaimsPrincipal Validate(string token);
}
