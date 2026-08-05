using System.Net;
using System.Text;
using Platform.Web.Infrastructure;

namespace Platform.Web.Tests;

public sealed class HealthApiTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _factory;

    public HealthApiTests(TestWebAppFactory factory) => _factory = factory;

    [Theory]
    [InlineData("/actuator/health")]
    [InlineData("/actuator/health/live")]
    [InlineData("/actuator/health/ready")]
    public async Task Health_Anonymous_Returns200(string path)
    {
        var client = _factory.CreateClient();

        var resp = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("http://", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RequiredFailure_MakesReadinessDown()
    {
        var probe = Probe(
            ("backend", true, _ => Task.FromResult(false)),
            ("telemetry", false, _ => Task.FromResult(true)));

        var report = await probe.CheckAsync(default);

        Assert.False(report.Ready);
        Assert.Equal("DOWN", report.Status);
        Assert.Equal("DOWN", report.Components["backend"].Status);
    }

    [Fact]
    public async Task OptionalFailure_IsDegradedButReady()
    {
        var probe = Probe(
            ("backend", true, _ => Task.FromResult(true)),
            ("mem0", false, _ => Task.FromResult(false)));

        var report = await probe.CheckAsync(default);

        Assert.True(report.Ready);
        Assert.Equal("DEGRADED", report.Status);
        Assert.Equal("DEGRADED", report.Components["mem0"].Status);
    }

    [Fact]
    public async Task ConcurrentReadinessChecks_AreSingleFlight()
    {
        var calls = 0;
        var probe = Probe(("backend", true, async ct =>
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(20, ct);
            return true;
        }));

        var reports = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => probe.CheckAsync(default)));

        Assert.Equal(1, calls);
        Assert.All(reports, report => Assert.True(report.Ready));
    }

    [Fact]
    public async Task CallerCancellation_IsNotConvertedToDependencyFailure()
    {
        var probe = Probe(("backend", true, async ct =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return true;
        }));
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => probe.CheckAsync(cancelled.Token));
    }

    [Fact]
    public async Task HttpProbes_DisposeResponses()
    {
        var handler = new TrackingResponseHandler();
        using var client = new HttpClient(handler);

        var ready = await PlatformReadinessProbe.CheckHttpAsync(
            client,
            "http://backend.test",
            "/health/ready",
            default);
        var status = await PlatformReadinessProbe.CheckHttpStatusAsync(
            client,
            new Uri("http://litellm.test/health/liveliness"),
            default);

        Assert.True(ready);
        Assert.True(status);
        Assert.Equal(2, handler.CreatedResponses);
        Assert.Equal(2, handler.DisposedResponses);
        Assert.Equal(0, handler.ActiveResponses);
    }

    [Fact]
    public async Task RepeatedExpiredCacheProbes_DoNotRetainResponses()
    {
        var handler = new TrackingResponseHandler();
        using var client = new HttpClient(handler);
        var probe = Probe(
            ("backend", true, ct => PlatformReadinessProbe.CheckHttpAsync(
                client,
                "http://backend.test",
                "/health/ready",
                ct)),
            ("litellm", false, ct => PlatformReadinessProbe.CheckHttpStatusAsync(
                client,
                new Uri("http://litellm.test/health/liveliness"),
                ct)));

        for (var round = 1; round <= 3; round++)
        {
            var report = await probe.CheckAsync(default);

            Assert.True(report.Ready);
            Assert.Equal(round * 2, handler.CreatedResponses);
            Assert.Equal(handler.CreatedResponses, handler.DisposedResponses);
            Assert.Equal(0, handler.ActiveResponses);

            if (round < 3)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(2100));
            }
        }
    }

    private static PlatformReadinessProbe Probe(
        params (string Name, bool Required, Func<CancellationToken, Task<bool>> Check)[] checks)
        => new(checks);

    private sealed class TrackingResponseHandler : HttpMessageHandler
    {
        private int _activeResponses;
        private int _createdResponses;
        private int _disposedResponses;

        public int ActiveResponses => Volatile.Read(ref _activeResponses);
        public int CreatedResponses => Volatile.Read(ref _createdResponses);
        public int DisposedResponses => Volatile.Read(ref _disposedResponses);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _createdResponses);
            Interlocked.Increment(ref _activeResponses);
            var content = request.RequestUri?.AbsolutePath == "/health/ready"
                ? "{\"status\":\"UP\",\"ready\":true,\"components\":{}}"
                : "{}";
            HttpResponseMessage response = new TrackingHttpResponseMessage(
                OnResponseDisposed,
                HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }

        private void OnResponseDisposed()
        {
            Interlocked.Increment(ref _disposedResponses);
            Interlocked.Decrement(ref _activeResponses);
        }
    }

    private sealed class TrackingHttpResponseMessage(
        Action onDispose,
        HttpStatusCode statusCode) : HttpResponseMessage(statusCode)
    {
        private int _disposed;

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                onDispose();
            }
        }
    }
}
