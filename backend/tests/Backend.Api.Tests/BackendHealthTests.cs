using System.Net;
using Backend.Api.Common;

namespace Backend.Api.Tests;

public sealed class BackendHealthTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _factory;

    public BackendHealthTests(TestWebAppFactory factory) => _factory = factory;

    [Theory]
    [InlineData("/health")]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task HealthEndpoints_AreAnonymousAndDoNotExposeConfiguration(string path)
    {
        var response = await _factory.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.ReadJsonAsync();
        Assert.DoesNotContain("url", body.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", body.ToJsonString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RequiredDatabaseFailure_MakesReadinessDown()
    {
        var probe = new BackendReadinessProbe(_ => Task.FromResult(false));

        var report = await probe.CheckAsync(default);

        Assert.False(report.Ready);
        Assert.Equal("DOWN", report.Status);
        Assert.True(report.Components["database_migrations"].Required);
    }

    [Fact]
    public async Task ConcurrentChecks_AreSingleFlight()
    {
        var calls = 0;
        var probe = new BackendReadinessProbe(async ct =>
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(20, ct);
            return true;
        });

        var reports = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => probe.CheckAsync(default)));

        Assert.Equal(1, calls);
        Assert.All(reports, report => Assert.True(report.Ready));
    }

    [Fact]
    public async Task CallerCancellation_IsNotConvertedToDown()
    {
        var probe = new BackendReadinessProbe(async ct =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return true;
        });
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => probe.CheckAsync(cancelled.Token));
    }
}
