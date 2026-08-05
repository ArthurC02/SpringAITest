using System.Diagnostics.Metrics;

namespace Backend.Api.Files;

/// <summary>
/// Low-cardinality, content-free retirement signal for the pre-allocation producer path.
/// It deliberately has no tags: tenant, user, key, title, text, and document id must never enter it.
/// </summary>
public sealed class DocumentIngestMetrics : IDisposable
{
    public const string MeterName = "Backend.Api.Files.DocumentIngest";
    public const string LegacyCreateCounterName = "backend.document_ingest.legacy_create";

    public static DocumentIngestMetrics Shared { get; } = new(MeterName);

    private readonly Meter _meter;
    private readonly Counter<long> _legacyCreate;

    public DocumentIngestMetrics(string meterName)
    {
        _meter = new Meter(meterName);
        _legacyCreate = _meter.CreateCounter<long>(
            LegacyCreateCounterName,
            description: "Documents created from legacy queue messages without a prior ingest intent");
    }

    public void RecordLegacyCreate() => _legacyCreate.Add(1);

    public void Dispose() => _meter.Dispose();
}
