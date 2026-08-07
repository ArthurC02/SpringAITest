using System.Diagnostics.Metrics;
using System.Text.Json;

namespace Backend.Api.Common;

public sealed class ArtifactCompatibilityUsageMetrics : IDisposable
{
    public const string MeterName = "Backend.Api.ArtifactCompatibilityUsage";
    public const string CounterName = "artifact_compatibility_usage_total";

    public static ArtifactCompatibilityUsageMetrics Shared { get; } = new(MeterName);
    public static readonly object CountedItemKey = new();

    private readonly Meter _meter;
    private readonly Counter<long> _usage;

    public ArtifactCompatibilityUsageMetrics(string meterName)
    {
        _meter = new Meter(meterName);
        _usage = _meter.CreateCounter<long>(CounterName);
    }

    public async Task<T> TrackAsync<T>(HttpContext context, string surface, string operation, Func<ArtifactUsage, Task<T>> action)
    {
        var usage = new ArtifactUsage();
        try
        {
            var result = await action(usage);
            Record(surface, operation, usage.ResolvedArtifactTypes, "success");
            return result;
        }
        catch (ApiException ex)
        {
            Record(surface, operation, usage.ResolvedArtifactTypes, ex.Status switch
            {
                StatusCodes.Status404NotFound => "not_found",
                >= 400 and < 500 => "rejected",
                _ => "error",
            });
            throw;
        }
        catch
        {
            Record(surface, operation, usage.ResolvedArtifactTypes, "error");
            throw;
        }
        finally
        {
            context.Items[CountedItemKey] = true;
        }
    }

    public void RecordRequestFailure(string surface, string operation, int statusCode)
        => Record(surface, operation, ["unknown"], statusCode switch
        {
            StatusCodes.Status404NotFound => "not_found",
            >= 400 and < 500 => "rejected",
            _ => "error",
        });

    private void Record(string surface, string operation, IEnumerable<string> resolvedArtifactTypes, string outcome)
    {
        foreach (var resolvedArtifactType in resolvedArtifactTypes)
        {
            Record(surface, operation, resolvedArtifactType, outcome);
        }
    }

    private void Record(string surface, string operation, string resolvedArtifactType, string outcome)
    {
        try
        {
            _usage.Add(1,
                new("service", "backend"),
                new("surface", surface),
                new("operation", operation),
                new("resolved_artifact_type", resolvedArtifactType),
                new("outcome", outcome));
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                @event = CounterName,
                timestampUtc = DateTime.UtcNow.ToString("O"),
                deploymentVersion = Environment.GetEnvironmentVariable("DEPLOYMENT_VERSION") ?? "unknown",
                service = "backend",
                surface,
                operation,
                resolvedArtifactType,
                outcome,
                count = 1,
            }));
        }
        catch
        {
            // Instrument listeners are optional observability; they must not affect API behavior.
        }
    }

    public void Dispose() => _meter.Dispose();
}

public sealed class ArtifactUsage
{
    public IReadOnlyList<string> ResolvedArtifactTypes { get; private set; } = ["unknown"];

    public void Resolve(string? kind)
        => ResolvedArtifactTypes = [ResolveKind(kind)];

    private static string ResolveKind(string? kind)
        => kind switch
        {
            "agentic" => "agent_skill",
            "flow" => "business_workflow",
            _ => "unknown",
        };

    public void Resolve(IEnumerable<string> kinds)
    {
        ResolvedArtifactTypes = kinds.Select(ResolveKind).Distinct(StringComparer.Ordinal).Take(3).ToArray();
        if (ResolvedArtifactTypes.Count == 0)
        {
            ResolvedArtifactTypes = ["unknown"];
        }
    }
}
