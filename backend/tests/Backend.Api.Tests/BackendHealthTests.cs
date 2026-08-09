using System.Net;
using System.Diagnostics.Metrics;
using Backend.Api.Common;
using Backend.Api.Files;
using Microsoft.Extensions.DependencyInjection;

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

    [Fact]
    public async Task ReadinessMetric_RecordsFreshUpAndDownOnly_WithExactStatusTag()
    {
        var meterName = BackendHealthMetrics.MeterName + ".tests." + Guid.NewGuid().ToString("N");
        using var metrics = new BackendHealthMetrics(meterName);
        using var listener = new MeterListener();
        var measurements = new List<(long Value, string Key, string? ValueTag)>();
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Meter.Name == meterName
                && instrument.Name == BackendHealthMetrics.ReadinessCheckCounterName)
            {
                current.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            Assert.Equal(1, tags.Length);
            measurements.Add((value, tags[0].Key, tags[0].Value?.ToString()));
        });
        listener.Start();

        var upCalls = 0;
        var upProbe = new BackendReadinessProbe(_ =>
        {
            Interlocked.Increment(ref upCalls);
            return Task.FromResult(true);
        }, metrics);
        Assert.True((await upProbe.CheckAsync(default)).Ready);
        Assert.True((await upProbe.CheckAsync(default)).Ready); // Cache hit does not emit another result.

        var downProbe = new BackendReadinessProbe(databaseRequired: true, dataSource: null, metrics: metrics);
        Assert.False((await downProbe.CheckAsync(default)).Ready);

        var noDatabaseProbe = new BackendReadinessProbe(databaseRequired: false, dataSource: null, metrics: metrics);
        Assert.True((await noDatabaseProbe.CheckAsync(default)).Ready);

        Assert.Equal(1, upCalls);
        Assert.Equal(2, measurements.Count);
        Assert.All(measurements, measurement =>
        {
            Assert.Equal(1, measurement.Value);
            Assert.Equal("status", measurement.Key);
            Assert.Contains(measurement.ValueTag, new[] { "up", "down" });
        });
        Assert.Single(measurements, measurement => measurement.ValueTag == "up");
        Assert.Single(measurements, measurement => measurement.ValueTag == "down");
    }

    [Fact]
    public async Task ReadinessMetric_CancelledOwnerAndWaitingCaller_DoNotEmitExtraMeasurements()
    {
        var meterName = BackendHealthMetrics.MeterName + ".cancellation.tests." + Guid.NewGuid().ToString("N");
        using var metrics = new BackendHealthMetrics(meterName);
        using var listener = new MeterListener();
        var measurements = 0;
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Meter.Name == meterName
                && instrument.Name == BackendHealthMetrics.ReadinessCheckCounterName)
            {
                current.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) => Interlocked.Increment(ref measurements));
        listener.Start();

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var probe = new BackendReadinessProbe(async ct =>
        {
            Interlocked.Increment(ref calls);
            entered.SetResult();
            await release.Task.WaitAsync(ct);
            return true;
        }, metrics);

        var owner = probe.CheckAsync(default);
        await entered.Task;
        using var waitingCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.CheckAsync(waitingCancellation.Token));
        release.SetResult();
        Assert.True((await owner).Ready);

        var cancelledOwnerMeasurements = Volatile.Read(ref measurements);
        var cancelledOwner = new BackendReadinessProbe(
            async ct =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return true;
            },
            metrics);
        using var ownerCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledOwner.CheckAsync(ownerCancellation.Token));

        Assert.Equal(1, calls);
        Assert.Equal(1, cancelledOwnerMeasurements);
        Assert.Equal(1, Volatile.Read(ref measurements));
    }

    [Fact]
    public async Task ReadinessMetric_NonCooperativeCheckAfterCallerCancellation_DoesNotEmitOrCache()
    {
        var meterName = BackendHealthMetrics.MeterName + ".non-cooperative-cancellation.tests." + Guid.NewGuid().ToString("N");
        using var metrics = new BackendHealthMetrics(meterName);
        using var listener = new MeterListener();
        var measurements = 0;
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Meter.Name == meterName
                && instrument.Name == BackendHealthMetrics.ReadinessCheckCounterName)
            {
                current.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) => Interlocked.Increment(ref measurements));
        listener.Start();

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var probe = new BackendReadinessProbe(async _ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.SetResult();
                await release.Task;
            }
            return true;
        }, metrics);
        using var cancellation = new CancellationTokenSource();

        var cancelledOwner = probe.CheckAsync(cancellation.Token);
        await entered.Task;
        cancellation.Cancel();
        release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledOwner);

        Assert.Equal(0, Volatile.Read(ref measurements));
        Assert.True((await probe.CheckAsync(default)).Ready);
        Assert.Equal(2, calls);
        Assert.Equal(1, Volatile.Read(ref measurements));
    }

    [Fact]
    public async Task ReadinessMetric_BoundedTimeout_RecordsOneDown()
    {
        var meterName = BackendHealthMetrics.MeterName + ".timeout.tests." + Guid.NewGuid().ToString("N");
        using var metrics = new BackendHealthMetrics(meterName);
        using var listener = new MeterListener();
        var measurements = new List<(string Key, string? Value)>();
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Meter.Name == meterName
                && instrument.Name == BackendHealthMetrics.ReadinessCheckCounterName)
            {
                current.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            Assert.Equal(1, value);
            Assert.Equal(1, tags.Length);
            measurements.Add((tags[0].Key, tags[0].Value?.ToString()));
        });
        listener.Start();

        var probe = new BackendReadinessProbe(async ct =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return true;
        }, metrics);

        Assert.False((await probe.CheckAsync(default)).Ready);
        var measurement = Assert.Single(measurements);
        Assert.Equal("status", measurement.Key);
        Assert.Equal("down", measurement.Value);
    }

    [Fact]
    public async Task ReadinessMetric_InternalException_RecordsOneDown()
    {
        var meterName = BackendHealthMetrics.MeterName + ".exception.tests." + Guid.NewGuid().ToString("N");
        using var metrics = new BackendHealthMetrics(meterName);
        using var listener = new MeterListener();
        var measurements = new List<(string Key, string? Value)>();
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Meter.Name == meterName
                && instrument.Name == BackendHealthMetrics.ReadinessCheckCounterName)
            {
                current.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            Assert.Equal(1, value);
            Assert.Equal(1, tags.Length);
            measurements.Add((tags[0].Key, tags[0].Value?.ToString()));
        });
        listener.Start();

        var probe = new BackendReadinessProbe(_ => throw new InvalidOperationException("probe failure"), metrics);

        Assert.False((await probe.CheckAsync(default)).Ready);
        var measurement = Assert.Single(measurements);
        Assert.Equal("status", measurement.Key);
        Assert.Equal("down", measurement.Value);
    }

    [Fact]
    public async Task ReadinessMetric_ListenerFailure_DoesNotChangeReadinessResult()
    {
        var meterName = BackendHealthMetrics.MeterName + ".listener-failure.tests." + Guid.NewGuid().ToString("N");
        using var metrics = new BackendHealthMetrics(meterName);
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Meter.Name == meterName
                && instrument.Name == BackendHealthMetrics.ReadinessCheckCounterName)
            {
                current.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) => throw new InvalidOperationException("listener failure"));
        listener.Start();

        var probe = new BackendReadinessProbe(_ => Task.FromResult(true), metrics);

        Assert.True((await probe.CheckAsync(default)).Ready);
    }

    /// <summary>
    /// 沒有 consumer 的部署(Testing、lite)不得出現這個元件 —— 「沒有 consumer」不可以看起來像
    /// 「consumer 壞了」,那會讓監控對著一個永遠 DOWN 的元件叫。
    /// </summary>
    [Fact]
    public async Task DocumentConsumer_NotStartedInThisProcess_IsAbsentFromComponents()
    {
        var probe = new BackendReadinessProbe(
            _ => Task.FromResult(true), documentConsumer: new DocumentConsumerState());

        var report = await probe.CheckAsync(default);

        Assert.False(report.Components.ContainsKey("document_consumer"));
        Assert.True(report.Ready);
    }

    [Fact]
    public async Task DocumentConsumer_Connected_IsUp_AndReadyStillComesFromTheDatabaseCheck()
    {
        var probe = new BackendReadinessProbe(
            _ => Task.FromResult(true),
            documentConsumer: new DocumentConsumerState { Active = true, Connected = true });

        var report = await probe.CheckAsync(default);

        Assert.Equal(new HealthComponent("UP", Required: false), report.Components["document_consumer"]);
        Assert.True(report.Ready);
    }

    /// <summary>
    /// 本項最重要的取捨,刻意釘死:broker 斷線時元件是 DOWN,但 <c>Ready</c> 仍為 true、HTTP 仍 200。
    /// 若它變成 gating,編排器會因為 broker 抖動重啟 backend —— 對停滯的文件處理毫無幫助,只多一次不穩定。
    /// </summary>
    [Fact]
    public async Task DocumentConsumer_Disconnected_IsDown_ButDoesNotMakeTheServiceNotReady()
    {
        var probe = new BackendReadinessProbe(
            _ => Task.FromResult(true),
            documentConsumer: new DocumentConsumerState { Active = true, Connected = false });

        var report = await probe.CheckAsync(default);

        Assert.Equal(new HealthComponent("DOWN", Required: false), report.Components["document_consumer"]);
        Assert.True(report.Ready);
        Assert.Equal("UP", report.Status);
    }

    /// <summary>
    /// 端點層:元件真的出現在 <c>GET /health/ready</c> 的 JSON 裡,且斷線時仍是 200
    /// (整條 readiness 判定不受影響)。用獨立 factory 避免與其他測試共用探針的 2 秒快取。
    /// </summary>
    [Fact]
    public async Task ReadyEndpoint_ExposesDocumentConsumerComponent_AndStays200WhenItIsDown()
    {
        using var factory = new TestWebAppFactory();
        var client = factory.CreateClient();
        var state = factory.Services.GetRequiredService<DocumentConsumerState>();
        state.Active = true;
        state.Connected = false;

        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.ReadJsonAsync();
        Assert.True(body["ready"]!.GetValue<bool>());
        var component = body["components"]!["document_consumer"]!;
        Assert.Equal("DOWN", component["status"]!.GetValue<string>());
        Assert.False(component["required"]!.GetValue<bool>());
    }
}
