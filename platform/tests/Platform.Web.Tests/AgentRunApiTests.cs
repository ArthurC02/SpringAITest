using System.Net;
using System.Net.Http.Json;

namespace Platform.Web.Tests;

[Collection("EngineCalls")]
public sealed class AgentRunApiTests
{
    private const string AgentId = FakeAgentService.ExistingIdText;
    private const string RunId = FakeAgentRunService.RunIdText;

    [Theory]
    [InlineData("POST", "/api/agents/" + AgentId + "/runs")]
    [InlineData("POST", "/api/agents/" + AgentId + "/runs/")]
    [InlineData("GET", "/api/runs/" + RunId)]
    [InlineData("GET", "/api/runs/" + RunId + "/events")]
    [InlineData("POST", "/api/runs/" + RunId + "/resume")]
    [InlineData("POST", "/api/runs/" + RunId + "/cancel")]
    public async Task TestFlagOff_FailsClosedBeforeAuthentication(string method, string path)
    {
        using var factory = new TestWebAppFactory(
            agentBuilderEnabled: true,
            agentTestRunEnabled: false);
        var before = FakeAgentRunService.Calls.Count;

        var response = await factory.CreateClient().SendAsync(Request(method, path));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(before, FakeAgentRunService.Calls.Count);
    }

    [Fact]
    public async Task TestFlagCannotEnableWhenBuilderIsOff()
    {
        using var factory = new TestWebAppFactory(
            agentBuilderEnabled: false,
            agentTestRunEnabled: true);

        var response = await factory.CreateClient().GetAsync("/api/runs/" + RunId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("USER", HttpStatusCode.Forbidden)]
    public async Task EnabledRoutes_RequireAdmin(string? role, HttpStatusCode expected)
    {
        using var factory = EnabledFactory();
        var client = factory.CreateClient();
        if (role is not null)
        {
            client = client.WithToken(factory.IssueToken("caller", role, "tenant-x"));
        }

        var response = await client.GetAsync("/api/runs/" + RunId);

        Assert.Equal(expected, response.StatusCode);
    }

    // EnabledRoutes_RequireAdmin 只涵蓋被擋下的兩格(匿名 401、USER 403);ADMIN 這格證明 Get 真的
    // 把 runId 往下轉發、並把下游的 200 body 原樣寫回(start/resume/events 各有成功案例,Get 之前沒有)。
    [Fact]
    public async Task Get_AsAdmin_ForwardsRunIdAndWritesProxiedBody()
    {
        using var factory = EnabledFactory();
        var client = factory.CreateClient().WithToken(
            factory.IssueToken("admin-x", "ADMIN", "tenant-x"));

        var response = await client.GetAsync("/api/runs/" + RunId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains($"get:{RunId}", FakeAgentRunService.Calls);
        var body = await response.ReadJsonAsync();
        Assert.Equal(RunId, body["id"]!.GetValue<string>());
        Assert.Equal("queued", body["status"]!.GetValue<string>());
        Assert.Equal("admin-x", FakeAgentRunService.LastContext!.UserId);
        Assert.Equal("tenant-x", FakeAgentRunService.LastContext.TenantCode);
    }

    [Fact]
    public async Task Start_ForwardsJwtIdentityInputAndIdempotencyKey()
    {
        using var factory = EnabledFactory();
        var client = factory.CreateClient().WithToken(
            factory.IssueToken(
                "admin-x",
                "ADMIN",
                "tenant-x",
                new[]
                {
                    "tool.use:local.calculator",
                    "knowledge.read:aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
                },
                groups: new[] { "operations", "reviewers" }));
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/agents/" + AgentId + "/runs")
        {
            Content = JsonContent.Create(new { message = "hello" }),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", "start-key");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Contains(
            $"start:{AgentId}:hello:start-key",
            FakeAgentRunService.Calls);
        Assert.Equal("admin-x", FakeAgentRunService.LastContext!.UserId);
        Assert.Equal("tenant-x", FakeAgentRunService.LastContext.TenantCode);
        Assert.Equal(
            new[]
            {
                "tool.use:local.calculator",
                "knowledge.read:aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
            }.OrderBy(value => value, StringComparer.Ordinal),
            FakeAgentRunService.LastContext.Capabilities!
                .OrderBy(value => value, StringComparer.Ordinal));
        Assert.Equal(
            new[] { "operations", "reviewers" },
            FakeAgentRunService.LastContext.Groups);
    }

    [Fact]
    public async Task Start_MalformedSignedGroupClaimDiscardsWholeGroupSet()
    {
        using var factory = EnabledFactory();
        var client = factory.CreateClient().WithToken(
            factory.IssueToken(
                "admin-x",
                "ADMIN",
                "tenant-x",
                groups: new[] { "operations", "*" }));
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/agents/" + AgentId + "/runs")
        {
            Content = JsonContent.Create(new { message = "hello" }),
        };
        request.Headers.TryAddWithoutValidation(
            "Idempotency-Key",
            "malformed-group-key");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Null(FakeAgentRunService.LastContext!.Groups);
    }

    [Fact]
    public async Task EventsAndResume_MapCamelCasePublicContract()
    {
        using var factory = EnabledFactory();
        var client = factory.CreateClient().WithToken(
            factory.IssueToken("admin-x", "ADMIN", "tenant-x"));

        var events = await client.GetAsync(
            $"/api/runs/{RunId}/events?afterSequence=12&limit=50");
        // 無 query string → 前端依賴的 camelCase 參數各自套用預設值(afterSequence=0、limit=100);
        // 這兩個數字是公開契約的一部分,改動會讓輪詢從頭重播或截斷。
        var defaults = await client.GetAsync($"/api/runs/{RunId}/events");
        using var resume = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/runs/{RunId}/resume")
        {
            Content = JsonContent.Create(new
            {
                input = new { message = "details" },
                expectedCheckpointVersion = 4,
            }),
        };
        resume.Headers.TryAddWithoutValidation("Idempotency-Key", "resume-key");
        var resumed = await client.SendAsync(resume);

        Assert.Equal(HttpStatusCode.OK, events.StatusCode);
        Assert.Equal(HttpStatusCode.OK, defaults.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, resumed.StatusCode);
        Assert.Contains($"events:{RunId}:12:50", FakeAgentRunService.Calls);
        Assert.Contains($"events:{RunId}:0:100", FakeAgentRunService.Calls);
        Assert.Contains(
            $"resume:{RunId}:details:4:resume-key",
            FakeAgentRunService.Calls);
    }

    // Cancel 之前只在「旗標關閉」那格被打過(中介軟體先攔,controller 從未執行);啟用+ADMIN 這格證明
    // reason 與 Idempotency-Key 都原樣往下送,且下游的 202 原樣寫回。
    [Fact]
    public async Task Cancel_AsAdmin_ForwardsReasonAndIdempotencyKey()
    {
        using var factory = EnabledFactory();
        var client = factory.CreateClient().WithToken(
            factory.IssueToken("admin-x", "ADMIN", "tenant-x"));
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/runs/{RunId}/cancel")
        {
            Content = JsonContent.Create(new { reason = "手動中止" }),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", "cancel-key");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Contains($"cancel:{RunId}:手動中止:cancel-key", FakeAgentRunService.Calls);
        Assert.Equal("admin-x", FakeAgentRunService.LastContext!.UserId);
        Assert.Equal("tenant-x", FakeAgentRunService.LastContext.TenantCode);
    }

    private static TestWebAppFactory EnabledFactory()
        => new(agentBuilderEnabled: true, agentTestRunEnabled: true);

    private static HttpRequestMessage Request(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST" && path.EndsWith("/runs", StringComparison.Ordinal))
        {
            request.Content = JsonContent.Create(new { message = "hello" });
        }
        else if (method == "POST" && path.EndsWith("/resume", StringComparison.Ordinal))
        {
            request.Content = JsonContent.Create(new
            {
                input = new { message = "hello" },
                expectedCheckpointVersion = 1,
            });
        }
        return request;
    }
}
