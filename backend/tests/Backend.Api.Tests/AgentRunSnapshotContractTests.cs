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
        var rejected = Assert.Throws<InvalidOperationException>(() =>
            AgentRunSnapshotBuilder.CreateExecutionArtifact(
                plusOne,
                SkillHash.Sha256(Encoding.UTF8.GetBytes(plusOne))));
        // ReadAuthoritativeSnapshot 有 5 種原因都拋同一型別;不驗訊息的話雜湊算錯也會讓這條假綠。
        Assert.Contains("canonical UTF-8 bytes", rejected.Message);
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
        // envelope 回音是自我一致的;獨立重算才證明 hash 真的蓋住這份聚合上限的 bytes。
        Assert.Equal(
            built.SnapshotHash,
            SkillHash.Sha256(Encoding.UTF8.GetBytes(built.StoredSnapshot)));
    }

    /// <summary>
    /// 上一條守 on-point(剛好 128 筆 / 4096 字 / 256 字元都必須收下);這條是另外半邊的 off-point:
    /// 三個上限各 +1 都必須各自報錯,否則常數打錯一位數不會有任何測試變紅。
    /// </summary>
    [Fact]
    public void ExecutionContract_RejectsSkillCountDescriptionAndAgentNameOverByOne()
    {
        var overCountSkills = Enumerable
            .Range(0, AgentExecutionContract.MaxSkillBindings + 1)
            .Select(index => SkillPin($"skill-{index:D3}"))
            .ToArray();
        Assert.Contains(
            ValidateExecutionContractWith(overCountSkills),
            error => error.Field == "skills"
                     && error.Message.Contains(
                         $"最多 {AgentExecutionContract.MaxSkillBindings} 筆"));

        Assert.Contains(
            ValidateExecutionContractWith(new[]
            {
                SkillPin(
                    "skill-000",
                    description: new string(
                        'd', AgentExecutionContract.MaxSkillDescriptionLength + 1)),
            }),
            error => error.Field == "skills.description");

        Assert.Contains(
            ValidateExecutionContractWith(
                Array.Empty<SkillSnapshotSource>(),
                agentName: new string('n', AgentExecutionContract.MaxAgentNameLength + 1)),
            error => error.Field == "name");
    }

    /// <summary>
    /// 每個 skill 級守衛都是「這個 pin 能不能被 D3 runtime 載入」的唯一判斷點,而本檔其他測試
    /// 餵進去的 skill 全部合法 —— 拿掉任何一條守衛今天都不會有測試變紅。
    /// </summary>
    [Fact]
    public void ExecutionContract_RejectsEverySkillLevelViolation()
    {
        // 合法基準先自證,否則以下每一條都可能因為無關原因假綠。
        Assert.Empty(ValidateExecutionContractWith(
            new[] { SkillPin("skill-a"), SkillPin("skill-b") }));

        Assert.Contains(
            ValidateExecutionContractWith(new[] { SkillPin("skill-a"), SkillPin("skill-a") }),
            error => error.Field == "skills" && error.Message.Contains("不可重複"));
        Assert.Contains(
            ValidateExecutionContractWith(new[] { SkillPin("  ") }),
            error => error.Field == "skills.name");
        Assert.Contains(
            ValidateExecutionContractWith(new[]
            {
                SkillPin(new string('s', AgentExecutionContract.MaxSkillNameLength + 1)),
            }),
            error => error.Field == "skills.name");
        Assert.Contains(
            ValidateExecutionContractWith(new[] { SkillPin("skill-a", revision: 0) }),
            error => error.Field == "skills"
                     && error.Message.Contains("無法由 D3 runtime 載入"));
        Assert.Contains(
            ValidateExecutionContractWith(new[] { SkillPin("skill-a", kind: "agent") }),
            error => error.Field == "skills"
                     && error.Message.Contains("無法由 D3 runtime 載入"));
    }

    /// <summary>
    /// Workflow pin 是快照裡唯一的圖來源;它的三個無效等價類在本檔從未出現過(其他測試一律
    /// 帶 seeded 合法 revision + contract version)。
    /// </summary>
    [Fact]
    public void ExecutionContract_RejectsInvalidWorkflowPin()
    {
        foreach (var invalid in new[]
                 {
                     WorkflowPin(revision: 0),
                     WorkflowPin(compilerContractVersion: "   "),
                     WorkflowPin(compilerContractVersion: new string(
                         'v', AgentExecutionContract.MaxWorkflowContractVersionLength + 1)),
                 })
        {
            Assert.Contains(
                ValidateExecutionContractWith(
                    Array.Empty<SkillSnapshotSource>(), workflow: invalid),
                error => error.Field == "runtime_workflow"
                         && error.Message.Contains("execution snapshot contract"));
        }
    }

    [Fact]
    public void OrchestratorChildSnapshot_ClampsAndHashesItsPinnedTokenCap()
    {
        var definition = DefinitionWithPadding(
            string.Empty,
            Array.Empty<AgentRevisionSkillInfo>(),
            new AgentRuntimeLimits(TokenBudget: 50_000));
        var agent = new PublishedAgentSnapshotSource(
            Guid.Parse("31111111-1111-4111-8111-111111111111"),
            "bounded-child",
            1,
            definition,
            SkillHash.Sha256(definition),
            Guid.Parse(AgentDefaults.RuntimeWorkflowId),
            AgentDefaults.RuntimeWorkflowRevision,
            Array.Empty<AgentRevisionSkillInfo>());
        var workflowDefinition = AgentRunSnapshotBuilder.CanonicalizeJson(
            AgentDefaults.RuntimeWorkflowDefinition);
        var workflow = new WorkflowSnapshotSource(
            Guid.Parse(AgentDefaults.RuntimeWorkflowId),
            AgentDefaults.RuntimeWorkflowRevision,
            1,
            workflowDefinition,
            SkillHash.Sha256(workflowDefinition),
            "1");

        var built = AgentRunSnapshotBuilder.Build(
            Guid.Parse("32222222-2222-4222-8222-222222222222"),
            "tenant-a", "admin-a", "ADMIN", Array.Empty<string>(),
            agent, workflow, Array.Empty<SkillSnapshotSource>(),
            "orchestrator-worker", 10_000,
            new OrchestratorChildSnapshotProvenance(
                Guid.Parse("33333333-3333-4333-8333-333333333333"),
                "bounded-task", 2));

        using var snapshot = JsonDocument.Parse(built.StoredSnapshot);
        Assert.Equal(10_000, snapshot.RootElement
            .GetProperty("orchestrator_token_cap").GetInt32());
        Assert.Equal("33333333-3333-4333-8333-333333333333", snapshot.RootElement
            .GetProperty("orchestrator_root_run_id").GetString());
        Assert.Equal("bounded-task", snapshot.RootElement
            .GetProperty("orchestrator_task_id").GetString());
        Assert.Equal(10_000, snapshot.RootElement.GetProperty("agent")
            .GetProperty("runtime_limits").GetProperty("token_budget").GetInt32());
        Assert.Equal(built.SnapshotHash,
            SkillHash.Sha256(Encoding.UTF8.GetBytes(built.StoredSnapshot)));
    }

    /// <summary>
    /// execution_kind 是三選一白名單,且必須在讀 definition 之前就擋下 —— 未知角色若能寫進
    /// 不可變快照,Workflow 端的 worker/verifier 角色強制就失去了唯一來源。
    /// </summary>
    [Fact]
    public void Build_RejectsUnsupportedExecutionKind()
    {
        var (agent, workflow) = MinimalAgentAndWorkflow(
            Guid.Parse("34111111-1111-4111-8111-111111111111"), "kind-guarded");

        var rejected = Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            AgentRunSnapshotBuilder.Build(
                Guid.Parse("34222222-2222-4222-8222-222222222222"),
                "tenant-a", "admin-a", "ADMIN", Array.Empty<string>(),
                agent, workflow, Array.Empty<SkillSnapshotSource>(),
                "orchestrator-planner");
        });

        Assert.Equal("executionKind", rejected.ParamName);
    }

    /// <summary>
    /// cap 守衛是五個條件的 OR,而上面那條 clamp 測試只走過唯一全合法的組合 —— 任一條件被刪掉
    /// 都不會變紅。每個無效等價類都必須讓子快照建不出來:建得出來就等於子 run 逃出根預算/來源。
    /// </summary>
    [Fact]
    public void Build_RejectsEveryInvalidOrchestratorTokenCapCombination()
    {
        var (agent, workflow) = MinimalAgentAndWorkflow(
            Guid.Parse("35111111-1111-4111-8111-111111111111"), "cap-guarded");
        var provenance = new OrchestratorChildSnapshotProvenance(
            Guid.Parse("35333333-3333-4333-8333-333333333333"), "bounded-task", 1);

        using var accepted = JsonDocument.Parse(
            Snapshot("orchestrator-worker", 10_000, provenance));
        Assert.Equal(
            10_000,
            accepted.RootElement.GetProperty("orchestrator_token_cap").GetInt32());

        Assert.Equal("orchestratorTokenCap", Rejected("direct-worker", 10_000, provenance));
        Assert.Equal("orchestratorTokenCap", Rejected("orchestrator-worker", 0, provenance));
        Assert.Equal(
            "orchestratorTokenCap",
            Rejected(
                "orchestrator-worker",
                AgentExecutionContract.MaxOrchestratorTokenCap + 1,
                provenance));
        Assert.Equal("orchestratorTokenCap", Rejected("orchestrator-worker", 10_000, null));
        Assert.Equal(
            "orchestratorTokenCap",
            Rejected(
                "orchestrator-worker", 10_000, provenance with { RootRunId = Guid.Empty }));
        Assert.Equal(
            "orchestratorTokenCap",
            Rejected("orchestrator-worker", 10_000, provenance with { TaskId = "  " }));
        Assert.Equal(
            "orchestratorTokenCap",
            Rejected("orchestrator-worker", 10_000, provenance with { Attempt = 0 }));

        string Snapshot(
            string executionKind,
            int tokenCap,
            OrchestratorChildSnapshotProvenance? childProvenance)
            => AgentRunSnapshotBuilder.Build(
                Guid.Parse("35222222-2222-4222-8222-222222222222"),
                "tenant-a", "admin-a", "ADMIN", Array.Empty<string>(),
                agent, workflow, Array.Empty<SkillSnapshotSource>(),
                executionKind, tokenCap, childProvenance).StoredSnapshot;

        string Rejected(
            string executionKind,
            int tokenCap,
            OrchestratorChildSnapshotProvenance? childProvenance)
            => Assert.Throws<ArgumentOutOfRangeException>(() =>
            {
                Snapshot(executionKind, tokenCap, childProvenance);
            }).ParamName!;
    }

    /// <summary>
    /// P1 (plan 03 §3): an unpinned Agent (no <c>prompt_manifest_revision</c>/<c>_sha256</c>) must
    /// produce a snapshot with no <c>agent.prompt_manifest</c> key at all -- not a null placeholder --
    /// so every pre-P1 caller's canonical bytes/hash are completely unaffected. The literal hash below
    /// pins that shape; if it ever changes for an unpinned Agent, this test catches the drift.
    /// </summary>
    [Fact]
    public void Snapshot_WithoutPromptManifestPin_OmitsKey_AndKeepsPinnedCanonicalHash()
    {
        var (agent, workflow) = MinimalAgentAndWorkflow(
            Guid.Parse("51111111-1111-4111-8111-111111111111"), "unpinned-agent");

        var built = AgentRunSnapshotBuilder.Build(
            Guid.Parse("52222222-2222-4222-8222-222222222222"),
            "tenant-a", "admin-a", "ADMIN", Array.Empty<string>(),
            agent, workflow, Array.Empty<SkillSnapshotSource>());

        using var snapshot = JsonDocument.Parse(built.StoredSnapshot);
        Assert.False(snapshot.RootElement.GetProperty("agent").TryGetProperty("prompt_manifest", out _));
        Assert.Equal(
            "b070628a2e7247136c5c02d9f4afe33c970eaf05462bf38e94b6f6922b6c7afc",
            built.SnapshotHash);
    }

    /// <summary>Pinned counterpart: the key appears only under <c>agent</c>, with the exact values.</summary>
    [Fact]
    public void Snapshot_WithPromptManifestPin_EmbedsRevisionAndSha256_UnderAgentOnly()
    {
        var pinnedSha256 = new string('7', 64);
        var (agent, workflow) = MinimalAgentAndWorkflow(
            Guid.Parse("53111111-1111-4111-8111-111111111111"),
            "pinned-agent",
            promptManifestRevision: 5,
            promptManifestSha256: pinnedSha256);

        var built = AgentRunSnapshotBuilder.Build(
            Guid.Parse("53222222-2222-4222-8222-222222222222"),
            "tenant-a", "admin-a", "ADMIN", Array.Empty<string>(),
            agent, workflow, Array.Empty<SkillSnapshotSource>());

        using var snapshot = JsonDocument.Parse(built.StoredSnapshot);
        var pin = snapshot.RootElement.GetProperty("agent").GetProperty("prompt_manifest");
        Assert.Equal(5, pin.GetProperty("revision").GetInt32());
        Assert.Equal(pinnedSha256, pin.GetProperty("sha256").GetString());
        // The pin lives only under `agent`; nothing else in the snapshot shape moves.
        Assert.False(snapshot.RootElement.TryGetProperty("prompt_manifest", out _));
    }

    /// <summary>
    /// 三個 execution_kind 裡只有 orchestrator-verifier 從未進過 Build;而 cap 夾預算和 P1 manifest pin
    /// 都改寫同一塊 agentNode/runtime_limits,兩者同時出現時必須互不吃掉對方。cap 取 on-point 上限,
    /// 且高於 Agent 自身預算 —— 夾出來的必須是伺服器預設值,不是 cap。
    /// </summary>
    [Fact]
    public void VerifierChildSnapshot_CarriesTokenCapAndPromptManifestPinTogether()
    {
        var pinnedSha256 = new string('9', 64);
        var (agent, workflow) = MinimalAgentAndWorkflow(
            Guid.Parse("54111111-1111-4111-8111-111111111111"),
            "pinned-verifier",
            promptManifestRevision: 7,
            promptManifestSha256: pinnedSha256);

        var built = AgentRunSnapshotBuilder.Build(
            Guid.Parse("54222222-2222-4222-8222-222222222222"),
            "tenant-a", "admin-a", "ADMIN", Array.Empty<string>(),
            agent, workflow, Array.Empty<SkillSnapshotSource>(),
            "orchestrator-verifier", AgentExecutionContract.MaxOrchestratorTokenCap,
            new OrchestratorChildSnapshotProvenance(
                Guid.Parse("55333333-3333-4333-8333-333333333333"), "verify-task", 1));

        using var snapshot = JsonDocument.Parse(built.StoredSnapshot);
        Assert.Equal(
            "orchestrator-verifier",
            snapshot.RootElement.GetProperty("execution_kind").GetString());
        Assert.Equal(
            AgentExecutionContract.MaxOrchestratorTokenCap,
            snapshot.RootElement.GetProperty("orchestrator_token_cap").GetInt32());
        Assert.Equal(1, snapshot.RootElement.GetProperty("orchestrator_attempt").GetInt32());
        var agentNode = snapshot.RootElement.GetProperty("agent");
        Assert.Equal(
            AgentExecutionContract.DefaultTokenBudget,
            agentNode.GetProperty("runtime_limits").GetProperty("token_budget").GetInt32());
        var pin = agentNode.GetProperty("prompt_manifest");
        Assert.Equal(7, pin.GetProperty("revision").GetInt32());
        Assert.Equal(pinnedSha256, pin.GetProperty("sha256").GetString());
        Assert.Equal(
            built.SnapshotHash,
            SkillHash.Sha256(Encoding.UTF8.GetBytes(built.StoredSnapshot)));
    }

    private static SkillSnapshotSource SkillPin(
        string name,
        int revision = 1,
        string kind = "flow",
        string description = "bounded description")
    {
        var immutableDefinition =
            $"name: {name}\ndescription: bounded description\nflow: []\n";
        return new SkillSnapshotSource(
            Guid.NewGuid(),
            name,
            description,
            revision,
            kind,
            immutableDefinition,
            SkillHash.Sha256(immutableDefinition),
            null);
    }

    private static WorkflowSnapshotSource WorkflowPin(
        int revision = AgentDefaults.RuntimeWorkflowRevision,
        string compilerContractVersion = "1")
    {
        var definition = AgentRunSnapshotBuilder.CanonicalizeJson(
            AgentDefaults.RuntimeWorkflowDefinition);
        return new WorkflowSnapshotSource(
            Guid.Parse(AgentDefaults.RuntimeWorkflowId),
            revision,
            1,
            definition,
            SkillHash.Sha256(definition),
            compilerContractVersion);
    }

    private static IReadOnlyList<AgentValidationError> ValidateExecutionContractWith(
        IReadOnlyList<SkillSnapshotSource> skills,
        string agentName = "bounded-agent",
        WorkflowSnapshotSource? workflow = null)
    {
        var (agent, seededWorkflow) = MinimalAgentAndWorkflow(
            Guid.Parse("41111111-1111-4111-8111-111111111111"), agentName);
        return AgentRunSnapshotBuilder.ValidateExecutionContract(
            agent,
            workflow ?? seededWorkflow,
            skills,
            "tenant-a",
            "admin-a",
            "ADMIN");
    }

    private static (PublishedAgentSnapshotSource Agent, WorkflowSnapshotSource Workflow) MinimalAgentAndWorkflow(
        Guid agentId,
        string name,
        int? promptManifestRevision = null,
        string? promptManifestSha256 = null)
    {
        var definition = DefinitionWithPadding(
            string.Empty, Array.Empty<AgentRevisionSkillInfo>());
        var agent = new PublishedAgentSnapshotSource(
            agentId,
            name,
            1,
            definition,
            SkillHash.Sha256(definition),
            Guid.Parse(AgentDefaults.RuntimeWorkflowId),
            AgentDefaults.RuntimeWorkflowRevision,
            Array.Empty<AgentRevisionSkillInfo>(),
            promptManifestRevision,
            promptManifestSha256);
        return (agent, WorkflowPin());
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

    /// <summary>
    /// backend/AGENTS.md:「Legacy rows missing canonical bytes fail closed;JSONB is never a fallback」。
    /// 這七條分支是那句話在程式碼裡的唯一防線 — 若有人把缺 bytes 改成回落讀 jsonb,今天全部仍會綠。
    /// </summary>
    [Fact]
    public void AuthoritativeDefinition_FailsClosedOnEveryTamperedForm()
    {
        var valid = DefinitionWithPadding(
            string.Empty, Array.Empty<AgentRevisionSkillInfo>());
        var validBytes = Encoding.UTF8.GetBytes(valid);
        var validSha = SkillHash.Sha256(validBytes);
        Assert.Equal(valid, Read(validBytes, validSha)); // happy path 先自證

        Assert.Contains(
            "lacks authoritative canonical definition bytes",
            Rejected(null, validSha));
        var overCap = Encoding.UTF8.GetBytes(
            DefinitionWithExactBytes(
                AgentExecutionContract.MaxCanonicalDefinitionBytes + 1));
        Assert.Contains(
            "exceeds its byte limit",
            Rejected(overCap, SkillHash.Sha256(overCap)));
        Assert.Contains(
            "hash mismatch",
            Rejected(validBytes, new string('0', 64)));

        var withBom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(validBytes).ToArray();
        Assert.Contains(
            "must not contain a UTF-8 BOM",
            Rejected(withBom, SkillHash.Sha256(withBom)));

        var badShape = Encoding.UTF8.GetBytes("""{"system_prompt":"only"}""");
        Assert.Contains(
            "invalid root schema",
            Rejected(badShape, SkillHash.Sha256(badShape)));

        var reordered = Encoding.UTF8.GetBytes(new JsonObject(
            JsonNode.Parse(valid)!.AsObject()
                .Reverse()
                .Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone()))).ToJsonString());
        Assert.Contains(
            "are not canonical JSON",
            Rejected(reordered, SkillHash.Sha256(reordered)));

        var invalidUtf8 = validBytes.Concat(new byte[] { 0xFF }).ToArray();
        Assert.Contains(
            "not strict UTF-8 JSON",
            Rejected(invalidUtf8, SkillHash.Sha256(invalidUtf8)));

        static string Read(byte[]? bytes, string sha)
            => AgentCanonicalizer.ReadAuthoritativeDefinition(bytes, sha, "Agent draft");

        static string Rejected(byte[]? bytes, string sha)
            => Assert.Throws<InvalidOperationException>(() => Read(bytes, sha)).Message;
    }

    /// <summary>
    /// 上一條的 badShape 樣本一次缺 10 個 key,而 `HasDefinitionShape` 是 && 串接 —— 只證明得到
    /// 第一個短路點。十一個必要 key 必須逐一拿掉都拒收,和 snapshot 側的六個 key 同一寫法。
    /// </summary>
    [Fact]
    public void AuthoritativeDefinition_FailsClosedOnEachMissingRequiredKey()
    {
        var valid = DefinitionWithPadding(
            string.Empty, Array.Empty<AgentRevisionSkillInfo>());

        foreach (var required in new[]
                 {
                     "system_prompt", "execution_roles", "capabilities", "output_contract",
                     "audience", "allowed_tools", "skill_bindings", "knowledge_sources",
                     "business_rules", "runtime_limits", "runtime_workflow",
                 })
        {
            var stripped = JsonNode.Parse(valid)!.AsObject();
            Assert.True(stripped.Remove(required));
            var bytes = Encoding.UTF8.GetBytes(
                AgentCanonicalizer.CanonicalizeDefinition(stripped.ToJsonString()));
            Assert.Contains(
                "invalid root schema",
                Assert.Throws<InvalidOperationException>(
                        () => AgentCanonicalizer.ReadAuthoritativeDefinition(
                            bytes, SkillHash.Sha256(bytes), "Agent draft"))
                    .Message);
        }
    }

    /// <summary>
    /// 同理的 run snapshot 側:`HasSnapshotShape` 的六個必要 key 逐一被拿掉都必須拒收,
    /// 否則 Workflow 可能拿到缺 caller/mode 的快照仍照跑。
    /// </summary>
    [Fact]
    public void AuthoritativeSnapshot_FailsClosedOnTamperedBytesAndMissingRequiredKeys()
    {
        var valid = CanonicalSnapshotWithExactBytes(512);
        var validBytes = Encoding.UTF8.GetBytes(valid);
        var validSha = SkillHash.Sha256(validBytes);
        Assert.Equal(valid, AgentRunSnapshotBuilder.ReadAuthoritativeSnapshot(validBytes, validSha));

        Assert.Contains(
            "lacks authoritative canonical snapshot bytes",
            Rejected(null, validSha));
        Assert.Contains("hash mismatch", Rejected(validBytes, new string('0', 64)));

        var withBom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(validBytes).ToArray();
        Assert.Contains(
            "must not contain a UTF-8 BOM",
            Rejected(withBom, SkillHash.Sha256(withBom)));

        var reordered = Encoding.UTF8.GetBytes(new JsonObject(
            JsonNode.Parse(valid)!.AsObject()
                .Reverse()
                .Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone()))).ToJsonString());
        Assert.Contains("are not canonical JSON", Rejected(reordered, SkillHash.Sha256(reordered)));

        foreach (var required in new[] { "run_id", "agent", "workflow", "skills", "caller", "mode" })
        {
            var stripped = JsonNode.Parse(valid)!.AsObject();
            stripped.Remove(required);
            var bytes = Encoding.UTF8.GetBytes(
                AgentRunSnapshotBuilder.CanonicalizeJson(stripped.ToJsonString()));
            Assert.Contains(
                "invalid root schema",
                Rejected(bytes, SkillHash.Sha256(bytes)));
        }

        var invalidUtf8 = validBytes.Concat(new byte[] { 0xFF }).ToArray();
        Assert.Contains(
            "not strict UTF-8 JSON",
            Rejected(invalidUtf8, SkillHash.Sha256(invalidUtf8)));

        static string Rejected(byte[]? bytes, string sha)
            => Assert.Throws<InvalidOperationException>(
                () => AgentRunSnapshotBuilder.ReadAuthoritativeSnapshot(bytes, sha)).Message;
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
        IReadOnlyList<AgentRevisionSkillInfo> bindings,
        AgentRuntimeLimits? runtimeLimits = null)
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
            RuntimeLimits: runtimeLimits ?? new AgentRuntimeLimits(),
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
