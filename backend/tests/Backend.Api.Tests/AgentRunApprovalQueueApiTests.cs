using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Backend.Api.Tests;

/// <summary>
/// O3 "discoverable approval queue" (04-operations-trigger-plan.md §4): GET /api/runs/approvals,
/// scope=visible|actionable, keyset pagination, and the DTO field boundary. Deliberately reuses
/// the InMemory-backed base <see cref="TestWebAppFactory"/> (same posture as
/// <see cref="AgentRunApprovalApiTests"/>) — the SQL itself is separately proved against real
/// Postgres by <c>AgentRunRepositoryTests.D7_ApprovalQueue_...ThroughDapper</c>. The 404
/// feature-gate parity for this route lives in <see cref="D7FeatureGateTests"/>.
/// </summary>
public sealed class AgentRunApprovalQueueApiTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _factory;
    public AgentRunApprovalQueueApiTests(TestWebAppFactory factory) => _factory = factory;

    /// <summary>
    /// The full O3 §4 acceptance matrix in one scenario: visible includes the owner's own runs
    /// regardless of role, and separately includes role-eligible non-owner approvals; actionable
    /// additionally excludes role mismatches and self-requested approvals; cross-tenant callers
    /// see nothing; and the queue's actionable flag has decision parity with the real endpoint.
    /// </summary>
    [Fact]
    public async Task Queue_VisibleAndActionablePredicates_MatchO3AndHaveDecisionParity()
    {
        // TestWebAppFactory is an IClassFixture — every test method in this class shares one
        // InMemory singleton, and the queue is cross-run/unscoped by design, so each test needs
        // its own tenant to avoid seeing another test's leftover approvals.
        var tenant = UniqueTenant();
        var otherTenant = UniqueTenant();

        // Run creation/lease/transition (AgentRunController) require the caller's role header to
        // be exactly ADMIN; the approval's own required_role field is a separate concept from
        // that header, so admin-a's D3-caller identity must use the ADMIN client throughout setup.
        using var owner = Client(tenant, "admin-a", "ADMIN");
        var agent = await AgentRunApprovalApiTests.PublishedUserAgentAsync(owner);

        var userRoleRun = await AgentRunApprovalApiTests.StartRunningAsync(owner, agent);
        var userRoleApproval = await AgentRunApprovalApiTests.CreateApprovalAsync(owner, userRoleRun, new string('a', 64), "USER");

        var adminRoleRun = await AgentRunApprovalApiTests.StartRunningAsync(owner, agent);
        var adminRoleApproval = await AgentRunApprovalApiTests.CreateApprovalAsync(owner, adminRoleRun, new string('b', 64), "ADMIN");

        // Owner sees both of their own runs' approvals regardless of required_role.
        Assert.Equal(2, (await Queue(owner, "visible")).Count);

        // A role-eligible, non-requesting USER approver sees only the USER-required approval
        // (never the ADMIN-required one), and it is actionable.
        using var approver = Client(tenant, "user-b", "USER");
        var approverVisible = await Queue(approver, "visible");
        var visibleItem = Assert.Single(approverVisible)!.AsObject();
        Assert.Equal(userRoleApproval["id"]!.GetValue<string>(), visibleItem["approval_id"]!.GetValue<string>());
        Assert.True(visibleItem["actionable"]!.GetValue<bool>());

        var approverActionable = await Queue(approver, "actionable");
        Assert.Equal(
            userRoleApproval["id"]!.GetValue<string>(),
            Assert.Single(approverActionable)!.AsObject()["approval_id"]!.GetValue<string>());

        // admin-a requested the USER-required approval itself: separation of duties excludes it
        // from "actionable to me" even under a USER role header that would otherwise match.
        using var ownerAsUserApprover = Client(tenant, "admin-a", "USER");
        Assert.Empty(await Queue(ownerAsUserApprover, "actionable"));

        // Cross-tenant caller sees neither list — indistinguishable from "nothing pending", not an error.
        using var crossTenant = Client(otherTenant, "user-b", "USER");
        Assert.Empty(await Queue(crossTenant, "visible"));
        Assert.Empty(await Queue(crossTenant, "actionable"));

        // Policy parity, first half: what the queue marked actionable, the decision endpoint accepts.
        var decided = await DecideAsync(approver, userRoleRun.Run["id"]!.GetValue<string>(), userRoleApproval["id"]!.GetValue<string>(), "queue-parity-accept");
        Assert.Equal(HttpStatusCode.OK, decided.StatusCode);

        // Policy parity, second half: what the queue excluded for role mismatch, the decision
        // endpoint independently rejects the same way (403, not just "not in the list").
        var mismatched = await DecideAsync(approver, adminRoleRun.Run["id"]!.GetValue<string>(), adminRoleApproval["id"]!.GetValue<string>(), "queue-parity-mismatch");
        Assert.Equal(HttpStatusCode.Forbidden, mismatched.StatusCode);
    }

    /// <summary>O3 §4 DTO boundary: exactly the allowed field set, never raw arguments/fingerprints/checkpoint/effect identity/lease.</summary>
    [Fact]
    public async Task Queue_ItemShape_ExposesOnlyTheAllowedO3Fields()
    {
        using var owner = Client(UniqueTenant(), "admin-a", "ADMIN");
        var agent = await AgentRunApprovalApiTests.PublishedUserAgentAsync(owner);
        var run = await AgentRunApprovalApiTests.StartRunningAsync(owner, agent);
        await AgentRunApprovalApiTests.CreateApprovalAsync(owner, run, new string('c', 64), "USER");

        var item = Assert.Single(await Queue(owner, "visible"))!.AsObject();

        Assert.Equal(
            new[]
            {
                "action_summary", "actionable", "agent_id", "agent_revision", "approval_id",
                "created_at", "expires_at", "required_role", "run_id", "status",
            },
            item.Select(pair => pair.Key).Order(StringComparer.Ordinal));
        Assert.Equal("runtime.write_evidence", item["action_summary"]!.GetValue<string>());
    }

    // The decision-table half: on-point vs off-point for the page-size boundary, plus proof that
    // the two pages do not overlap (the keyset cursor actually advances, not just re-slices).
    [Fact]
    public async Task Queue_Pagination_ReturnsHasMoreAndAdvancingCursor_WhenExceedingLimit()
    {
        using var owner = Client(UniqueTenant(), "admin-a", "ADMIN");
        var agent = await AgentRunApprovalApiTests.PublishedUserAgentAsync(owner);
        for (var i = 0; i < 3; i++)
        {
            var run = await AgentRunApprovalApiTests.StartRunningAsync(owner, agent);
            await AgentRunApprovalApiTests.CreateApprovalAsync(owner, run, new string((char)('d' + i), 64), "USER");
        }

        var firstPage = await owner.GetAsync("/api/runs/approvals?scope=visible&limit=2");
        Assert.Equal(HttpStatusCode.OK, firstPage.StatusCode);
        var firstBody = await firstPage.ReadJsonAsync();
        Assert.Equal(2, firstBody["items"]!.AsArray().Count);
        Assert.True(firstBody["has_more"]!.GetValue<bool>());
        var cursor = firstBody["next_cursor"]!.GetValue<string>();

        var secondPage = await owner.GetAsync($"/api/runs/approvals?scope=visible&limit=2&cursor={Uri.EscapeDataString(cursor)}");
        Assert.Equal(HttpStatusCode.OK, secondPage.StatusCode);
        var secondBody = await secondPage.ReadJsonAsync();
        Assert.Single(secondBody["items"]!.AsArray());
        Assert.False(secondBody["has_more"]!.GetValue<bool>());
        Assert.Null(secondBody["next_cursor"]);

        var firstIds = firstBody["items"]!.AsArray().Select(x => x!["approval_id"]!.GetValue<string>()).ToHashSet();
        var secondIds = secondBody["items"]!.AsArray().Select(x => x!["approval_id"]!.GetValue<string>()).ToHashSet();
        Assert.Empty(firstIds.Intersect(secondIds));
    }

    [Fact]
    public async Task Queue_RejectsUnknownScope()
    {
        using var owner = Client("demo-a", "admin-a", "USER");
        var response = await owner.GetAsync("/api/runs/approvals?scope=bogus");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // limit's off-points on both sides of the [1,100] range that the controller enforces.
    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task Queue_RejectsLimitOutOfRange(int limit)
    {
        using var owner = Client("demo-a", "admin-a", "USER");
        var response = await owner.GetAsync($"/api/runs/approvals?scope=visible&limit={limit}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Queue_RejectsMalformedCursor()
    {
        using var owner = Client("demo-a", "admin-a", "USER");
        var response = await owner.GetAsync("/api/runs/approvals?scope=visible&cursor=not-base64!!");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static Task<HttpResponseMessage> DecideAsync(HttpClient client, string runId, string approvalId, string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/runs/{runId}/approvals/{approvalId}/approve")
        {
            Content = JsonContent.Create(new { reason = idempotencyKey }),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        return client.SendAsync(request);
    }

    private static async Task<JsonArray> Queue(HttpClient client, string scope)
        => (await (await client.GetAsync($"/api/runs/approvals?scope={scope}")).ReadJsonAsync())["items"]!.AsArray();

    private HttpClient Client(string tenant, string user, string role)
        => _factory.CreateInternalClient().WithTenant(tenant).WithUser(user).WithRole(role);

    private static string UniqueTenant() => "demo-a-" + Guid.NewGuid().ToString("N");
}
