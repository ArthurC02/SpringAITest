using System.Text.Json;
using Backend.Api.Skills;

namespace Backend.Api.AgentRuns;

internal enum AgentRunCommandValidationMode
{
    DirectClaim,
    Recovery,
}

internal static class AgentRunCommandInput
{
    private const int MaxMessageLength = 16_384;
    private const int MaxCancelReasonLength = 500;

    public static bool TryParseAndValidate(
        string json,
        string commandType,
        long checkpointGeneration,
        long checkpointVersion,
        string? checkpointRef,
        AgentRunCommandValidationMode mode,
        out JsonElement input,
        out string? targetTerminal)
    {
        input = default;
        targetTerminal = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return TryValidate(
                document.RootElement,
                commandType,
                checkpointGeneration,
                checkpointVersion,
                checkpointRef,
                mode,
                out input,
                out targetTerminal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool TryValidate(
        JsonElement candidate,
        string commandType,
        long checkpointGeneration,
        long checkpointVersion,
        string? checkpointRef,
        AgentRunCommandValidationMode mode,
        out JsonElement input,
        out string? targetTerminal)
    {
        input = default;
        targetTerminal = null;
        if (candidate.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var valid = commandType switch
        {
            "start" => IsStart(candidate),
            "resume" => IsResume(
                candidate,
                checkpointGeneration,
                checkpointVersion,
                checkpointRef,
                mode),
            "cancel" => IsCancel(candidate),
            "deadline_cleanup" => IsDeadlineCleanup(
                candidate,
                out targetTerminal),
            _ => false,
        };
        if (!valid)
        {
            targetTerminal = null;
            return false;
        }

        input = candidate.Clone();
        return true;
    }

    public static string CanonicalSha256(
        JsonElement validatedInput,
        string commandType)
        => SkillHash.Sha256(CanonicalUtf8(validatedInput, commandType));

    public static bool MatchesCanonicalSha256(
        JsonElement validatedInput,
        string commandType,
        string? expectedSha256)
        => SkillHash.MatchesSha256(
            CanonicalUtf8(validatedInput, commandType),
            expectedSha256);

    private static bool IsStart(JsonElement input)
        => (HasExactProperties(input, "message")
            || HasExactProperties(input, "message", "task_envelope"))
           && IsMessage(input.GetProperty("message"))
           && (!input.TryGetProperty("task_envelope", out var envelope)
               || envelope.ValueKind == JsonValueKind.Object);

    private static bool IsResume(
        JsonElement input,
        long checkpointGeneration,
        long checkpointVersion,
        string? currentCheckpointRef,
        AgentRunCommandValidationMode mode)
    {
        if (!HasExactProperties(
                input,
                "message",
                "expected_checkpoint_version",
                "expected_checkpoint_ref"))
        {
            return false;
        }

        var version = input.GetProperty("expected_checkpoint_version");
        var checkpointRef = input.GetProperty("expected_checkpoint_ref");
        return IsMessage(input.GetProperty("message"))
               && version.ValueKind == JsonValueKind.Number
               && version.TryGetInt64(out var parsedVersion)
               && parsedVersion > 0
               && checkpointRef.ValueKind == JsonValueKind.String
               && AgentRunCheckpointRef.IsValidPromotion(
                   currentCheckpointRef,
                   checkpointGeneration)
               && (mode == AgentRunCommandValidationMode.DirectClaim
                   ? parsedVersion == checkpointVersion
                     && string.Equals(
                         checkpointRef.GetString(),
                         currentCheckpointRef,
                         StringComparison.Ordinal)
                   : parsedVersion <= checkpointVersion
                     && AgentRunCheckpointRef.IsValidAtOrBefore(
                         checkpointRef.GetString(),
                         checkpointGeneration));
    }

    private static bool IsCancel(JsonElement input)
    {
        if (!HasExactProperties(input, "reason"))
        {
            return false;
        }

        var reason = input.GetProperty("reason");
        return reason.ValueKind == JsonValueKind.Null
               || reason.ValueKind == JsonValueKind.String
               && reason.GetString()!.Length <= MaxCancelReasonLength;
    }

    private static bool IsDeadlineCleanup(
        JsonElement input,
        out string? targetTerminal)
    {
        targetTerminal = null;
        if (!HasExactProperties(input, "target_terminal"))
        {
            return false;
        }

        var target = input.GetProperty("target_terminal");
        var value = target.ValueKind == JsonValueKind.String
            ? target.GetString()
            : null;
        if (target.ValueKind != JsonValueKind.String
            || value is not
                (AgentRunStatuses.Failed or AgentRunStatuses.Cancelled))
        {
            return false;
        }

        targetTerminal = value;
        return true;
    }

    private static bool IsMessage(JsonElement value)
        => value.ValueKind == JsonValueKind.String
           && value.GetString() is { } message
           && !string.IsNullOrWhiteSpace(message)
           && message.Length <= MaxMessageLength;

    private static bool HasExactProperties(
        JsonElement input,
        params string[] expected)
    {
        var properties = input.EnumerateObject().Select(item => item.Name).ToArray();
        return properties.Length == expected.Length
               && properties.ToHashSet(StringComparer.Ordinal)
                   .SetEquals(expected);
    }

    private static byte[] CanonicalUtf8(
        JsonElement input,
        string commandType)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            switch (commandType)
            {
                case "start":
                    writer.WriteString(
                        "message",
                        input.GetProperty("message").GetString());
                    if (input.TryGetProperty("task_envelope", out var taskEnvelope))
                    {
                        writer.WritePropertyName("task_envelope");
                        taskEnvelope.WriteTo(writer);
                    }
                    break;
                case "resume":
                    writer.WriteString(
                        "message",
                        input.GetProperty("message").GetString());
                    writer.WriteNumber(
                        "expected_checkpoint_version",
                        input.GetProperty("expected_checkpoint_version").GetInt64());
                    writer.WriteString(
                        "expected_checkpoint_ref",
                        input.GetProperty("expected_checkpoint_ref").GetString());
                    break;
                case "cancel":
                    writer.WritePropertyName("reason");
                    var reason = input.GetProperty("reason");
                    if (reason.ValueKind == JsonValueKind.Null)
                    {
                        writer.WriteNullValue();
                    }
                    else
                    {
                        writer.WriteStringValue(reason.GetString());
                    }
                    break;
                case "deadline_cleanup":
                    writer.WriteString(
                        "target_terminal",
                        input.GetProperty("target_terminal").GetString());
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unsupported agent-run command type '{commandType}'.");
            }
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }
}
