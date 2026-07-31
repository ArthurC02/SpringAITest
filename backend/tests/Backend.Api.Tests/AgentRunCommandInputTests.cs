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

    // 「整包不是物件」已由上面的 42 覆蓋;這裡補另一個等價類:是物件、message 存在
    // 但型別錯。IsMessage 只認字串,錯型別若被放行,canonical 編碼會在 GetString()
    // 上丟例外,而不是乾淨地回一個驗證失敗。
    [Theory]
    [InlineData("""{"message":42}""")]
    [InlineData("""{"message":true}""")]
    [InlineData("""{"message":null}""")]
    [InlineData("""{"message":["valid"]}""")]
    [InlineData("""{"message":{"text":"valid"}}""")]
    public void Start_RejectsNonStringMessage(string json)
    {
        Assert.False(Validate(json, "start", out _));
    }

    // message 上限 16384(start 與 resume 共用同一個 IsMessage):on-point 放行、
    // off-point 拒收。長度寫錯會讓合法的長提問在派工前就被判成 invalid input。
    [Theory]
    [InlineData(16_384, true)]
    [InlineData(16_385, false)]
    public void Start_MessageIsCappedAt16384(int messageLength, bool expected)
    {
        var json = $$"""{"message":"{{new string('m', messageLength)}}"}""";

        Assert.Equal(expected, Validate(json, "start", out _));
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

    // canonical 編碼剩下的三個分支。command_input 存成 jsonb,claim 時重讀再比對
    // 寫入當下算的 hash,所以每個分支的正規化範圍都是契約:頂層鍵順序不影響 hash、
    // 值被竄改必須抓到、選填的 task_envelope 不可被整棵砍掉還算出同一個 hash。
    // 注意 task_envelope 是用 WriteTo 原樣寫出的 —— 巢狀鍵順序「不會」被重新正規化,
    // 這是實際行為,一併釘住以免無聲改變。
    [Fact]
    public void CanonicalHash_CoversStartCancelAndDeadlineCleanupBranches()
    {
        var start = Parse(
            """{"message":"go","task_envelope":{"objective":"research","priority":1}}""");
        var startTopLevelReordered = Parse(
            """{"task_envelope":{"objective":"research","priority":1},"message":"go"}""");
        var startNestedReordered = Parse(
            """{"message":"go","task_envelope":{"priority":1,"objective":"research"}}""");
        var startTampered = Parse(
            """{"message":"go","task_envelope":{"objective":"exfiltrate","priority":1}}""");

        var startHash = AgentRunCommandInput.CanonicalSha256(start, "start");
        Assert.Equal(
            startHash,
            AgentRunCommandInput.CanonicalSha256(startTopLevelReordered, "start"));
        Assert.NotEqual(
            startHash,
            AgentRunCommandInput.CanonicalSha256(startNestedReordered, "start"));
        Assert.True(AgentRunCommandInput.MatchesCanonicalSha256(
            startTopLevelReordered,
            "start",
            startHash));
        Assert.False(AgentRunCommandInput.MatchesCanonicalSha256(
            startTampered,
            "start",
            startHash));
        // 整棵 task_envelope 被拿掉也算竄改,不能與帶 envelope 的 start 撞 hash。
        Assert.False(AgentRunCommandInput.MatchesCanonicalSha256(
            Parse("""{"message":"go"}"""),
            "start",
            startHash));

        // cancel:reason 的 null 與字串是不同型別的值,不得算出同一個 hash。
        var cancelNullHash = AgentRunCommandInput.CanonicalSha256(
            Parse("""{"reason":null}"""),
            "cancel");
        Assert.True(AgentRunCommandInput.MatchesCanonicalSha256(
            Parse("""{"reason":null}"""),
            "cancel",
            cancelNullHash));
        Assert.False(AgentRunCommandInput.MatchesCanonicalSha256(
            Parse("""{"reason":"stop"}"""),
            "cancel",
            cancelNullHash));

        // deadline_cleanup:終局狀態被換掉必須抓到,否則 failed 會被改寫成 cancelled。
        var failedHash = AgentRunCommandInput.CanonicalSha256(
            Parse("""{"target_terminal":"failed"}"""),
            "deadline_cleanup");
        Assert.True(AgentRunCommandInput.MatchesCanonicalSha256(
            Parse("""{"target_terminal":"failed"}"""),
            "deadline_cleanup",
            failedHash));
        Assert.False(AgentRunCommandInput.MatchesCanonicalSha256(
            Parse("""{"target_terminal":"cancelled"}"""),
            "deadline_cleanup",
            failedHash));

        static JsonElement Parse(string json)
            => JsonDocument.Parse(json).RootElement.Clone();
    }

    private static string V2CheckpointRef(long generation, char hashCharacter)
        => $"v2:{generation}:{new string(hashCharacter, 64)}:{Guid.NewGuid():D}";
}
