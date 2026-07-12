using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Platform.Web.Auth;

/// <summary>
/// HS256 JWT 驗證服務。金鑰用 UTF-8 bytes 當 HMAC key。
/// 刻意不驗 issuer/audience;ClockSkew 設為 0(過期即拒,不給寬限)。
/// platform 不再簽發 token(改由 backend 簽發,claims/secret 位元相容),此處只驗證。
/// </summary>
public sealed class JwtService : IJwtService
{
    private readonly JwtOptions _options;

    public JwtService(JwtOptions options) => _options = options;

    public ClaimsPrincipal Validate(string token)
    {
        var handler = new JwtSecurityTokenHandler
        {
            // 與 JwtBearer 中介軟體一致:不把 sub 映射成 ClaimTypes.NameIdentifier 等長名。
            MapInboundClaims = false,
        };
        return handler.ValidateToken(token, BuildValidationParameters(_options.Secret), out _);
    }

    /// <summary>建立 token 驗證參數;Program.cs 的 JwtBearer 與此處驗證共用同一組。</summary>
    public static TokenValidationParameters BuildValidationParameters(string secret) => new()
    {
        ValidateIssuer = false,
        ValidateAudience = false,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
        ValidateLifetime = true,
        ClockSkew = TimeSpan.Zero,
    };
}
