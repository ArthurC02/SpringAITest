using System.IdentityModel.Tokens.Jwt;
using System.Text;
using Backend.Api.Auth;
using Microsoft.IdentityModel.Tokens;

namespace Backend.Api.Tests;

/// <summary>
/// 驗 backend 簽發的 token 能被「platform 現行驗證參數」通過(HS256、同 secret、claims sub/role/tenantCode)。
/// 這組驗證參數逐字對應 platform JwtService.BuildValidationParameters,確保簽發側相容。
/// </summary>
public sealed class JwtTests
{
    private const string Secret = "dev-jwt-secret-change-me-0123456789abcdef";

    private static JwtService Service(string? secret = null) =>
        new(secret ?? Secret, TimeSpan.FromHours(24));

    private static TokenValidationParameters PlatformParams(string secret) => new()
    {
        ValidateIssuer = false,
        ValidateAudience = false,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
        ValidateLifetime = true,
        ClockSkew = TimeSpan.Zero,
    };

    [Fact]
    public void Issue_TokenValidatesWithPlatformParams_AndCarriesClaims()
    {
        var token = Service().Issue("alice", "ADMIN", "demo-a");

        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var principal = handler.ValidateToken(token, PlatformParams(Secret), out _);

        Assert.Equal("alice", principal.FindFirst("sub")!.Value);
        Assert.Equal("ADMIN", principal.FindFirst("role")!.Value);
        Assert.Equal("demo-a", principal.FindFirst("tenantCode")!.Value);
    }

    [Fact]
    public void Issue_ExpiresApprox24hAfterIssued()
    {
        var token = Service().Issue("alice", "USER", "demo-a");

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        var lifetime = jwt.ValidTo - jwt.ValidFrom;

        Assert.Equal(TimeSpan.FromHours(24), lifetime);
    }

    [Fact]
    public void Issue_WithDifferentSecret_FailsPlatformValidation()
    {
        var token = Service(secret: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa").Issue("alice", "USER", "demo-a");

        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        Assert.ThrowsAny<SecurityTokenException>(() =>
            handler.ValidateToken(token, PlatformParams("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"), out _));
    }
}
