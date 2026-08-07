using System.Diagnostics.Metrics;
using System.Net.Http.Json;
using Backend.Api.Common;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Backend.Api.Tests;

public sealed class ArtifactCompatibilityUsageMetricsTests
{
    [Theory]
    [InlineData(404, "not_found")]
    [InlineData(409, "rejected")]
    [InlineData(502, "error")]
    public async Task FinalApiOutcome_RecordsOneBoundedMeasurement(int status, string expectedOutcome)
    {
        using var capture = new MetricCapture();
        var exception = new ApiException(status, "failure");

        await Assert.ThrowsAsync<ApiException>(() => capture.Metrics.TrackAsync<object>(
            new DefaultHttpContext(), "public_skills", "update", usage =>
            {
                usage.Resolve("flow");
                throw exception;
            }));

        var tags = Assert.Single(capture.Measurements);
        Assert.Equal("backend", tags["service"]);
        Assert.Equal("public_skills", tags["surface"]);
        Assert.Equal("update", tags["operation"]);
        Assert.Equal("business_workflow", tags["resolved_artifact_type"]);
        Assert.Equal(expectedOutcome, tags["outcome"]);
    }

    [Fact]
    public async Task Success_IsRecordedOnlyAfterOperationCompletes()
    {
        using var capture = new MetricCapture();
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var operation = capture.Metrics.TrackAsync(new DefaultHttpContext(), "public_skills", "create", async usage =>
        {
            usage.Resolve("agentic");
            await committed.Task;
            return true;
        });

        Assert.Empty(capture.Measurements);
        committed.SetResult();
        Assert.True(await operation);
        Assert.Equal("success", Assert.Single(capture.Measurements)["outcome"]);
    }

    [Fact]
    public async Task UnexpectedFailure_UsesUnknownTypeAndErrorOutcome()
    {
        using var capture = new MetricCapture();

        await Assert.ThrowsAsync<InvalidOperationException>(() => capture.Metrics.TrackAsync<object>(
            new DefaultHttpContext(), "public_business_workflows", "delete", _ => throw new InvalidOperationException()));

        var tags = Assert.Single(capture.Measurements);
        Assert.Equal("unknown", tags["resolved_artifact_type"]);
        Assert.Equal("error", tags["outcome"]);
    }

    [Fact]
    public async Task MixedList_RecordsOneBoundedRowPerRepresentedType()
    {
        using var capture = new MetricCapture();

        await capture.Metrics.TrackAsync(new DefaultHttpContext(), "public_skills", "list", usage =>
        {
            usage.Resolve(["flow", "agentic", "flow"]);
            return Task.FromResult(true);
        });

        Assert.Equal(2, capture.Measurements.Count);
        Assert.Equal(
            ["agent_skill", "business_workflow"],
            capture.Measurements.Select(tags => tags["resolved_artifact_type"]).Order().ToArray());
    }

    [Theory]
    [InlineData("/api/skills", "POST", 403, "public_skills", "create", "rejected")]
    [InlineData("/api/skills/example", "PUT", 400, "public_skills", "update", "rejected")]
    [InlineData("/api/business-workflows/example", "DELETE", 401, "public_business_workflows", "delete", "rejected")]
    public async Task RequestBoundary_RecordsPreActionFailures(
        string path, string method, int status, string surface, string operation, string outcome)
    {
        using var capture = new MetricCapture();
        var middleware = new ArtifactCompatibilityUsageMiddleware(context =>
        {
            context.Response.StatusCode = status;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Method = method;

        await middleware.InvokeAsync(context, capture.Metrics);
        await context.Response.CompleteAsync();

        var tags = Assert.Single(capture.Measurements);
        Assert.Equal(surface, tags["surface"]);
        Assert.Equal(operation, tags["operation"]);
        Assert.Equal("unknown", tags["resolved_artifact_type"]);
        Assert.Equal(outcome, tags["outcome"]);
    }

    [Fact]
    public async Task RequestBoundary_DoesNotDoubleCountActionInstrumentation()
    {
        using var capture = new MetricCapture();
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/skills/example";
        context.Request.Method = "GET";
        async Task Next(HttpContext requestContext)
        {
            requestContext.Response.StatusCode = 404;
            await Assert.ThrowsAsync<ApiException>(() => capture.Metrics.TrackAsync<object>(
                requestContext, "public_skills", "read", usage =>
                {
                    usage.Resolve("flow");
                    throw new ApiException(404, "missing");
                }));
        }
        var middleware = new ArtifactCompatibilityUsageMiddleware(Next);

        await middleware.InvokeAsync(context, capture.Metrics);
        await context.Response.CompleteAsync();

        var tags = Assert.Single(capture.Measurements);
        Assert.Equal("business_workflow", tags["resolved_artifact_type"]);
        Assert.Equal("not_found", tags["outcome"]);
    }

    [Theory]
    [InlineData("admin", 403)]
    [InlineData("model", 400)]
    [InlineData("internal-token", 401)]
    public async Task RealPipeline_PreActionFailure_IsObservable(string failure, int expectedStatus)
    {
        using var factory = new BoundaryFactory();
        using var client = factory.CreateClient();
        if (failure != "internal-token")
        {
            client.DefaultRequestHeaders.Add(InternalTokenMiddleware.HeaderName, TestWebAppFactory.InternalToken);
            client.DefaultRequestHeaders.Add(IdentityHeaders.TenantHeader, "demo-a");
            client.DefaultRequestHeaders.Add(IdentityHeaders.RoleHeader, failure == "admin" ? "USER" : "ADMIN");
        }

        using var response = await client.PostAsJsonAsync("/api/skills", new { });

        Assert.Equal(expectedStatus, (int)response.StatusCode);
        var tags = Assert.Single(factory.Capture.Measurements);
        Assert.Equal("public_skills", tags["surface"]);
        Assert.Equal("create", tags["operation"]);
        Assert.Equal("unknown", tags["resolved_artifact_type"]);
        Assert.Equal("rejected", tags["outcome"]);
    }

    private sealed class BoundaryFactory : TestWebAppFactory
    {
        public MetricCapture Capture { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ArtifactCompatibilityUsageMetrics>();
                services.AddSingleton(Capture.Metrics);
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                Capture.Dispose();
            }
        }
    }

    private sealed class MetricCapture : IDisposable
    {
        private readonly MeterListener _listener = new();

        public MetricCapture()
        {
            var meterName = ArtifactCompatibilityUsageMetrics.MeterName + ".tests." + Guid.NewGuid().ToString("N");
            Metrics = new ArtifactCompatibilityUsageMetrics(meterName);
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == meterName
                    && instrument.Name == ArtifactCompatibilityUsageMetrics.CounterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                Assert.Equal(1, value);
                Assert.Equal(5, tags.Length);
                var measurement = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var tag in tags)
                {
                    measurement.Add(tag.Key, Assert.IsType<string>(tag.Value));
                }
                Measurements.Add(measurement);
            });
            _listener.Start();
        }

        public ArtifactCompatibilityUsageMetrics Metrics { get; }
        public List<Dictionary<string, string>> Measurements { get; } = [];

        public void Dispose()
        {
            _listener.Dispose();
            Metrics.Dispose();
        }
    }
}
