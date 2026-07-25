using System.Text.Json;
using Backend.Api.AgentRuns;

namespace Backend.Api.Tests;

public sealed class AgentRunCommandInputTests
{
    [Fact]
    public void Resume_AllowsAcceptedOriginAtOrBeforeCurrentCheckpoint()
    {
        const long currentGeneration = 7;
        const long currentVersion = 3;
        var currentCheckpointRef = V2CheckpointRef(currentGeneration, 'a');
        var acceptedOrigin = JsonSerializer.SerializeToElement(new
        {
            message = "answer",
            expected_checkpoint_version = currentVersion - 1,
            expected_checkpoint_ref = V2CheckpointRef(
                currentGeneration - 1,
                'b'),
        });
        var futureVersion = JsonSerializer.SerializeToElement(new
        {
            message = "answer",
            expected_checkpoint_version = currentVersion + 1,
            expected_checkpoint_ref = currentCheckpointRef,
        });
        var futureGeneration = JsonSerializer.SerializeToElement(new
        {
            message = "answer",
            expected_checkpoint_version = currentVersion,
            expected_checkpoint_ref = V2CheckpointRef(
                currentGeneration + 1,
                'c'),
        });
        var unrelatedOlderRef = JsonSerializer.SerializeToElement(new
        {
            message = "answer",
            expected_checkpoint_version = currentVersion - 1,
            expected_checkpoint_ref = V2CheckpointRef(
                currentGeneration - 1,
                'd'),
        });

        Assert.True(Valid(acceptedOrigin));
        Assert.True(Valid(unrelatedOlderRef));
        Assert.False(Valid(futureVersion));
        Assert.False(Valid(futureGeneration));

        Assert.False(Direct(acceptedOrigin));
        var exactCurrent = JsonSerializer.SerializeToElement(new
        {
            message = "answer",
            expected_checkpoint_version = currentVersion,
            expected_checkpoint_ref = currentCheckpointRef,
        });
        Assert.True(Direct(exactCurrent));

        bool Valid(JsonElement candidate) =>
            AgentRunCommandInput.TryValidate(
                candidate,
                "resume",
                currentGeneration,
                currentVersion,
                currentCheckpointRef,
                AgentRunCommandValidationMode.Recovery,
                out _,
                out _);

        bool Direct(JsonElement candidate) =>
            AgentRunCommandInput.TryValidate(
                candidate,
                "resume",
                currentGeneration,
                currentVersion,
                currentCheckpointRef,
                AgentRunCommandValidationMode.DirectClaim,
                out _,
                out _);
    }

    [Fact]
    public void Start_RejectsScalarEmptyAndUnknownFields()
    {
        Assert.False(Valid(JsonSerializer.SerializeToElement(42)));
        Assert.False(Valid(JsonSerializer.SerializeToElement(new { })));
        Assert.False(Valid(JsonSerializer.SerializeToElement(new
        {
            message = "valid",
            injected = true,
        })));
        Assert.True(Valid(JsonSerializer.SerializeToElement(new
        {
            message = "valid",
        })));

        static bool Valid(JsonElement candidate) =>
            AgentRunCommandInput.TryValidate(
                candidate,
                "start",
                0,
                0,
                null,
                AgentRunCommandValidationMode.DirectClaim,
                out _,
                out _);
    }

    [Fact]
    public void CanonicalHash_NormalizesJsonShapeButCommitsTypedValues()
    {
        var first = JsonDocument.Parse(
            """
            {
              "message": "answer",
              "expected_checkpoint_version": 3,
              "expected_checkpoint_ref": "v2:7:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa:00000000-0000-0000-0000-000000000001"
            }
            """).RootElement.Clone();
        var reordered = JsonDocument.Parse(
            """{"expected_checkpoint_ref":"v2:7:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa:00000000-0000-0000-0000-000000000001","expected_checkpoint_version":3,"message":"answer"}""")
            .RootElement.Clone();
        var tampered = JsonDocument.Parse(
            """{"message":"different","expected_checkpoint_version":3,"expected_checkpoint_ref":"v2:7:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa:00000000-0000-0000-0000-000000000001"}""")
            .RootElement.Clone();

        var expected = AgentRunCommandInput.CanonicalSha256(first, "resume");
        Assert.Equal(
            expected,
            AgentRunCommandInput.CanonicalSha256(reordered, "resume"));
        Assert.NotEqual(
            expected,
            AgentRunCommandInput.CanonicalSha256(tampered, "resume"));
        Assert.True(
            AgentRunCommandInput.MatchesCanonicalSha256(
                reordered,
                "resume",
                expected));
        Assert.False(
            AgentRunCommandInput.MatchesCanonicalSha256(
                tampered,
                "resume",
                expected));
    }

    private static string V2CheckpointRef(long generation, char hashCharacter)
        => $"v2:{generation}:{new string(hashCharacter, 64)}:{Guid.NewGuid():D}";
}
