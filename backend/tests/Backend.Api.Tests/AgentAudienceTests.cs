using System.Reflection;
using System.Text.RegularExpressions;
using Backend.Api.Agents;
using Backend.Api.Common;
using Microsoft.AspNetCore.Http;

namespace Backend.Api.Tests;

public sealed class AgentAudienceTests
{
    [Fact]
    public void LifecycleNormalization_OnlyMigratesLegacyBareRoles()
    {
        Assert.Equal("role:ADMIN", AgentAudience.NormalizeAuthoringEntry("ADMIN"));
        Assert.Equal("role:USER", AgentAudience.NormalizeAuthoringEntry("USER"));
        Assert.Equal(
            "group:finance-reviewers",
            AgentAudience.NormalizeAuthoringEntry("group:finance-reviewers"));

        var legacyFreeString =
            AgentAudience.NormalizeAuthoringEntry("finance-reviewers");
        Assert.Equal("finance-reviewers", legacyFreeString);
        Assert.False(AgentAudience.IsCanonicalEntry(legacyFreeString));
    }

    [Theory]
    [InlineData("role:admin", "role:ADMIN")]   // 已帶前綴但大小寫不正 → 修正為正規值
    [InlineData("role:bogus", "role:bogus")]   // 前綴後不是已知角色 → 原樣放行,不憑空造 role
    public void AuthoringNormalization_RolePrefixedEntryUppercasesOnlyKnownRoles(
        string raw,
        string expected)
        => Assert.Equal(expected, AgentAudience.NormalizeAuthoringEntry(raw));

    [Theory]
    [InlineData("role:ADMIN", true)]
    [InlineData("group:finance-reviewers", true)]
    [InlineData("role:BOGUS", false)]
    [InlineData(null, false)]
    public void CanonicalEntry_AcceptsOnlyKnownRolesAndCanonicalGroupIds(
        string? value,
        bool expected)
        => Assert.Equal(expected, AgentAudience.IsCanonicalEntry(value));

    // 決策表另一半:sibling 測 allowLegacyPublishedRoles=true,這裡補 false 的兩個組合。
    [Theory]
    [InlineData("role:ADMIN", true)]  // 正規 role 條目走主分支,不受相容旗標影響
    [InlineData("ADMIN", false)]      // 舊式裸角色只有相容旗標開著才算數
    public void Eligibility_WithoutLegacyCompatibility_OnlyCanonicalRoleEntryMatches(
        string audienceEntry,
        bool expected)
        => Assert.Equal(
            expected,
            AgentAudience.Matches(
                new[] { audienceEntry },
                "ADMIN",
                Array.Empty<string>(),
                allowLegacyPublishedRoles: false));

    [Fact]
    public void Eligibility_AllowsExplicitGroupAndLegacyBareRoleOnly()
    {
        Assert.True(AgentAudience.Matches(
            new[] { "group:finance-reviewers" },
            "ADMIN",
            new[] { "finance-reviewers" },
            allowLegacyPublishedRoles: true));
        Assert.True(AgentAudience.Matches(
            new[] { "ADMIN" },
            "ADMIN",
            Array.Empty<string>(),
            allowLegacyPublishedRoles: true));
        Assert.False(AgentAudience.Matches(
            new[] { "finance-reviewers" },
            "USER",
            new[] { "finance-reviewers" },
            allowLegacyPublishedRoles: true));
        // audience 可以合法地是空陣列(Validate 對 audience 沒有非空要求)→ 空 audience 誰都不符。
        Assert.False(AgentAudience.Matches(
            Array.Empty<string>(),
            "ADMIN",
            Array.Empty<string>(),
            allowLegacyPublishedRoles: true));
    }

    [Theory]
    [InlineData("operations\n")]
    [InlineData("operations\t")]
    [InlineData(" operations")]
    [InlineData("operations ")]
    [InlineData("operations  reviewers")]
    public void CanonicalGroupIdAndHeader_RejectNonCanonicalWireGrammar(
        string rawHeader)
    {
        Assert.False(AgentAudience.IsCanonicalGroupId(rawHeader));

        var context = new DefaultHttpContext();
        context.Request.Headers[IdentityHeaders.GroupsHeader] = rawHeader;
        var error = Assert.Throws<ApiException>(
            () => context.Request.UserGroups());
        Assert.Equal(StatusCodes.Status400BadRequest, error.Status);
    }

    [Fact]
    public void CanonicalGroupSet_ExactAggregateAccepted_PlusOneHeaderRejected()
    {
        var exact = GroupSet(exceedByOneByte: false);
        var plusOne = GroupSet(exceedByOneByte: true);
        Assert.True(AgentAudience.IsCanonicalGroupSet(exact));
        Assert.False(AgentAudience.IsCanonicalGroupSet(plusOne));

        var accepted = new DefaultHttpContext();
        accepted.Request.Headers[IdentityHeaders.GroupsHeader] =
            string.Join(' ', exact);
        Assert.Equal(exact, accepted.Request.UserGroups());

        var rejected = new DefaultHttpContext();
        rejected.Request.Headers[IdentityHeaders.GroupsHeader] =
            string.Join(' ', plusOne);
        var error = Assert.Throws<ApiException>(
            () => rejected.Request.UserGroups());
        Assert.Equal(StatusCodes.Status400BadRequest, error.Status);
    }

    [Fact]
    public void CanonicalGroupSet_ExactCallerGroupCountAccepted_PlusOneRejected()
    {
        // 兩組的 wire 位元組數都遠低於 2048,只有數量跨過 MaxCallerGroups → 單獨驗數量邊界。
        Assert.True(AgentAudience.IsCanonicalGroupSet(
            CountedGroupSet(AgentAudience.MaxCallerGroups)));
        Assert.False(AgentAudience.IsCanonicalGroupSet(
            CountedGroupSet(AgentAudience.MaxCallerGroups + 1)));
    }

    [Fact]
    public void CanonicalGroupSet_RejectsDuplicateGroupIds()
    {
        Assert.True(AgentAudience.IsCanonicalGroupId("finance-reviewers"));
        Assert.False(AgentAudience.IsCanonicalGroupSet(
            new[] { "finance-reviewers", "finance-reviewers" }));
    }

    // 契約鏡像釘樁:這三個上限與 group id regex 與 platform 的 UserGroupContract
    // (platform/src/Platform.Service/Dtos/UserContext.cs)逐字相同。任一邊放寬,另一邊會靜默
    // 丟棄整組 group(而非回錯)—— 授權無聲消失。單邊漂移必須讓兩套件其中一支變紅。
    [Fact]
    public void WireContract_PinsBoundsAndGroupIdPatternMirroredInPlatform()
    {
        Assert.Equal(128, AgentAudience.MaxGroupIdLength);
        Assert.Equal(256, AgentAudience.MaxCallerGroups);
        Assert.Equal(2_048, AgentAudience.MaxGroupsWireUtf8Bytes);
        var regex = typeof(AgentAudience)
            .GetMethod(
                "CanonicalGroupIdRegex",
                BindingFlags.NonPublic | BindingFlags.Static)!
            .GetCustomAttribute<GeneratedRegexAttribute>()!;
        Assert.Equal("^[a-z0-9](?:[a-z0-9._-]{0,126}[a-z0-9])?\\z", regex.Pattern);
        // Options 與 Pattern 同等載重:單邊加上 IgnoreCase 會讓同一字串在兩服務判定不同,
        // 只釘 Pattern 的話兩支測試仍全綠,正是上面警告的「靜默丟組」失效模式。
        Assert.Equal(RegexOptions.CultureInvariant, regex.Options);
    }

    private static string[] CountedGroupSet(int count)
        => Enumerable.Range(0, count)
            .Select(index => $"g{index:D3}")
            .ToArray();

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
