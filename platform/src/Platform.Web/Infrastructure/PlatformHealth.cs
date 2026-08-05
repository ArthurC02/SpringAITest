using System.Net.Http.Json;
using Platform.Service.Options;
using RabbitMQ.Client;

namespace Platform.Web.Infrastructure;

internal sealed record HealthComponent(string Status, bool Required);

internal sealed record HealthReport(
    string Status,
    bool Ready,
    IReadOnlyDictionary<string, HealthComponent> Components);

internal sealed class PlatformReadinessProbe
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(1);
    private readonly IReadOnlyList<(string Name, bool Required, Func<CancellationToken, Task<bool>> Check)> _checks;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HealthReport? _cached;
    private DateTime _cachedAt;

    public PlatformReadinessProbe(
        IHttpClientFactory clients,
        BackendOptions backend,
        WorkflowOptions workflow,
        RabbitMqOptions rabbit,
        LlmOptions llm,
        Mem0Options mem0,
        bool mem0InMemory,
        string? telemetryEndpoint,
        bool localTelemetry,
        bool testing)
    {
        Task<bool> HttpReady(string baseUrl, string path, CancellationToken ct)
            => CheckHttpAsync(clients.CreateClient("health"), baseUrl, path, ct);
        Task<bool> HttpStatus(string baseUrl, string path, CancellationToken ct)
            => CheckHttpStatusAsync(
                clients.CreateClient("health"),
                new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), path.TrimStart('/')),
                ct);
        _checks =
        [
            ("backend", true, testing
                ? _ => Task.FromResult(true)
                : ct => HttpReady(backend.BaseUrl, "/health/ready", ct)),
            ("workflow", true, testing
                ? _ => Task.FromResult(true)
                : ct => HttpReady(workflow.BaseUrl, "/health/ready", ct)),
            ("rabbitmq", true, testing
                ? _ => Task.FromResult(true)
                : ct => CheckRabbitAsync(rabbit.Url, ct)),
            ("litellm", false, testing
                ? _ => Task.FromResult(true)
                : ct => HttpStatus(llm.BaseUrl, "/health/liveliness", ct)),
            ("mem0", false, testing || mem0InMemory
                ? _ => Task.FromResult(true)
                : ct => HttpStatus(mem0.BaseUrl, "/health", ct)),
            ("telemetry", false, testing || localTelemetry || string.IsNullOrWhiteSpace(telemetryEndpoint)
                ? _ => Task.FromResult(true)
                : ct => CheckTelemetryAsync(clients.CreateClient("health"), telemetryEndpoint!, ct)),
        ];
    }

    internal PlatformReadinessProbe(
        IReadOnlyList<(string Name, bool Required, Func<CancellationToken, Task<bool>> Check)> checks)
        => _checks = checks;

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
            var results = await Task.WhenAll(_checks.Select(check => BoundedAsync(check.Check, ct)));
            var components = _checks
                .Select((check, index) => new KeyValuePair<string, HealthComponent>(
                    check.Name,
                    new(
                        results[index] ? "UP" : check.Required ? "DOWN" : "DEGRADED",
                        check.Required)))
                .ToDictionary();
            var ready = _checks.Select((check, index) => results[index] || !check.Required).All(x => x);
            var degraded = results.Any(result => !result);
            _cached = new HealthReport(
                !ready ? "DOWN" : degraded ? "DEGRADED" : "UP",
                ready,
                components);
            _cachedAt = DateTime.UtcNow;
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<bool> BoundedAsync(
        Func<CancellationToken, Task<bool>> check,
        CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            return await check(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    internal static async Task<bool> CheckHttpAsync(
        HttpClient client,
        string baseUrl,
        string path,
        CancellationToken ct)
    {
        using var response = await client.GetAsync(
            new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), path.TrimStart('/')),
            ct);
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }
        var payload = await response.Content.ReadFromJsonAsync<HealthReport>(ct);
        return payload?.Ready is true;
    }

    private static async Task<bool> CheckRabbitAsync(string url, CancellationToken ct)
    {
        var factory = new ConnectionFactory { Uri = new Uri(url) };
        await using var connection = await factory.CreateConnectionAsync(ct);
        return connection.IsOpen;
    }

    private static Task<bool> CheckTelemetryAsync(
        HttpClient client,
        string endpoint,
        CancellationToken ct)
    {
        var origin = new Uri(new Uri(endpoint), "/api/public/health");
        return CheckHttpStatusAsync(client, origin, ct);
    }

    internal static async Task<bool> CheckHttpStatusAsync(
        HttpClient client,
        Uri uri,
        CancellationToken ct)
    {
        using var response = await client.GetAsync(uri, ct);
        return response.IsSuccessStatusCode;
    }
}

internal static class PlatformHealthEndpoints
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
        PlatformReadinessProbe probe,
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
