namespace Platform.Web.Auth;

/// <summary>JWT 設定。Secret 讀環境變數 JWT_SECRET(與 backend 簽發時位元相容)。只用於驗證。</summary>
public sealed class JwtOptions
{
    public string Secret { get; set; } = "dev-jwt-secret-change-me-0123456789abcdef";
}
