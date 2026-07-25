using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Backend.Api.AgentRuns;
using Backend.Api.Agents;
using Backend.Api.Skills;

namespace Backend.Api.Tests;

public sealed class AgentRunSnapshotContractTests
{
    [Fact]
    public void CanonicalDefinition_ExactFourMiBAccepted_PlusOneRejected()
    {
        var exact = DefinitionWithExactBytes(
            AgentExecutionContract.MaxCanonicalDefinitionBytes);
        var plusOne = DefinitionWithExactBytes(
            AgentExecutionContract.MaxCanonicalDefinitionBytes + 1);

        Assert.Empty(AgentCanonicalizer.Validate(exact, "bounded-agent"));
        Assert.Contains(
            AgentCanonicalizer.Validate(plusOne, "bounded-agent"),
            error => error.Field == "definition");
    }

    [Fact]
    public void ExecutionArtifact_ExactEightMiBAccepted_PlusOneRejected()
    {
        var exact = CanonicalSnapshotWithExactBytes(
            AgentExecutionContract.MaxSnapshotCanonicalBytes);
        var artifact = AgentRunSnapshotBuilder.CreateExecutionArtifact(
            exact,
            SkillHash.Sha256(Encoding.UTF8.GetBytes(exact)));

        using var envelope = JsonDocument.Parse(artifact);
        var encoded = envelope.RootElement
            .GetProperty("snapshot_canonical_base64")
            .GetString()!;
        Assert.Equal(
            AgentExecutionContract.MaxSnapshotCanonicalBase64Length,
            encoded.Length);
        Assert.Equal(
            Encoding.UTF8.GetBytes(exact),
            Convert.FromBase64String(encoded));

        var plusOne = CanonicalSnapshotWithExactBytes(
            AgentExecutionContract.MaxSnapshotCanonicalBytes + 1);
        Assert.Throws<InvalidOperationException>(() =>
            AgentRunSnapshotBuilder.CreateExecutionArtifact(
                plusOne,
                SkillHash.Sha256(Encoding.UTF8.GetBytes(plusOne))));
    }

    [Fact]
    public void Snapshot_MaximumSkillCountAndDescriptions_StaysWithinAggregateCap()
    {
        var bindings = Enumerable.Range(0, AgentExecutionContract.MaxSkillBindings)
            .Select(index => new AgentRevisionSkillInfo(
                $"skill-{index:D3}",
                1,
                index,
                true))
            .ToArray();
        var definition = DefinitionWithPadding(string.Empty, bindings);
        var agent = new PublishedAgentSnapshotSource(
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            new string('n', AgentExecutionContract.MaxAgentNameLength),
            1,
            definition,
            SkillHash.Sha256(definition),
            Guid.Parse(AgentDefaults.RuntimeWorkflowId),
            AgentDefaults.RuntimeWorkflowRevision,
            bindings);
        var workflowDefinition = AgentRunSnapshotBuilder.CanonicalizeJson(
            AgentDefaults.RuntimeWorkflowDefinition);
        var workflow = new WorkflowSnapshotSource(
            Guid.Parse(AgentDefaults.RuntimeWorkflowId),
            AgentDefaults.RuntimeWorkflowRevision,
            1,
            workflowDefinition,
            SkillHash.Sha256(workflowDefinition),
            new string('v', AgentExecutionContract.MaxWorkflowContractVersionLength));
        var description = new string(
            '\u2028',
            AgentExecutionContract.MaxSkillDescriptionLength);
        var skills = bindings.Select(binding =>
        {
            var immutableDefinition =
                $"name: {binding.Skill}\ndescription: maximum description\nflow: []\n";
            return new SkillSnapshotSource(
                Guid.NewGuid(),
                binding.Skill,
                description,
                binding.SkillRevision,
                "flow",
                immutableDefinition,
                SkillHash.Sha256(immutableDefinition),
                null);
        }).ToArray();

        Assert.Empty(AgentRunSnapshotBuilder.ValidateExecutionContract(
            agent,
            workflow,
            skills,
            "tenant-a",
            "admin-a",
            "ADMIN"));
        var built = AgentRunSnapshotBuilder.Build(
            Guid.Parse("22222222-2222-4222-8222-222222222222"),
            "tenant-a",
            "admin-a",
            "ADMIN",
            Array.Empty<string>(),
            agent,
            workflow,
            skills);

        Assert.InRange(
            built.CanonicalByteLength,
            3 * 1024 * 1024,
            AgentExecutionContract.MaxSnapshotCanonicalBytes);
        var artifact = AgentRunSnapshotBuilder.CreateExecutionArtifact(
            built.StoredSnapshot,
            built.SnapshotHash);
        using var envelope = JsonDocument.Parse(artifact);
        Assert.Equal(
            built.SnapshotHash,
            envelope.RootElement.GetProperty("snapshot_hash").GetString());
    }

    [Fact]
    public void CanonicalVector_PinsUtf16KeyOrderNumberLexemesAndEscapes()
    {
        const string source =
            """
            {
              "\uE000": "bmp",
              "\uD83D\uDE00": "astral",
              "numbers": {
                "small": 0.00000100,
                "negative_zero": -0,
                "large": 9007199254740993123456789,
                "exponent": 1e+3,
                "decimal": 1.230
              },
              "strings": "line\u2028paragraph\u2029next\u0085nbsp\u00A0control\u0001astral\uD83D\uDE00中"
            }
            """;
        const string expected =
            """{"numbers":{"decimal":1.230,"exponent":1e+3,"large":9007199254740993123456789,"negative_zero":-0,"small":0.00000100},"strings":"line\u2028paragraph\u2029next\u0085nbsp\u00A0control\u0001astral\uD83D\uDE00中","\uD83D\uDE00":"astral","\uE000":"bmp"}""";

        var canonical = AgentRunSnapshotBuilder.CanonicalizeJson(source);

        Assert.Equal(expected, canonical);
        Assert.Equal(
            "a158ea00b3e6ba6bbe1c749d0fc58f389329fc002877425a28afccba95f4b818",
            SkillHash.Sha256(Encoding.UTF8.GetBytes(canonical)));
        var snapshot =
            "{\"agent\":" + canonical
            + ",\"caller\":{},\"mode\":\"test\","
            + "\"run_id\":\"22222222-2222-4222-8222-222222222222\","
            + "\"skills\":[],\"workflow\":{}}";
        Assert.Equal(
            "e022b786c71cc3c4e0e0090c00b54d6e371a4ee83c798f84e44111b5d96c00df",
            SkillHash.Sha256(Encoding.UTF8.GetBytes(snapshot)));
        var artifact = AgentRunSnapshotBuilder.CreateExecutionArtifact(
            snapshot,
            SkillHash.Sha256(Encoding.UTF8.GetBytes(snapshot)));
        using var envelope = JsonDocument.Parse(artifact);
        Assert.Equal(
            Encoding.UTF8.GetBytes(snapshot),
            Convert.FromBase64String(envelope.RootElement
                .GetProperty("snapshot_canonical_base64")
                .GetString()!));
    }

    private static string DefinitionWithExactBytes(int targetBytes)
    {
        var empty = DefinitionWithPadding(
            string.Empty,
            Array.Empty<AgentRevisionSkillInfo>());
        var paddingLength = targetBytes - Encoding.UTF8.GetByteCount(empty);
        Assert.True(paddingLength >= 0);
        var definition = DefinitionWithPadding(
            new string('x', paddingLength),
            Array.Empty<AgentRevisionSkillInfo>());
        Assert.Equal(targetBytes, Encoding.UTF8.GetByteCount(definition));
        return definition;
    }

    private static string DefinitionWithPadding(
        string padding,
        IReadOnlyList<AgentRevisionSkillInfo> bindings)
        => AgentCanonicalizer.Canonicalize(new AgentUpsert(
            Slug: null,
            Name: null,
            Description: null,
            SystemPrompt: "bounded prompt",
            ExecutionRoles: new[] { "worker" },
            Capabilities: null,
            OutputContract: JsonSerializer.SerializeToElement(new { padding }),
            Audience: new[] { "ADMIN" },
            AllowedTools: Array.Empty<string>(),
            SkillBindings: bindings.Select(binding =>
                    new AgentSkillBinding(binding.Skill, "latest"))
                .ToArray(),
            KnowledgeSources: Array.Empty<string>(),
            BusinessRules: null,
            RuntimeLimits: new AgentRuntimeLimits(),
            RuntimeWorkflow: new AgentWorkflowRef(
                AgentDefaults.RuntimeWorkflowId,
                AgentDefaults.RuntimeWorkflowRevision)));

    private static string CanonicalSnapshotWithExactBytes(int targetBytes)
    {
        const string prefix = "{\"agent\":{\"padding\":\"";
        const string suffix =
            "\"},\"caller\":{},\"mode\":\"test\","
            + "\"run_id\":\"22222222-2222-4222-8222-222222222222\","
            + "\"skills\":[],\"workflow\":{}}";
        var paddingLength =
            targetBytes
            - Encoding.UTF8.GetByteCount(prefix)
            - Encoding.UTF8.GetByteCount(suffix);
        Assert.True(paddingLength >= 0);
        var json = prefix + new string('x', paddingLength) + suffix;
        Assert.Equal(targetBytes, Encoding.UTF8.GetByteCount(json));
        return json;
    }
}
