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
    public const string ProcessingOutcomeCounterName = "backend.document_processing.outcomes";

    public static DocumentIngestMetrics Shared { get; } = new(MeterName);

    private readonly Meter _meter;
    private readonly Counter<long> _legacyCreate;
    private readonly Counter<long> _processingOutcomes;

    public DocumentIngestMetrics(string meterName)
    {
        _meter = new Meter(meterName);
        _legacyCreate = _meter.CreateCounter<long>(
            LegacyCreateCounterName,
            description: "Documents created from legacy queue messages without a prior ingest intent");
        _processingOutcomes = _meter.CreateCounter<long>(
            ProcessingOutcomeCounterName,
            description: "Document processor final outcomes");
    }

    public void RecordLegacyCreate() => SafeAdd(_legacyCreate);

    public void RecordProcessingOutcome(DocumentProcessingOutcome outcome)
    {
        var value = outcome switch
        {
            DocumentProcessingOutcome.Success => "success",
            DocumentProcessingOutcome.RetryableFailure => "retryable_failure",
            DocumentProcessingOutcome.TerminalFailure => "terminal_failure",
            _ => null,
        };

        if (value is not null)
        {
            SafeAdd(_processingOutcomes, new KeyValuePair<string, object?>("outcome", value));
        }
    }

    private static void SafeAdd(Counter<long> counter, KeyValuePair<string, object?>? tag = null)
    {
        try
        {
            if (tag is { } value)
            {
                counter.Add(1, value);
            }
            else
            {
                counter.Add(1);
            }
        }
        catch
        {
            // Instrument listeners are optional observability; they must not affect processing.
        }
    }

    public void Dispose() => _meter.Dispose();
}
