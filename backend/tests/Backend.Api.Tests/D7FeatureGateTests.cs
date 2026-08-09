using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Backend.Api.Tests;

public sealed class D7FeatureGateTests : IClassFixture<D7FeatureGateTests.DisabledFactory>
{
    private readonly DisabledFactory _factory;
    public D7FeatureGateTests(DisabledFactory factory) => _factory = factory;

    public static TheoryData<HttpMethod, string> HiddenRoutes => new()
    {
        { HttpMethod.Get, $"/api/runs/{Guid.NewGuid():D}/approvals" },
        // O3 discoverable approval queue: same gate posture as every other D7 route.
        { HttpMethod.Get, "/api/runs/approvals" },
        // The two state-mutating decision routes: the gate must win over the mandatory
        // Idempotency-Key 400 inside Decide(), which never runs because the middleware
        // short-circuits before MVC — these requests carry no Idempotency-Key header.
        { HttpMethod.Post, $"/api/runs/{Guid.NewGuid():D}/approvals/{Guid.NewGuid():D}/approve" },
        { HttpMethod.Post, $"/api/runs/{Guid.NewGuid():D}/approvals/{Guid.NewGuid():D}/reject" },
        { HttpMethod.Post, $"/api/agent-runs/{Guid.NewGuid():D}/approvals" },
        { HttpMethod.Post, $"/api/agent-runs/{Guid.NewGuid():D}/approvals/{Guid.NewGuid():D}/consume" },
        { HttpMethod.Get, $"/api/agent-runs/{Guid.NewGuid():D}/approvals/{Guid.NewGuid():D}/execution-identity" },
        { HttpMethod.Post, $"/api/agent-runs/{Guid.NewGuid():D}/write-effects/{Guid.NewGuid():D}/evidence" },
        { HttpMethod.Post, $"/api/agent-runs/{Guid.NewGuid():D}/write-effects/{Guid.NewGuid():D}/complete" },
        { HttpMethod.Post, $"/api/agent-runs/{Guid.NewGuid():D}/approvals/{Guid.NewGuid():D}/execute/claim" },
        { HttpMethod.Post, "/api/agent-run-approval-executions/recovery/claim" },
        { HttpMethod.Post, $"/api/agent-run-approval-executions/{Guid.NewGuid():D}/complete" },
        { HttpMethod.Post, "/api/operations/telemetry" },
        { HttpMethod.Get, "/api/admin/operations/metrics" },
    };

    [Theory]
    [MemberData(nameof(HiddenRoutes))]
    public async Task WriteFeatureOff_HidesEveryPublicAndInternalD7RouteBeforeTokenAndBodyParsing(
        HttpMethod method,
        string path)
    {
        using var request = new HttpRequestMessage(method, path);
        if (method != HttpMethod.Get)
            request.Content = JsonContent.Create(new { deliberately_invalid = true });

        var response = await _factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        // 決策表的另一半:404 的 body 也是契約 —— 而契約刻意是「與一般找不到資源**無法區分**」。
        // 任何專屬於 feature gate 的訊息(舊值 "Feature is unavailable")都等於告訴呼叫端這裡有個關著的功能。
        Assert.Equal("找不到資源", (await response.ReadJsonAsync())["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task WriteFeatureOff_UsesSameNotFoundContractAsUnknownRoute_ForTrustedCaller()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Internal-Token", TestWebAppFactory.InternalToken);
        using var gated = await client.GetAsync("/api/admin/operations/metrics");
        using var unknown = await client.GetAsync("/api/not-a-route");

        Assert.Equal(HttpStatusCode.NotFound, gated.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        var gatedBody = await gated.ReadJsonAsync();
        var unknownBody = await unknown.ReadJsonAsync();
        gatedBody.AssertApiError(404, "not_found");
        unknownBody.AssertApiError(404, "not_found");
        Assert.Equal(gatedBody["message"]!.GetValue<string>(), unknownBody["message"]!.GetValue<string>());
        Assert.Equal(gated.Content.Headers.ContentType?.MediaType, unknown.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task WriteFeatureOff_UsesSameNotFoundContractAsUnknownRoute_BeforeTokenValidation()
    {
        using var client = _factory.CreateClient();
        using var gated = await client.GetAsync("/api/admin/operations/metrics");
        using var unknown = await client.GetAsync("/api/not-a-route");

        Assert.Equal(HttpStatusCode.NotFound, gated.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        var gatedBody = await gated.ReadJsonAsync();
        var unknownBody = await unknown.ReadJsonAsync();
        gatedBody.AssertApiError(404, "not_found");
        unknownBody.AssertApiError(404, "not_found");
        Assert.Equal(gatedBody["message"]!.GetValue<string>(), unknownBody["message"]!.GetValue<string>());
    }

    public sealed class DisabledFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            TestWebAppFactory.ConfigureCredentials(builder);
            builder.UseSetting("AGENT_WRITE_TOOLS_ENABLED", "false");
        }
    }
}
