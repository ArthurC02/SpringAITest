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
    }

    public sealed class DisabledFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("AGENT_WRITE_TOOLS_ENABLED", "false");
        }
    }
}
