using System.Diagnostics.Metrics;

namespace Backend.Api.Common;

internal sealed class BackendHealthMetrics : IDisposable
{
    public const string MeterName = "Backend.Api.Common.Health";
    public const string ReadinessCheckCounterName = "backend.health.readiness.checks";

    public static BackendHealthMetrics Shared { get; } = new(MeterName);

    private readonly Meter _meter;
    private readonly Counter<long> _readinessChecks;

    public BackendHealthMetrics(string meterName)
    {
        _meter = new Meter(meterName);
        _readinessChecks = _meter.CreateCounter<long>(
            ReadinessCheckCounterName,
            description: "Fresh backend readiness probe results");
    }

    public void RecordReadinessCheck(bool healthy)
    {
        try
        {
            _readinessChecks.Add(
                1,
                new KeyValuePair<string, object?>("status", healthy ? "up" : "down"));
        }
        catch
        {
            // Instrument listeners are optional observability; they must not affect readiness.
        }
    }

    public void Dispose() => _meter.Dispose();
}
