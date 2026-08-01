using System.Security.Claims;
using Platform.Service.Dtos;
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

    // capabilities 與 groups 的畸形處理刻意不同:groups 一筆畸形整組作廢(見
    // GetGroups_OneMalformedClaimRejectsWholeSet),capabilities 只逐筆過濾掉畸形項,
    // 其餘合法 grant 存活。這裡釘住現行行為,免得日後誤以為兩者對稱而改錯任一側。
    [Fact]
    public void GetCapabilities_OneMalformedClaimAmongValidOnes_KeepsOnlyValidEntries()
    {
        var principal = Principal(
            "USER",
            new Claim("capabilities", "workflow.manage"),
            new Claim("capabilities", "agent.author orchestrator.manage"));

        Assert.Equal(new[] { "workflow.manage" }, principal.GetCapabilities());
        Assert.True(principal.HasCapability("workflow.manage"));
        Assert.False(principal.HasCapability("agent.author"));
    }

    [Fact]
    public void GetCapabilities_Missing_ReturnsNull_HasCapabilityFalse()
    {
        var principal = Principal("ADMIN");

        Assert.Null(principal.GetCapabilities());
        Assert.False(principal.HasCapability("workflow.manage"));
    }

    // capabilities 與 groups 同樣是簽發端決定大小的 wire 資料,兩道界限(數量、聚合 UTF-8 位元組)
    // 必須對稱:超限整組 fail-closed,不回一個看起來合法的子集。
    // 四格 = 數量 on-point/off-point + wire byte on-point/off-point。

    [Fact]
    public void GetCapabilities_ExactlyAtMaxCount_Succeeds()
    {
        var claims = Enumerable.Range(0, UserCapabilityContract.MaxCapabilities)
            .Select(index => new Claim("capabilities", $"c{index:D3}"))
            .ToArray();
        var principal = Principal("ADMIN", claims);

        Assert.Equal(UserCapabilityContract.MaxCapabilities, principal.GetCapabilities()!.Count);
    }

    [Fact]
    public void GetCapabilities_OverMaxCountRejectsWholeSet()
    {
        var claims = Enumerable.Range(0, UserCapabilityContract.MaxCapabilities + 1)
            .Select(index => new Claim("capabilities", $"c{index:D3}"))
            .Append(new Claim("capabilities", "workflow.manage"))
            .ToArray();
        var principal = Principal("ADMIN", claims);

        Assert.Null(principal.GetCapabilities());
        Assert.False(principal.HasCapability("workflow.manage"));
    }

    [Fact]
    public void GetCapabilities_ExactlyAtWireByteBound_Succeeds()
    {
        var capabilities = GroupSet(exceedByOneByte: false);
        Assert.Equal(
            UserCapabilityContract.MaxCapabilitiesWireUtf8Bytes,
            System.Text.Encoding.UTF8.GetByteCount(string.Join(' ', capabilities)));
        var principal = Principal(
            "ADMIN", capabilities.Select(c => new Claim("capabilities", c)).ToArray());

        Assert.Equal(capabilities, principal.GetCapabilities()!);
    }

    [Fact]
    public void GetCapabilities_OverAggregateWireBoundRejectsWholeSet()
    {
        var claims = GroupSet(exceedByOneByte: true)
            .Select(capability => new Claim("capabilities", capability))
            .Append(new Claim("capabilities", "workflow.manage"))
            .ToArray();
        var principal = Principal("ADMIN", claims);

        Assert.Null(principal.GetCapabilities());
        Assert.False(principal.HasCapability("workflow.manage"));
    }

    // 數量下界(GetGroups 自有的 claims.Length == 0 early return):完全沒有 groups claim →
    // null 而非空集合,下游因此不帶 X-User-Groups header。capabilities 側已有對應測試。
    [Fact]
    public void GetGroups_Missing_ReturnsNull()
    {
        var principal = Principal("ADMIN");

        Assert.Null(principal.GetGroups());
        Assert.Null(principal.ToUserContext().Groups);
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

    // 上限的「放行側」:剛好 MaxGroups 筆(名稱夠短,不觸發 wire byte 上限)必須被接受;
    // 只測 257 拒絕的話,把上限誤打成 255 之類的縮水改動不會有任何測試失敗。
    [Fact]
    public void GetGroups_ExactlyAtMaxCount_Succeeds()
    {
        var claims = Enumerable.Range(0, UserGroupContract.MaxGroups)
            .Select(index => new Claim("groups", $"g{index:D3}"))
            .ToArray();
        var principal = Principal("ADMIN", claims);

        Assert.Equal(UserGroupContract.MaxGroups, principal.GetGroups()!.Count);
    }

    // 聚合位元組上限的「放行側」:恰好 MaxGroupsWireUtf8Bytes(2048)必須被接受(off-point 見上一個測試)。
    [Fact]
    public void GetGroups_ExactlyAtWireByteBound_Succeeds()
    {
        var groups = GroupSet(exceedByOneByte: false);
        Assert.Equal(
            UserGroupContract.MaxGroupsWireUtf8Bytes,
            System.Text.Encoding.UTF8.GetByteCount(string.Join(' ', groups)));
        var principal = Principal("ADMIN", groups.Select(g => new Claim("groups", g)).ToArray());

        Assert.Equal(groups.OrderBy(g => g, StringComparer.Ordinal), principal.GetGroups()!);
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
        var adminWithCap = Principal("ADMIN", new Claim("capabilities", "workflow.manage"));
        var unauthenticatedWithCap = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim("capabilities", "workflow.manage") }));
        var adminNoCap = Principal("ADMIN");
        var userNoCap = Principal("USER");

        Assert.True((await authz.AuthorizeAsync(withCap, null, "workflow.manage")).Succeeded);
        // policy 只看 capability,不看 role:ADMIN 帶 capability 同樣放行(角色既不加分也不扣分)。
        Assert.True((await authz.AuthorizeAsync(adminWithCap, null, "workflow.manage")).Succeeded);
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
