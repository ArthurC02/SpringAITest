using System.Net;
using Platform.Service.Dtos;

namespace Platform.Service.Tests;

/// <summary>
/// O2 "Unified Runs and Tasks center" (04-operations-trigger-plan.md §3): a transparent
/// Backend proxy — this service's only job is building the request path/query and forwarding
/// identity, never re-deriving Backend's filters/cursor/visibility logic.
/// </summary>
public sealed class RunDiscoveryServiceTests
{
    private static readonly UserContext Admin = new("op-a", "demo-a", "ADMIN");

    private static RunDiscoveryService Build(StubHttpMessageHandler backend)
        => new(TestBackend.Client(backend));

    [Theory]
    [InlineData("", "http://backend/api/runs")]
    [InlineData("?kind=direct-agent&limit=7", "http://backend/api/runs?kind=direct-agent&limit=7")]
    public async Task ListAsync_ForwardsQueryStringVerbatim_AndIdentityHeaders(
        string queryString, string expectedUrl)
    {
        var backend = new StubHttpMessageHandler(_ => TestHttp.Json(
            HttpStatusCode.OK, """{"items":[],"next_cursor":null,"has_more":false}"""));

        var result = await Build(backend).ListAsync(queryString, Admin);

        Assert.Equal(expectedUrl, backend.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Get, backend.LastRequest.Method);
        Assert.Equal("demo-a", backend.Header("X-Tenant-Id"));
        Assert.Equal("op-a", backend.Header("X-User-Id"));
        Assert.Equal(200, result.Status);
    }

    // Backend owns the visibility/filter decision; this proxy must pass its status/body through
    // verbatim, never rewrite it into a locally-derived error.
    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ListAsync_DownstreamRejection_PassesStatusAndBodyThroughVerbatim(HttpStatusCode status)
    {
        var backend = new StubHttpMessageHandler(_ => TestHttp.Error(status, "downstream message"));

        var result = await Build(backend).ListAsync("?kind=bogus", Admin);

        Assert.Equal((int)status, result.Status);
        Assert.Contains("downstream message", result.Body);
    }
}
