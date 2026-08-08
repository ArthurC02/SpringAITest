using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using Backend.Api.Common;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
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

    // 觀測絕不能改變 API 回應:錯誤路徑上白名單 fail-fast 會**取代**呼叫端正在往上丟的例外,
    // 把 409 變成 500。此時只能吞掉證據並記錄,原例外照常傳播(成功路徑仍 fail-fast)。
    [Fact]
    public async Task ErrorPath_UnboundedDimension_DoesNotReplaceOriginalApiException()
    {
        using var capture = new MetricCapture();

        var error = await Assert.ThrowsAsync<ApiException>(() => capture.Metrics.TrackAsync<object>(
            new DefaultHttpContext(), "public_business_workflows", "revision_read",
            _ => throw new ApiException(409, "stale")));

        Assert.Equal(409, error.Status);
        Assert.Empty(capture.Measurements);
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
        MarkAsMatchedControllerAction(context);

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
        MarkAsMatchedControllerAction(context);
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

    // middleware 刻意註冊在 InternalTokenMiddleware 之前。/api/business-workflows 沒有
    // revisions 路由；POST 卻會被字串分類成合法的 create。未匹配 controller action 的探測
    // 不得污染零使用量證據。
    [Fact]
    public async Task RequestBoundary_PathOutsideSurfaceAuthority_EmitsNoUsageEvent()
    {
        using var capture = new MetricCapture();
        var middleware = new ArtifactCompatibilityUsageMiddleware(context =>
        {
            context.Response.StatusCode = 404;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/business-workflows/example/revisions";
        context.Request.Method = "POST";

        await middleware.InvokeAsync(context, capture.Metrics);
        await context.Response.CompleteAsync();

        Assert.Empty(capture.Measurements);
    }

    [Fact]
    public async Task RealPipeline_NonexistentPathThatLooksLikeCreate_EmitsNoUsageEvent()
    {
        using var factory = new BoundaryFactory();
        using var response = await factory.CreateClient().PostAsJsonAsync(
            "/api/business-workflows/example/revisions", new { });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(factory.Capture.Measurements);
    }

    private static void MarkAsMatchedControllerAction(HttpContext context)
        => context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(new ControllerActionDescriptor()),
            "test controller action"));

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

    // 「只用有界維度」是 P3-R2 的硬要求(根 AGENTS.md 不變量),語意鏡像 workflow/app/usage_evidence.py
    // 的 _ALLOWED fail-fast:未知值寧可炸掉也不能靜默寫出高基數標籤把整份證據作廢。
    // 合法值的放行側由本檔其餘測試(create/update/list/read/delete × 兩個 surface)覆蓋。
    [Theory]
    [InlineData("legacy_skills", "list")]           // surface 不在 AUTHORITY["backend"]
    [InlineData("public_skills", "validate")]       // validate 是 platform/workflow 的權威,不是 backend 的
    [InlineData("public_skills", "invoke")]
    public void UnboundedDimension_FailsFastInsteadOfEmittingHighCardinalityLabel(
        string surface, string operation)
    {
        using var capture = new MetricCapture();

        Assert.Throws<ArgumentException>(
            () => capture.Metrics.RecordRequestFailure(surface, operation, 400));
        Assert.Empty(capture.Measurements);
    }

    // surface×operation 是**配對**白名單,不是兩個獨立集合:匯出器檢查的是
    // AUTHORITY["backend"][surface] 是否含這個 operation。revision_read 對 public_skills 合法,
    // 對 public_business_workflows 不合法(該 surface 沒有 revisions 路由)—— 平坦集合會放行後者。
    [Theory]
    [InlineData("public_skills", "revision_read", true)]
    [InlineData("public_business_workflows", "export", true)]
    [InlineData("public_business_workflows", "revision_read", false)]
    public void SurfaceOperationPair_MirrorsExportAuthority(string surface, string operation, bool authoritative)
    {
        using var capture = new MetricCapture();

        if (authoritative)
        {
            capture.Metrics.RecordRequestFailure(surface, operation, 404);
            Assert.Equal(operation, Assert.Single(capture.Measurements)["operation"]);
        }
        else
        {
            Assert.Throws<ArgumentException>(
                () => capture.Metrics.RecordRequestFailure(surface, operation, 404));
            Assert.Empty(capture.Measurements);
        }
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
