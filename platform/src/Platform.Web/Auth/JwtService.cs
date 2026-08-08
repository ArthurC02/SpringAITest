using Microsoft.IdentityModel.Tokens;

namespace Platform.Web.Auth;

/// <summary>
/// Validates backend-issued ES256 JWTs. Platform intentionally has no issuance API or private key.
/// 只有一個成員:生產唯一用法是把這組參數交給 JwtBearer(Program.cs),由框架的 JsonWebTokenHandler 驗章。
/// </summary>
public static class JwtService
{
    public static TokenValidationParameters BuildValidationParameters(JwtOptions options) => new()
    {
        ValidateIssuer = true,
        ValidIssuer = options.Issuer,
        ValidateAudience = true,
        ValidAudience = options.Audience,
        ValidateIssuerSigningKey = true,
        RequireSignedTokens = true,
        RequireExpirationTime = true,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.Zero,
        ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
        TryAllIssuerSigningKeys = false,
        IssuerSigningKeyResolver = (_, _, kid, _) =>
            !string.IsNullOrWhiteSpace(kid)
            && options.VerificationKeys.TryGetValue(kid, out var key)
                ? [key]
                : [],
    };
}
