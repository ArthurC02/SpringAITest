using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Platform.Web.Tests;

public sealed class ApiRouteSnapshotTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _factory;

    public ApiRouteSnapshotTests(TestWebAppFactory factory) => _factory = factory;

    [Fact]
    public void RegisteredPublicApiRoutes_MatchReviewedSnapshot()
    {
        using var client = _factory.CreateClient();
        var actual = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .SelectMany(endpoint =>
            {
                var path = "/" + (endpoint.RoutePattern.RawText ?? "").TrimStart('/');
                var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods
                    ?? ["ANY"];
                return methods.Select(method => $"{method.ToUpperInvariant()} {path}");
            })
            .Where(route => route[(route.IndexOf(' ') + 1)..].StartsWith("/api/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        var snapshotPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "RouteSnapshots", "platform-public-api.txt"));
        if (Environment.GetEnvironmentVariable("UPDATE_ROUTE_SNAPSHOTS") == "1")
        {
            File.WriteAllLines(snapshotPath, actual);
            return;
        }
        var expected = File.ReadAllLines(snapshotPath);
        Assert.True(expected.SequenceEqual(actual, StringComparer.Ordinal),
            $"Platform public API route snapshot drifted. Review the contract and update {snapshotPath} intentionally.\n"
            + string.Join('\n', actual));
    }
}
