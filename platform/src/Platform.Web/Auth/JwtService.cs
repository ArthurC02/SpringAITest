using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;

namespace Platform.Web.Auth;

/// <summary>Validates backend-issued ES256 JWTs. Platform intentionally has no issuance API or private key.</summary>
public sealed class JwtService
{
    private readonly JwtOptions _options;

    public JwtService(JwtOptions options) => _options = options;

    public ClaimsPrincipal Validate(string token)
    {
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        return handler.ValidateToken(token, BuildValidationParameters(_options), out _);
    }

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
