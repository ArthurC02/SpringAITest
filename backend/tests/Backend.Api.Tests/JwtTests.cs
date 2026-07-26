using System.IdentityModel.Tokens.Jwt;
using System.Text;
using Backend.Api.Agents;
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

    // 到期邊界:platform 的驗證參數 ClockSkew=Zero,所以 exp 一過就必須立刻失效,沒有隱性寬限期。
    // (exp == nbf 無法簽發 — JwtSecurityToken 要求 Expires > NotBefore,所以取最小可簽發壽命 1 秒。)
    [Fact]
    public async Task Issue_TokenPastExactExpiry_FailsPlatformValidation_WithNoClockSkewGrace()
    {
        var token = new JwtService(Secret, TimeSpan.FromSeconds(1)).Issue("alice", "USER", "demo-a");
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var jwt = handler.ReadJwtToken(token);
        Assert.Equal(TimeSpan.FromSeconds(1), jwt.ValidTo - jwt.ValidFrom);

        await Task.Delay(1_200);

        Assert.Throws<SecurityTokenExpiredException>(() =>
            handler.ValidateToken(token, PlatformParams(Secret), out _));
    }

    [Fact]
    public void Issue_WithDifferentSecret_FailsPlatformValidation()
    {
        var token = Service(secret: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa").Issue("alice", "USER", "demo-a");

        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        Assert.ThrowsAny<SecurityTokenException>(() =>
            handler.ValidateToken(token, PlatformParams("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"), out _));
    }

    // ---- A-DATA-14/15:capabilities claim(如 workflow.manage)只在使用者具備時簽入 ----

    [Fact] // capability principal:token 帶 capabilities claim,role/tenantCode 不受影響。
    public void Issue_WithCapability_SignsCapabilitiesClaim()
    {
        var token = Service().Issue("sysadmin", "ADMIN", "demo-a", new[] { "workflow.manage" });

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.Contains(jwt.Claims, c => c.Type == "capabilities" && c.Value == "workflow.manage");
        Assert.Equal("ADMIN", jwt.Claims.Single(c => c.Type == "role").Value);
    }

    [Theory] // A-DATA-14:單純 tenant ADMIN 與 USER 均**不**自動取得 capability(fail closed)。
    [InlineData("ADMIN")]
    [InlineData("USER")]
    public void Issue_WithoutCapability_HasNoCapabilitiesClaim(string role)
    {
        var token = Service().Issue("bob", role, "demo-a");

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.DoesNotContain(jwt.Claims, c => c.Type == "capabilities");
    }

    [Fact] // 位元相容:不帶 capabilities 時,claim 集合仍恰為既有四個(名稱/存在性不變)。
    public void Issue_WithoutCapability_KeepsExactExistingClaimSet()
    {
        var token = Service().Issue("alice", "ADMIN", "demo-a");

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        // 既有 payload claims(含 JwtSecurityToken 自帶的 exp/nbf)恰為這些;無 capabilities。
        Assert.Equal(
            new[] { "exp", "iat", "nbf", "role", "sub", "tenantCode" },
            jwt.Claims.Select(c => c.Type).OrderBy(t => t, StringComparer.Ordinal).ToArray());
    }

    [Fact] // A-DATA-15:SYSTEM_ADMIN 標籤只映射 capability policy,不形成繞過的隱含角色 —
           // 具 workflow.manage 的使用者 role 仍是原本的(此例 USER),不被升格成 ADMIN。
    public void Issue_CapabilityDoesNotImplyRoleElevation()
    {
        var token = Service().Issue("power-user", "USER", "demo-a", new[] { "workflow.manage" });

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.Equal("USER", jwt.Claims.Single(c => c.Type == "role").Value);
        Assert.Contains(jwt.Claims, c => c.Type == "capabilities" && c.Value == "workflow.manage");
    }

    [Fact]
    public void Issue_SignsCanonicalDistinctGroupClaimsInOrdinalOrder()
    {
        var token = Service().Issue(
            "alice",
            "ADMIN",
            "demo-a",
            groups: new[]
            {
                "zeta",
                "operations",
                "operations",
            });

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.Equal(
            new[] { "operations", "zeta" },
            jwt.Claims
                .Where(claim => claim.Type == "groups")
                .Select(claim => claim.Value)
                .ToArray());
    }

    [Theory]
    [InlineData("*")]
    [InlineData("Operations")]
    [InlineData("group:operations")]
    [InlineData("operations finance")]
    [InlineData("operations\n")]
    public void Issue_OneMalformedGroupRejectsWholeSet(string malformed)
    {
        Assert.Throws<InvalidOperationException>(() => Service().Issue(
            "alice",
            "ADMIN",
            "demo-a",
            groups: new[] { "operations", malformed }));
    }

    [Fact]
    public void Issue_OverBoundGroupSetRejectsWholeSet()
    {
        Assert.Throws<InvalidOperationException>(() => Service().Issue(
            "alice",
            "ADMIN",
            "demo-a",
            groups: Enumerable.Range(0, AgentAudience.MaxCallerGroups + 1)
                .Select(index => $"group-{index:D3}")
                .ToArray()));
    }

    [Fact]
    public void Issue_GroupWireExactBoundFitsDeployedAuthorizationHeader_PlusOneFails()
    {
        var exact = GroupSet(exceedByOneByte: false);
        var plusOne = GroupSet(exceedByOneByte: true);
        Assert.Equal(
            AgentAudience.MaxGroupsWireUtf8Bytes,
            Encoding.UTF8.GetByteCount(string.Join(' ', exact)));
        Assert.Equal(
            AgentAudience.MaxGroupsWireUtf8Bytes + 1,
            Encoding.UTF8.GetByteCount(string.Join(' ', plusOne)));

        var capabilities = Enumerable.Range(0, 16)
            .Select(index => $"tool.use:t{index:D2}{new string('a', 48)}")
            .ToArray();
        var token = Service().Issue(
            "alice",
            "ADMIN",
            "demo-a",
            capabilities,
            exact);
        var authorizationBytes = Encoding.ASCII.GetByteCount("Bearer " + token);
        Assert.True(authorizationBytes < JwtService.MaxAuthorizationValueBytes);
        Assert.True(authorizationBytes < 8 * 1_024);
        Assert.Throws<InvalidOperationException>(() => Service().Issue(
            "alice",
            "ADMIN",
            "demo-a",
            groups: plusOne));
    }

    private static string[] GroupSet(bool exceedByOneByte)
        => Enumerable.Range(0, 16)
            .Select(index =>
            {
                var length = index == 0 || exceedByOneByte && index == 1
                    ? 128
                    : 127;
                var prefix = $"g{index:D2}";
                return prefix + new string('a', length - prefix.Length);
            })
            .ToArray();
}
