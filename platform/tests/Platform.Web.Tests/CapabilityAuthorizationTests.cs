using System.Security.Claims;
using Platform.Web.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Platform.Web.Tests;

/// <summary>
/// capabilities claim → workflow.manage policy 映射(D1)。驗兩件事:
/// (a) ClaimsPrincipal 只解析 backend canonical repeated claims，畸形複合字串 fail-closed;
/// (b) 註冊的 workflow.manage authorization policy 只放行帶該 capability 的 principal,
///     未認證、單純 tenant ADMIN 與 USER 都拒絕(不新增可繞過 tenant/policy 的隱含超級角色)。
/// </summary>
public sealed class CapabilityAuthorizationTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _factory;

    public CapabilityAuthorizationTests(TestWebAppFactory factory) => _factory = factory;

    private static ClaimsPrincipal Principal(string role, params Claim[] extra)
    {
        var claims = new List<Claim>
        {
            new(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub, "user-a"),
            new("role", role),
            new("tenantCode", "demo-a"),
        };
        claims.AddRange(extra);
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    // ---- (a) 解析:backend canonical 多個同名 claim;複合字串/缺席 → 無 capability ----

    [Fact]
    public void GetCapabilities_ArrayForm_ParsesEach()
    {
        var principal = Principal(
            "USER", new Claim("capabilities", "workflow.manage"), new Claim("capabilities", "agent.author"));

        Assert.True(principal.HasCapability("workflow.manage"));
        Assert.True(principal.HasCapability("agent.author"));
        Assert.False(principal.HasCapability("orchestrator.manage"));
    }

    [Fact]
    public void GetCapabilities_SpaceDelimitedSingleClaim_IsRejectedFailClosed()
    {
        var principal = Principal("USER", new Claim("capabilities", "workflow.manage agent.author"));

        Assert.Null(principal.GetCapabilities());
        Assert.False(principal.HasCapability("workflow.manage"));
        Assert.False(principal.HasCapability("agent.author"));
    }

    [Fact]
    public void GetCapabilities_Missing_ReturnsNull_HasCapabilityFalse()
    {
        var principal = Principal("ADMIN");

        Assert.Null(principal.GetCapabilities());
        Assert.False(principal.HasCapability("workflow.manage"));
    }

    [Fact]
    public void GetGroups_RepeatedCanonicalClaimsReturnSortedAtomicSet()
    {
        var principal = Principal(
            "ADMIN",
            new Claim("groups", "zeta"),
            new Claim("groups", "operations"));

        Assert.Equal(
            new[] { "operations", "zeta" },
            principal.GetGroups());
        Assert.Equal(
            new[] { "operations", "zeta" },
            principal.ToUserContext().Groups);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("Operations")]
    [InlineData("group:operations")]
    [InlineData("operations finance")]
    [InlineData("operations,finance")]
    [InlineData("operations\n")]
    public void GetGroups_OneMalformedClaimRejectsWholeSet(string malformed)
    {
        var principal = Principal(
            "ADMIN",
            new Claim("groups", "operations"),
            new Claim("groups", malformed));

        Assert.Null(principal.GetGroups());
        Assert.Null(principal.ToUserContext().Groups);
    }

    [Fact]
    public void GetGroups_OverBoundRejectsWholeSet()
    {
        var claims = Enumerable.Range(0, 257)
            .Select(index => new Claim("groups", $"group-{index:D3}"))
            .ToArray();
        var principal = Principal("ADMIN", claims);

        Assert.Null(principal.GetGroups());
    }

    [Fact]
    public void GetGroups_DuplicateClaimRejectsWholeSet()
    {
        var principal = Principal(
            "ADMIN",
            new Claim("groups", "operations"),
            new Claim("groups", "operations"));

        Assert.Null(principal.GetGroups());
    }

    [Fact]
    public void GetGroups_OverAggregateWireBoundRejectsWholeSet()
    {
        var claims = GroupSet(exceedByOneByte: true)
            .Select(group => new Claim("groups", group))
            .ToArray();
        var principal = Principal("ADMIN", claims);

        Assert.Null(principal.GetGroups());
    }

    // ---- (b) policy 映射 ----

    [Fact]
    public async Task WorkflowManagePolicy_OnlyCapabilityPrincipalSucceeds()
    {
        using var scope = _factory.Services.CreateScope();
        var authz = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();

        var withCap = Principal("USER", new Claim("capabilities", "workflow.manage"));
        var unauthenticatedWithCap = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim("capabilities", "workflow.manage") }));
        var adminNoCap = Principal("ADMIN");
        var userNoCap = Principal("USER");

        Assert.True((await authz.AuthorizeAsync(withCap, null, "workflow.manage")).Succeeded);
        Assert.False((await authz.AuthorizeAsync(
            unauthenticatedWithCap, null, "workflow.manage")).Succeeded);
        // 單純 tenant ADMIN 不自動取得 —— 不升格所有 ADMIN。
        Assert.False((await authz.AuthorizeAsync(adminNoCap, null, "workflow.manage")).Succeeded);
        Assert.False((await authz.AuthorizeAsync(userNoCap, null, "workflow.manage")).Succeeded);
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
