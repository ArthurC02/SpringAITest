using Backend.Api.Files;
using Npgsql;

namespace Backend.Api.Common;

internal sealed record HealthComponent(string Status, bool Required);

internal sealed record HealthReport(
    string Status,
    bool Ready,
    IReadOnlyDictionary<string, HealthComponent> Components);

internal sealed class BackendReadinessProbe
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(1);
    private readonly Func<CancellationToken, Task<bool>> _check;
    private readonly bool _databaseRequired;
    private readonly BackendHealthMetrics _metrics;
    private readonly DocumentConsumerState? _documentConsumer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HealthReport? _cached;
    private DateTime _cachedAt;

    public BackendReadinessProbe(
        bool databaseRequired,
        NpgsqlDataSource? dataSource,
        BackendHealthMetrics? metrics = null,
        DocumentConsumerState? documentConsumer = null)
    {
        _databaseRequired = databaseRequired;
        _metrics = metrics ?? BackendHealthMetrics.Shared;
        _documentConsumer = documentConsumer;
        _check = !databaseRequired
            ? _ => Task.FromResult(true)
            : async ct =>
            {
                if (dataSource is null)
                {
                    return false;
                }
                await using var connection = await dataSource.OpenConnectionAsync(ct);
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT to_regclass('public.tenants') IS NOT NULL
                       AND to_regclass('public.conversations') IS NOT NULL
                    """;
                return await command.ExecuteScalarAsync(ct) is true;
            };
    }

    internal BackendReadinessProbe(
        Func<CancellationToken, Task<bool>> check,
        BackendHealthMetrics? metrics = null,
        DocumentConsumerState? documentConsumer = null)
    {
        _check = check;
        _databaseRequired = true;
        _metrics = metrics ?? BackendHealthMetrics.Shared;
        _documentConsumer = documentConsumer;
    }

    public async Task<HealthReport> CheckAsync(CancellationToken ct)
    {
        if (_cached is not null && DateTime.UtcNow - _cachedAt < CacheDuration)
        {
            return _cached;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_cached is not null && DateTime.UtcNow - _cachedAt < CacheDuration)
            {
                return _cached;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ProbeTimeout);
            bool healthy;
            try
            {
                healthy = await _check(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                healthy = false;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                healthy = false;
            }

            // A non-cooperative check can return after the caller has cancelled. The caller's
            // cancellation still wins over a fresh result: do not publish or cache it.
            ct.ThrowIfCancellationRequested();

            if (_databaseRequired)
            {
                _metrics.RecordReadinessCheck(healthy);
            }

            var components = new Dictionary<string, HealthComponent>
            {
                ["database_migrations"] = new(healthy ? "UP" : "DOWN", Required: true),
            };

            // 只有真的跑著 consumer 的進程才呈報這個元件 —— 「沒有 consumer」不能看起來像
            // 「consumer 壞了」。Required:false 是刻意的:它不參與 Ready 判定,broker 抖動不得把
            // backend 判成 not ready(重啟 backend 對停滯中的文件處理沒有幫助,只多一次不穩定)。
            // 要升級成 gating 就是把下面那個 false 改成 true,是獨立的維運決策。
            if (_documentConsumer is { Active: true } consumer)
            {
                components["document_consumer"] = new(consumer.Connected ? "UP" : "DOWN", Required: false);
            }

            _cached = new HealthReport(
                healthy ? "UP" : "DOWN",
                healthy,
                components);
            _cachedAt = DateTime.UtcNow;
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }
}

internal static class BackendHealthEndpoints
{
    public static IResult Live()
        => Results.Json(new HealthReport(
            "UP",
            Ready: true,
            new Dictionary<string, HealthComponent>
            {
                ["process"] = new("UP", Required: true),
            }));

    public static async Task<IResult> Ready(
        BackendReadinessProbe probe,
        CancellationToken ct)
    {
        var report = await probe.CheckAsync(ct);
        return Results.Json(
            report,
            statusCode: report.Ready
                ? StatusCodes.Status200OK
                : StatusCodes.Status503ServiceUnavailable);
    }
}
