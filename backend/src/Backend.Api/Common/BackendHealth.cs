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
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HealthReport? _cached;
    private DateTime _cachedAt;

    public BackendReadinessProbe(bool databaseRequired, NpgsqlDataSource? dataSource)
    {
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

    internal BackendReadinessProbe(Func<CancellationToken, Task<bool>> check)
        => _check = check;

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

            _cached = new HealthReport(
                healthy ? "UP" : "DOWN",
                healthy,
                new Dictionary<string, HealthComponent>
                {
                    ["database_migrations"] = new(healthy ? "UP" : "DOWN", Required: true),
                });
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
