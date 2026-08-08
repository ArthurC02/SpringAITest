using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using Platform.Web.Auth;

namespace Platform.Web.Tests;

public sealed class JwtTests
{
    /// <summary>
    /// 測試專用的 token 驗證器:直接對 <see cref="JwtService.BuildValidationParameters"/> 產出的參數
    /// 驗一個 token,不必起 <c>WebApplicationFactory</c> 打 HTTP。
    /// ⚠️ 此 <see cref="JwtSecurityTokenHandler"/> **僅供測試** —— 生產路徑上沒有任何地方 new 它:
    /// Program.cs 只把同一組 <see cref="TokenValidationParameters"/> 交給 JwtBearer,實際驗章的是
    /// 框架內部的 <c>JsonWebTokenHandler</c>。兩者共用的是「驗證參數」這個唯一真理來源,不是 handler。
    /// </summary>
    private sealed class TestTokenValidator(JwtOptions options)
    {
        public ClaimsPrincipal Validate(string token)
            => new JwtSecurityTokenHandler { MapInboundClaims = false }
                .ValidateToken(token, JwtService.BuildValidationParameters(options), out _);
    }

    private static JwtOptions Options(params (string Kid, TestJwtKeyPair Key)[] keys)
    {
        var ring = keys.Length == 0
            ? new Dictionary<string, string> { [TestTokens.ActiveKid] = TestTokens.ActiveKey.PublicPemBase64 }
            : keys.ToDictionary(entry => entry.Kid, entry => entry.Key.PublicPemBase64, StringComparer.Ordinal);
        return JwtOptions.Resolve(TestTokens.Issuer, TestTokens.Audience, JsonSerializer.Serialize(ring));
    }

    [Fact]
    public void Validate_AcceptsBackendFormatToken_AndReadsClaims()
    {
        var principal = new TestTokenValidator(Options()).Validate(TestTokens.Mint("alice", "ADMIN", "demo-a"));

        Assert.Equal("alice", principal.FindFirst("sub")!.Value);
        Assert.Equal("ADMIN", principal.FindFirst("role")!.Value);
        Assert.Equal("demo-a", principal.FindFirst("tenantCode")!.Value);
    }

    [Fact]
    public void Validate_RotationRingAcceptsOldAndNewKids()
    {
        var oldKey = TestJwtKeyPair.Create();
        var newKey = TestJwtKeyPair.Create();
        var service = new TestTokenValidator(Options(("old", oldKey), ("new", newKey)));

        Assert.Equal("alice", service.Validate(TestTokens.Mint(
            "alice", keyPair: oldKey, kid: "old")).FindFirst("sub")!.Value);
        Assert.Equal("bob", service.Validate(TestTokens.Mint(
            "bob", keyPair: newKey, kid: "new")).FindFirst("sub")!.Value);
    }

    [Theory]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("kid")]
    public void Validate_RejectsWrongIssuerAudienceOrUnknownKid(string mismatch)
    {
        var token = TestTokens.Mint(
            issuer: mismatch == "issuer" ? "wrong-issuer" : null,
            audience: mismatch == "audience" ? "wrong-audience" : null,
            kid: mismatch == "kid" ? "unknown" : null);

        Assert.ThrowsAny<SecurityTokenException>(() => new TestTokenValidator(Options()).Validate(token));
    }

    [Fact]
    public void Validate_RejectsMissingKid()
    {
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(
            issuer: TestTokens.Issuer,
            audience: TestTokens.Audience,
            claims: [new Claim("sub", "alice")],
            notBefore: now,
            expires: now.AddHours(1),
            signingCredentials: new SigningCredentials(
                TestTokens.ActiveKey.CreatePrivateSecurityKey(kid: string.Empty),
                SecurityAlgorithms.EcdsaSha256));
        token.Header.Remove(JwtHeaderParameterNames.Kid);
        var encoded = new JwtSecurityTokenHandler().WriteToken(token);

        Assert.ThrowsAny<SecurityTokenException>(() => new TestTokenValidator(Options()).Validate(encoded));
    }

    [Fact]
    public void Validate_RejectsHmacAlgorithmConfusionUsingPublicMaterial()
    {
        var now = DateTime.UtcNow;
        var publicMaterial = Convert.FromBase64String(TestTokens.ActiveKey.PublicPemBase64);
        var symmetric = new SymmetricSecurityKey(publicMaterial) { KeyId = TestTokens.ActiveKid };
        var token = new JwtSecurityToken(
            issuer: TestTokens.Issuer,
            audience: TestTokens.Audience,
            claims: [new Claim("sub", "alice")],
            notBefore: now,
            expires: now.AddHours(1),
            signingCredentials: new SigningCredentials(symmetric, SecurityAlgorithms.HmacSha256));
        var encoded = new JwtSecurityTokenHandler().WriteToken(token);

        Assert.ThrowsAny<SecurityTokenException>(() => new TestTokenValidator(Options()).Validate(encoded));
    }

    [Fact]
    public void Validate_RejectsExpiredAndNotYetValidTokens_WithZeroClockSkew()
    {
        var service = new TestTokenValidator(Options());
        Assert.Throws<SecurityTokenExpiredException>(() => service.Validate(TestTokens.Mint(
            notBefore: DateTime.UtcNow.AddHours(-2),
            expires: DateTime.UtcNow.AddHours(-1))));
        Assert.Throws<SecurityTokenNotYetValidException>(() => service.Validate(TestTokens.Mint(
            notBefore: DateTime.UtcNow.AddHours(1),
            expires: DateTime.UtcNow.AddHours(2))));
    }

    [Fact]
    public void PlatformSurface_HasNoIssuanceApiOrPrivateKey()
    {
        Assert.DoesNotContain(typeof(JwtService).GetMethods(), method => method.Name == "Issue");
        var options = Options();
        foreach (var key in options.VerificationKeys.Values)
        {
            Assert.ThrowsAny<CryptographicException>(() => key.ECDsa.ExportParameters(includePrivateParameters: true));
        }
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void Configuration_InvalidRing_FailsFast(string ring)
    {
        Assert.Throws<InvalidOperationException>(() =>
            JwtOptions.Resolve(TestTokens.Issuer, TestTokens.Audience, ring));
    }

    [Fact]
    public void Configuration_InvalidBase64OrPrivatePem_FailsFast()
    {
        var invalidBase64 = JsonSerializer.Serialize(new Dictionary<string, string> { ["kid"] = "***" });
        Assert.Throws<InvalidOperationException>(() =>
            JwtOptions.Resolve(TestTokens.Issuer, TestTokens.Audience, invalidBase64));

        var privateMaterial = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
            TestTokens.ActiveKey.CreatePrivateSecurityKey("kid").ECDsa.ExportPkcs8PrivateKeyPem()));
        var privateRing = JsonSerializer.Serialize(new Dictionary<string, string> { ["kid"] = privateMaterial });
        Assert.Throws<InvalidOperationException>(() =>
            JwtOptions.Resolve(TestTokens.Issuer, TestTokens.Audience, privateRing));
    }
}
