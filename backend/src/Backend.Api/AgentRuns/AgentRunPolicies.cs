using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Backend.Api.Agents;
using Backend.Api.Common;
using Backend.Api.Skills;

namespace Backend.Api.AgentRuns;

internal static class AgentRunLeasePolicy
{
    public static string NewToken()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    public static bool Matches(
        string? tokenHash,
        long currentGeneration,
        DateTime? expiresAt,
        string? token,
        long requestedGeneration,
        DateTime now)
        => tokenHash is not null
           && requestedGeneration > 0
           && currentGeneration == requestedGeneration
           && expiresAt > now
           && !string.IsNullOrWhiteSpace(token)
           && string.Equals(tokenHash, SkillHash.Sha256(token), StringComparison.Ordinal);
}

internal static class AgentRunRecoveryPolicy
{
    public static bool HasValidCheckpointSeed(
        long checkpointGeneration,
        string? checkpointRef,
        long checkpointVersion)
        => checkpointVersion == 0
            ? checkpointGeneration == 0 && checkpointRef is null
            : checkpointVersion > 0
              && AgentRunCheckpointRef.IsValidPromotion(checkpointRef, checkpointGeneration);

    public static bool HasValidPinnedIdentity(string? tenantId, string? userId, string? role)
        => HasValidIdentityValue(tenantId, AgentExecutionContract.MaxCallerIdentityLength)
           && HasValidIdentityValue(userId, AgentExecutionContract.MaxCallerIdentityLength)
           && HasValidIdentityValue(role, AgentExecutionContract.MaxCallerRoleLength);

    public static string? CounterError(
        long leaseGeneration,
        long stateVersion,
        long checkpointVersion,
        long eventAckCursor,
        long latestEventSequence)
    {
        if (leaseGeneration is < 0 or >= long.MaxValue - 1)
        {
            return "run_recovery_generation_exhausted";
        }

        return stateVersion is < 0 or >= long.MaxValue - 2
               || checkpointVersion is < 0 or >= long.MaxValue - 1
               || eventAckCursor is < 0 or >= long.MaxValue - 1
               || latestEventSequence is < 0 or >= long.MaxValue - 1
            ? "run_recovery_counter_exhausted"
            : null;
    }

    private static bool HasValidIdentityValue(string? value, int maxLength)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength;
}

internal static class AgentRunEventPolicy
{
    public static bool WithinJsonLimit(JsonElement? value, int max)
        => JsonUtf8.IsNullOrWithinLimit(value, max);

    public static string? JsonText(JsonElement? value)
        => value is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } json
            ? json.GetRawText()
            : null;

    public static string? Normalize(string? value, int max)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized) || max <= 0)
        {
            return null;
        }

        var length = Math.Min(normalized.Length, max);
        if (length < normalized.Length && char.IsHighSurrogate(normalized[length - 1]))
        {
            length--;
        }

        return normalized[..length];
    }

    public static bool IsSafePayload(JsonElement? payload)
        => JsonUtf8.IsNullOrWithinLimit(payload, 32_768)
           && (payload is null or { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined }
               || IsSafeNode(payload.Value, 0));

    public static bool ReplayMatches(
        string priorEventType,
        string? priorNodeId,
        string priorSnapshotHash,
        string priorPayload,
        AgentRunEventAppend candidate,
        string snapshotHash)
        => string.Equals(priorEventType, candidate.EventType?.Trim(), StringComparison.Ordinal)
           && string.Equals(priorNodeId, Normalize(candidate.NodeId, 200), StringComparison.Ordinal)
           && string.Equals(priorSnapshotHash, snapshotHash, StringComparison.Ordinal)
           && JsonNode.DeepEquals(
               JsonNode.Parse(priorPayload),
               JsonNode.Parse(JsonText(candidate.Payload) ?? "{}"));

    private static bool IsSafeNode(JsonElement node, int depth)
    {
        if (depth > 8)
        {
            return false;
        }

        if (node.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in node.EnumerateObject())
            {
                var key = property.Name.ToLowerInvariant();
                if (key is "prompt" or "message" or "content" or "args" or "arguments"
                    or "value" or "token" or "secret" or "resource"
                    || !IsSafeNode(property.Value, depth + 1))
                {
                    return false;
                }
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
            {
                if (!IsSafeNode(item, depth + 1))
                {
                    return false;
                }
            }
        }

        return true;
    }
}
