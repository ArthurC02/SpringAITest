using Platform.Web.Auth;
using Microsoft.IdentityModel.Tokens;

namespace Platform.Web.Tests;

/// <summary>
/// JwtService 只保留驗證(platform 不再簽發)。驗證的對象是 backend 位元相容格式的 token
/// (由 TestTokens 以相同 secret / claims 鑄造)。
/// </summary>
public sealed class JwtTests
{
    private const string Secret = TestTokens.DefaultSecret;

    private static JwtService Service(string? secret = null) =>
        new(new JwtOptions { Secret = secret ?? Secret });

    [Fact]
    public void Validate_Accepts_BackendFormatToken_AndReadsClaims()
    {
        var token = TestTokens.Mint("alice", "ADMIN", "demo-a");

        var principal = Service().Validate(token);

        Assert.Equal("alice", principal.FindFirst("sub")!.Value);
        Assert.Equal("ADMIN", principal.FindFirst("role")!.Value);
        Assert.Equal("demo-a", principal.FindFirst("tenantCode")!.Value);
    }

    [Fact]
    public void Validate_Rejects_ExpiredToken()
    {
        // nbf=-2h、exp=-1h;ClockSkew=0 必被拒。
        var token = TestTokens.Mint(
            "alice", "USER", "demo-a",
            notBefore: DateTime.UtcNow.AddHours(-2),
            expires: DateTime.UtcNow.AddHours(-1));

        Assert.ThrowsAny<SecurityTokenException>(() => Service().Validate(token));
    }

    // ValidateLifetime 同時管 nbf 與 exp:「尚未生效」是與「已過期」不同的例外型別與判斷分支。
    [Fact]
    public void Validate_Rejects_NotYetValidToken()
    {
        var token = TestTokens.Mint(
            "alice", "USER", "demo-a",
            notBefore: DateTime.UtcNow.AddHours(1),
            expires: DateTime.UtcNow.AddHours(2));

        Assert.ThrowsAny<SecurityTokenNotYetValidException>(() => Service().Validate(token));
    }

    [Fact]
    public void Validate_Rejects_TamperedToken()
    {
        var token = TestTokens.Mint("alice", "USER", "demo-a");

        // 竄改簽章段,驗證必失敗。
        var parts = token.Split('.');
        parts[2] += "tampered";
        var tampered = string.Join('.', parts);

        Assert.ThrowsAny<SecurityTokenException>(() => Service().Validate(tampered));
    }

    [Fact]
    public void Validate_Rejects_DifferentSecret()
    {
        var token = TestTokens.Mint("alice", "USER", "demo-a", secret: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

        Assert.ThrowsAny<SecurityTokenException>(() =>
            Service(secret: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb").Validate(token));
    }
}
