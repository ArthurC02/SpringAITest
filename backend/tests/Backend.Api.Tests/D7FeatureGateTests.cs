using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Backend.Api.Tests;

public sealed class D7FeatureGateTests
{
    public static TheoryData<HttpMethod, string> HiddenRoutes => new()
    {
        { HttpMethod.Get, $"/api/runs/{Guid.NewGuid():D}/approvals" },
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
        using var factory = new DisabledFactory();
        using var request = new HttpRequestMessage(method, path);
        if (method != HttpMethod.Get)
            request.Content = JsonContent.Create(new { deliberately_invalid = true });

        var response = await factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private sealed class DisabledFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("AGENT_WRITE_TOOLS_ENABLED", "false");
        }
    }
}
