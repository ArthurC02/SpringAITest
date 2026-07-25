using System.Net;
using System.Net.Http.Json;

namespace Platform.Web.Tests;

[Collection("EngineCalls")]
public sealed class RunApprovalApiTests
{
    private const string RunId = FakeAgentRunService.RunIdText;
    private const string ApprovalId = "66666666-6666-4666-8666-666666666666";

    [Fact]
    public async Task FeatureOff_HidesApprovalRoutesBeforeAuthentication()
    {
        using var factory = new TestWebAppFactory(agentWriteToolsEnabled: false);
        var before = FakeAgentRunService.Calls.Count;

        var response = await factory.CreateClient().GetAsync($"/api/runs/{RunId}/approvals");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(before, FakeAgentRunService.Calls.Count);
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("USER", HttpStatusCode.OK)]
    [InlineData("ADMIN", HttpStatusCode.OK)]
    public async Task EnabledApprovalList_RequiresAuthenticationButNotAdmin(string? role, HttpStatusCode expected)
    {
        using var factory = new TestWebAppFactory(agentWriteToolsEnabled: true);
        var client = factory.CreateClient();
        if (role is not null)
        {
            client = client.WithToken(factory.IssueToken("business-approver", role, "tenant-x"));
        }

        var response = await client.GetAsync($"/api/runs/{RunId}/approvals");

        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.OK)
        {
            Assert.Equal("business-approver", FakeAgentRunService.LastContext!.UserId);
            Assert.Equal("tenant-x", FakeAgentRunService.LastContext.TenantCode);
        }
    }

    [Fact]
    public async Task Decision_ForwardsOnlySignedIdentityReasonAndIdempotencyKey()
    {
        using var factory = new TestWebAppFactory(agentWriteToolsEnabled: true);
        var client = factory.CreateClient().WithToken(factory.IssueToken("approver", "USER", "tenant-x"));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/runs/{RunId}/approvals/{ApprovalId}/approve")
        {
            Content = JsonContent.Create(new { reason = "reviewed" }),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", "approval-attempt-1");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Contains($"approval:{RunId}:{ApprovalId}:True:reviewed:approval-attempt-1:approver", FakeAgentRunService.Calls);
    }
}
