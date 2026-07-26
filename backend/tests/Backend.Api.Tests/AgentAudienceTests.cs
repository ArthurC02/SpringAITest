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
