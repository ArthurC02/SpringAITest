using System.Text.Json;
using System.Text.Json.Nodes;
using Backend.Api.AgentRuns;
using Backend.Api.Agents;
using Backend.Api.Common;
using Backend.Api.Contexts;
using Backend.Api.OrchestratorRuns;
using Backend.Api.Skills;
using Backend.Api.Workflows;

namespace Backend.Api.Tests;

public sealed class SharedRunPolicyTests
{
    [Fact]
    public void CanonicalJsonTree_IsSharedByDefinitionAndContextContracts()
    {
        const string input = "{\"\uE000\":{\"z\":null,\"a\":[{\"b\":2,\"a\":1}]},\"\uD83D\uDE00\":\"astral\"}";
        var source = JsonNode.Parse(input)!;
        var normalized = CanonicalJsonTree.Normalize(source)!;

        Assert.Equal(
            AgentCanonicalizer.CanonicalizeDefinition(input),
            WorkflowCanonicalizer.Canonicalize(input));
        using var document = JsonDocument.Parse(input);
        Assert.Equal(
            AgentCanonicalizer.CanonicalizeDefinition(input),
            ContextCanonicalizer.CanonicalizeDefinition(document.RootElement));
        Assert.True(JsonNode.DeepEquals(
            normalized,
            JsonNode.Parse(AgentRunSnapshotBuilder.CanonicalizeJson(input))));

        source["\uE000"]!["a"]![0]!["a"] = 99;
        Assert.Equal(1, normalized["\uE000"]!["a"]![0]!["a"]!.GetValue<int>());
    }

    [Fact]
    public void AgentRunPolicies_KeepLeaseRecoveryAndEventRulesInParity()
    {
        const string token = "lease-token";
        var now = DateTime.UtcNow;
        Assert.True(AgentRunLeasePolicy.Matches(
            SkillHash.Sha256(token), 3, now.AddSeconds(1), token, 3, now));
        Assert.False(AgentRunLeasePolicy.Matches(
            SkillHash.Sha256(token), 3, now, token, 3, now));

        Assert.True(AgentRunRecoveryPolicy.HasValidCheckpointSeed(0, null, 0));
        Assert.False(AgentRunRecoveryPolicy.HasValidCheckpointSeed(0, "checkpoint", 1));
        Assert.Equal(
            "run_recovery_counter_exhausted",
            AgentRunRecoveryPolicy.CounterError(1, long.MaxValue - 2, 0, 0, 0));

        var safePayload = JsonDocument.Parse("{\"status\":\"ok\"}").RootElement.Clone();
        var secretPayload = JsonDocument.Parse("{\"token\":\"secret\"}").RootElement.Clone();
        Assert.True(AgentRunEventPolicy.IsSafePayload(safePayload));
        Assert.False(AgentRunEventPolicy.IsSafePayload(secretPayload));

        var replay = new AgentRunEventAppend(
            Guid.NewGuid(), "event", " node ", "ignored", safePayload);
        Assert.True(AgentRunEventPolicy.ReplayMatches(
            "event", "node", "snapshot", "{\"status\":\"ok\"}", replay, "snapshot"));
    }

    [Theory]
    [InlineData(100)]
    [InlineData(200)]
    [InlineData(500)]
    public void EventNormalization_DoesNotSplitSurrogatePairs(int max)
    {
        var source = new string('x', max - 1) + "😀";

        var normalized = AgentRunEventPolicy.Normalize(source, max);

        Assert.Equal(new string('x', max - 1), normalized);
        Assert.False(char.IsHighSurrogate(normalized![^1]));
    }

    [Theory]
    [InlineData(32_768)]
    [InlineData(65_536)]
    [InlineData(1_048_576)]
    public void JsonCaps_UseUtf8BytesAtTheBoundary(int cap)
    {
        var payload = JsonDocument.Parse(
            "{\"data\":\"" + new string('x', cap - 11) + "\"}").RootElement.Clone();

        Assert.Equal(cap, JsonUtf8.ByteCount(payload));
        Assert.True(AgentRunEventPolicy.WithinJsonLimit(payload, cap));
        Assert.False(AgentRunEventPolicy.WithinJsonLimit(payload, cap - 1));
    }

    [Fact]
    public void EventPayloadCap_RejectsUnicodeThatFitsOnlyByUtf16Length()
    {
        var accepted = JsonDocument.Parse(
            "{\"data\":\"" + string.Concat(Enumerable.Repeat("😀", 8_189)) + "\"}").RootElement.Clone();
        var rejected = JsonDocument.Parse(
            "{\"data\":\"" + string.Concat(Enumerable.Repeat("😀", 8_190)) + "\"}").RootElement.Clone();

        Assert.Equal(32_767, JsonUtf8.ByteCount(accepted));
        Assert.Equal(32_771, JsonUtf8.ByteCount(rejected));
        Assert.True(AgentRunEventPolicy.IsSafePayload(accepted));
        Assert.False(AgentRunEventPolicy.IsSafePayload(rejected));
    }

    [Fact]
    public void OrchestratorSnapshotProjection_UsesTheSameBudgetAndPinView()
    {
        using var document = JsonDocument.Parse("""
            {
              "token_budget": 500,
              "limits": {
                "max_context_rounds": 2,
                "max_tasks": 3,
                "max_child_runs": 4,
                "max_concurrency": 5,
                "max_repair_rounds": 6,
                "timeout_seconds": 7.5
              },
              "workers": [{
                "agent_id": "11111111-1111-1111-1111-111111111111",
                "agent_revision": 2,
                "workflow_id": "22222222-2222-2222-2222-222222222222",
                "workflow_revision": 3,
                "snapshot_hash": "worker-hash",
                "token_cap": 400
              }],
              "verifier": {
                "agent_id": "33333333-3333-3333-3333-333333333333",
                "agent_revision": 4,
                "workflow_id": "44444444-4444-4444-4444-444444444444",
                "workflow_revision": 5,
                "snapshot_hash": "verifier-hash",
                "token_cap": 300
              }
            }
            """);

        var budgets = OrchestratorRunSnapshotProjection.Budgets(document.RootElement);
        Assert.Equal(5, budgets.GetProperty("maxConcurrency").GetInt32());
        Assert.Equal(500, budgets.GetProperty("tokenBudget").GetInt32());
        var pin = OrchestratorRunSnapshotProjection.FindPin(
            document.RootElement,
            "worker",
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            2);
        Assert.Equal("worker-hash", pin?.Hash);
        Assert.Equal(400, pin?.TokenCap);
    }
}
