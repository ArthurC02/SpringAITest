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
}
