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
        // D5 child start command 的合法形狀:{message, task_envelope};task_envelope 必須是物件。
        Assert.True(Valid(JsonSerializer.SerializeToElement(new
        {
            message = "valid",
            task_envelope = new { objective = "research" },
        })));
        Assert.False(Valid(JsonSerializer.SerializeToElement(new
        {
            message = "valid",
            task_envelope = 42,
        })));
        Assert.False(Valid(JsonSerializer.SerializeToElement(new { message = "   " })));

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

    // cancel reason 上限 500:on-point 放行、off-point 拒收。這些輸入是 poison-recovery
    // 比對 canonical hash 的實際型別,長度寫錯會讓合法 cancel 被 dead-letter。
    [Theory]
    [InlineData(null, true)]
    [InlineData(0, true)]
    [InlineData(500, true)]
    [InlineData(501, false)]
    public void Cancel_ReasonIsNullableStringUpTo500(int? reasonLength, bool expected)
    {
        var json = reasonLength is null
            ? """{"reason":null}"""
            : $$"""{"reason":"{{new string('r', reasonLength.Value)}}"}""";

        Assert.Equal(expected, Validate(json, "cancel", out _));
    }

    [Theory]
    [InlineData("cancel", """{}""")]                              // 缺 reason
    [InlineData("cancel", """{"reason":42}""")]                   // reason 非字串/非 null
    [InlineData("cancel", """{"reason":null,"extra":1}""")]       // 多出欄位
    [InlineData("deadline_cleanup", """{"target_terminal":"completed"}""")]
    [InlineData("deadline_cleanup", """{"target_terminal":""}""")]
    [InlineData("deadline_cleanup", """{"target_terminal":42}""")]
    [InlineData("deadline_cleanup", """{}""")]
    [InlineData("restart", """{"message":"x"}""")]                // 未知 commandType
    public void CommandInput_RejectsMalformedShapesAndUnknownType(string commandType, string json)
    {
        Assert.False(Validate(json, commandType, out var targetTerminal));
        // 被拒時 targetTerminal 必須清空,否則呼叫端可能拿到殘留的終局狀態。
        Assert.Null(targetTerminal);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public void DeadlineCleanup_AcceptsOnlyFailedOrCancelled(string terminal)
    {
        Assert.True(Validate($$"""{"target_terminal":"{{terminal}}"}""", "deadline_cleanup", out var target));
        Assert.Equal(terminal, target);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    public void Resume_RejectsNonPositiveOrFractionalCheckpointVersion(string version)
    {
        var checkpointRef = V2CheckpointRef(7, 'a');
        var json = $$"""
            {"message":"answer","expected_checkpoint_version":{{version}},"expected_checkpoint_ref":"{{checkpointRef}}"}
            """;

        Assert.False(AgentRunCommandInput.TryParseAndValidate(
            json, "resume", 7, 3, checkpointRef,
            AgentRunCommandValidationMode.Recovery, out _, out _));
    }

    // 未知 commandType 通過驗證是不可能的,但 canonical 編碼是獨立入口:
    // 它必須明確丟例外,而不是靜靜寫出一個空物件(那會讓兩個不同命令算出同一個 hash)。
    [Fact]
    public void CanonicalSha256_UnknownCommandType_Throws()
    {
        var input = JsonSerializer.SerializeToElement(new { message = "valid" });

        Assert.Throws<InvalidOperationException>(
            () => AgentRunCommandInput.CanonicalSha256(input, "restart"));
    }

    private static bool Validate(string json, string commandType, out string? targetTerminal)
        => AgentRunCommandInput.TryParseAndValidate(
            json, commandType, 0, 0, null,
            AgentRunCommandValidationMode.DirectClaim, out _, out targetTerminal);

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
