using System.Text.Json;
using System.Text.Json.Nodes;
using Backend.Api.Agents;
using Backend.Api.Data.InMemory;
using Backend.Api.Skills;

namespace Backend.Api.Tests;

/// <summary>
/// 升格後 InMemory 儲存庫(DB_PROVIDER=inmemory / Lite 模式)的單元驗收。
/// 打生產類別本身,釘住「與 DbBootstrap 一致的種子 + BCrypt」、租戶隔離、真 cosine、
/// 「一租戶至多一 active」這四條升格時最容易改壞的不變量。
/// </summary>
public sealed class InMemoryRepositoriesTests
{
    // ---- Auth:種子帳號與 DbBootstrap 一致,且密碼走 BCrypt.Verify(不是 hash 字面值比對) ----

    [Theory]
    [InlineData("admin-a", "ADMIN", "demo-a")]
    [InlineData("user-a", "USER", "demo-a")]
    [InlineData("user-b", "USER", "demo-b")]
    public async Task Auth_SeedUser_MatchesRoleTenant_AndPasswordVerifies(string username, string role, string tenant)
    {
        var repo = new InMemoryAuthRepository();

        var user = await repo.FindUserByUsernameAsync(username, default);

        Assert.NotNull(user);
        Assert.Equal(role, user!.Role);
        Assert.Equal(tenant, user.TenantCode);
        // 登入語義:BCrypt.Verify 通過(hash 是 BCrypt.HashPassword("password123"),與 DbBootstrap 同一套邏輯)。
        Assert.True(BCrypt.Net.BCrypt.Verify("password123", user.PasswordHash));
        Assert.False(BCrypt.Net.BCrypt.Verify("wrong-password", user.PasswordHash));
    }

    [Fact]
    public async Task Auth_SeedTenant_HasInviteCode()
    {
        var repo = new InMemoryAuthRepository();

        var tenant = await repo.FindTenantByCodeAsync("demo-a", default);

        Assert.NotNull(tenant);
        Assert.Equal("demo-a-invite", tenant!.InviteCode);
    }

    [Fact]
    public async Task Auth_UnknownUser_ReturnsNull()
    {
        var repo = new InMemoryAuthRepository();
        Assert.Null(await repo.FindUserByUsernameAsync("nobody", default));
    }

    // ---- Conversation:依 (tenant, user) 隔離 — 別的租戶/使用者看不到 ----

    [Fact]
    public async Task Conversation_ListDesc_IsScopedByTenantAndUser()
    {
        var repo = new InMemoryConversationRepository();
        await repo.AddAsync("demo-a", "user-a", "Q1", "A1", default);
        await repo.AddAsync("demo-b", "user-b", "Q2", "A2", default);
        await repo.AddAsync("demo-a", "other-user", "Q3", "A3", default);
        // 只變租戶、user id 相同:少了這列,掉光 tenant 過濾器(只剩 user 過濾)也測得過。
        await repo.AddAsync("demo-b", "user-a", "Q4", "A4", default);

        var mine = await repo.ListDescAsync("demo-a", "user-a", default);

        var item = Assert.Single(mine);
        Assert.Equal("A1", item.Reply);
    }

    [Fact]
    public async Task Conversation_ListDesc_NewestFirst_TieBrokenByIdDesc()
    {
        var repo = new InMemoryConversationRepository();
        var first = await repo.AddAsync("demo-a", "user-a", "Q1", "A1", default);
        var second = await repo.AddAsync("demo-a", "user-a", "Q2", "A2", default);

        var list = await repo.ListDescAsync("demo-a", "user-a", default);

        Assert.Equal(new[] { second.Id, first.Id }, list.Select(i => i.Id).ToArray());
    }

    // ---- Rag:真 cosine — 相同向量 → 1.0,正交向量 → 0.0,依相似度遞減排序,跨租戶不可見 ----

    private static float[] Vec(params float[] v) => v;

    private static async Task SeedReadyAsync(
        InMemoryRagRepository repo, string tenant, string docId, string title, (string, float[])[] chunks)
    {
        await repo.InsertProcessingDocumentAsync(docId, tenant, title, default);
        await repo.CompleteDocumentAsync(
            docId, tenant, chunks.Select(c => c.Item1).ToList(), chunks.Select(c => c.Item2).ToList(), default);
    }

    [Fact]
    public async Task Rag_Search_ComputesCosine_IdenticalIsOne_OrthogonalIsZero()
    {
        var repo = new InMemoryRagRepository();
        var docId = Guid.NewGuid().ToString();
        await SeedReadyAsync(repo, "demo-a", docId, "doc", new[]
        {
            ("same", Vec(1f, 0f, 0f)),         // 與 query 相同 → cosine 1.0
            ("orthogonal", Vec(0f, 1f, 0f)),   // 與 query 正交 → cosine 0.0
        });

        var results = await repo.SearchAsync("demo-a", Vec(1f, 0f, 0f), 10, default);

        Assert.Equal(2, results.Count);
        // 手算驗證:相同向量 cosine=1、正交 cosine=0,且高分在前。
        Assert.Equal("same", results[0].Content);
        Assert.Equal(1.0, results[0].Score, 5);
        Assert.Equal("orthogonal", results[1].Content);
        Assert.Equal(0.0, results[1].Score, 5);
    }

    [Fact]
    public async Task Rag_Search_ZeroMagnitudeChunk_ScoresZero_NotNaN()
    {
        var repo = new InMemoryRagRepository();
        await SeedReadyAsync(repo, "demo-a", Guid.NewGuid().ToString(), "doc", new[]
        {
            ("zero", Vec(0f, 0f)),   // 退化向量:|b|=0 → 走特例回 0(不是 0/0 = NaN)
            ("same", Vec(1f, 0f)),
        });

        var results = await repo.SearchAsync("demo-a", Vec(1f, 0f), 10, default);

        Assert.Equal(new[] { "same", "zero" }, results.Select(r => r.Content).ToArray());
        Assert.Equal(0.0, results[1].Score, 5);
    }

    [Fact]
    public async Task Rag_Search_OppositeVector_ScoresMinusOne_AndSortsLast()
    {
        var repo = new InMemoryRagRepository();
        await SeedReadyAsync(repo, "demo-a", Guid.NewGuid().ToString(), "doc", new[]
        {
            ("opposite", Vec(-1f, 0f)),      // cosine 值域下界 -1 — 排序最後
            ("orthogonal", Vec(0f, 1f)),
            ("same", Vec(1f, 0f)),
        });

        var results = await repo.SearchAsync("demo-a", Vec(1f, 0f), 10, default);

        Assert.Equal(
            new[] { "same", "orthogonal", "opposite" }, results.Select(r => r.Content).ToArray());
        Assert.Equal(-1.0, results[2].Score, 5);
    }

    [Fact]
    public async Task Rag_Search_ExcludesOtherTenants()
    {
        var repo = new InMemoryRagRepository();
        await SeedReadyAsync(repo, "demo-a", Guid.NewGuid().ToString(), "a", new[] { ("A-secret", Vec(1f, 0f)) });
        await SeedReadyAsync(repo, "demo-b", Guid.NewGuid().ToString(), "b", new[] { ("B-note", Vec(1f, 0f)) });

        var results = await repo.SearchAsync("demo-b", Vec(1f, 0f), 10, default);

        Assert.All(results, r => Assert.StartsWith("B-", r.Content));
    }

    [Fact]
    public async Task Rag_ScopedSearch_EnforcesTenantAndExactDocumentIds()
    {
        var repo = new InMemoryRagRepository();
        var allowed = Guid.NewGuid();
        var denied = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        await SeedReadyAsync(
            repo,
            "demo-a",
            allowed.ToString("D"),
            "allowed",
            new[] { ("allowed", Vec(1f, 0f)) });
        await SeedReadyAsync(
            repo,
            "demo-a",
            denied.ToString("D"),
            "denied",
            new[] { ("denied", Vec(1f, 0f)) });
        await SeedReadyAsync(
            repo,
            "demo-b",
            otherTenant.ToString("D"),
            "other",
            new[] { ("other", Vec(1f, 0f)) });

        var scoped = await repo.SearchScopedAsync(
            "demo-a",
            Vec(1f, 0f),
            10,
            new[] { allowed, otherTenant },
            default);
        var empty = await repo.SearchScopedAsync(
            "demo-a",
            Vec(1f, 0f),
            10,
            Array.Empty<Guid>(),
            default);

        Assert.All(scoped, item => Assert.Equal(allowed.ToString("D"), item.DocumentId));
        Assert.NotEmpty(scoped);
        Assert.Empty(empty);
    }

    [Fact]
    public async Task Rag_Search_DimensionMismatch_SkipsChunk_DoesNotThrow()
    {
        var repo = new InMemoryRagRepository();
        var docId = Guid.NewGuid().ToString();
        await SeedReadyAsync(repo, "demo-a", docId, "doc", new[]
        {
            ("wrong-dim", Vec(1f, 0f, 0f)), // query 只有 2 維 → 維度不匹配 → 略過
            ("ok", Vec(1f, 0f)),
        });

        var results = await repo.SearchAsync("demo-a", Vec(1f, 0f), 10, default);

        var hit = Assert.Single(results);
        Assert.Equal("ok", hit.Content);
    }

    // ---- ConfigurationSet:一租戶至多一 active(activate 先關其餘再開目標) ----

    [Fact]
    public async Task ConfigurationSet_Activate_LeavesExactlyOneActivePerTenant()
    {
        var repo = new InMemoryConfigurationSetRepository();
        var empty = new Dictionary<string, object>();
        var a = (await repo.CreateAsync("demo-a", "set-a", empty, "admin-a", default))!;
        var b = (await repo.CreateAsync("demo-a", "set-b", empty, "admin-a", default))!;

        await repo.ActivateAsync("demo-a", a.Id, default);
        await repo.ActivateAsync("demo-a", b.Id, default);

        // 啟用 b 後只剩 b 為 active(a 被自動關閉)。
        var active = await repo.GetActiveAsync("demo-a", default);
        Assert.NotNull(active);
        Assert.Equal(b.Id, active!.Id);

        var all = await repo.ListAsync("demo-a", default);
        Assert.Single(all, s => s.IsActive);

        // active 是逐租戶的:demo-a 的啟用不會外溢到 demo-b。
        Assert.Null(await repo.GetActiveAsync("demo-b", default));
    }

    // 鏡射 ConfigurationSetRepositoryTests 標為「回歸(HIGH)」的 DB 版:Lite 模式跑的是這支實作,
    // activate 不存在的 id 必須完全不動既有 active(不能靜默清空)。
    [Fact]
    public async Task ConfigurationSet_Activate_NonexistentId_LeavesExistingActiveUntouched()
    {
        var repo = new InMemoryConfigurationSetRepository();
        var empty = new Dictionary<string, object>();
        var active = (await repo.CreateAsync("demo-a", "set-active", empty, "admin-a", default))!;
        await repo.ActivateAsync("demo-a", active.Id, default);

        Assert.Null(await repo.ActivateAsync("demo-a", Guid.NewGuid(), default));

        Assert.Equal(active.Id, (await repo.GetActiveAsync("demo-a", default))!.Id);
    }

    // 同一個 OR 守衛的另一個無效等價類:id 存在,但屬於別的租戶 — 一樣回 null,
    // 且既不得啟用別租戶的 set,也不得動到自己租戶既有的 active。
    [Fact]
    public async Task ConfigurationSet_Activate_OtherTenantsId_ReturnsNull_AndTouchesNothing()
    {
        var repo = new InMemoryConfigurationSetRepository();
        var empty = new Dictionary<string, object>();
        var mine = (await repo.CreateAsync("demo-a", "set-mine", empty, "admin-a", default))!;
        var theirs = (await repo.CreateAsync("demo-b", "set-theirs", empty, "admin-b", default))!;
        await repo.ActivateAsync("demo-a", mine.Id, default);

        Assert.Null(await repo.ActivateAsync("demo-a", theirs.Id, default));

        Assert.Equal(mine.Id, (await repo.GetActiveAsync("demo-a", default))!.Id);
        Assert.False((await repo.GetAsync("demo-b", theirs.Id, default))!.IsActive);
        Assert.Null(await repo.GetActiveAsync("demo-b", default));
    }

    // ---- Agent:repo 層對「已標 validated 但定義/內容不合法」的縱深防禦(不走 HTTP)----

    private static string AgentDefinition(
        string systemPrompt = "你是研究助手", int timeoutSeconds = 60)
        => AgentCanonicalizer.Canonicalize(new AgentUpsert(
            Slug: null,
            Name: null,
            Description: null,
            SystemPrompt: systemPrompt,
            ExecutionRoles: new[] { "worker" },
            Capabilities: null,
            OutputContract: null,
            Audience: new[] { "role:ADMIN" },
            AllowedTools: null,
            SkillBindings: null,
            KnowledgeSources: null,
            BusinessRules: null,
            RuntimeLimits: new AgentRuntimeLimits(TimeoutSeconds: timeoutSeconds),
            RuntimeWorkflow: new AgentWorkflowRef(
                AgentDefaults.RuntimeWorkflowId, AgentDefaults.RuntimeWorkflowRevision)));

    private static async Task<Agent> ValidatedAgentAsync(
        InMemoryAgentRepository repo, string slug, string definition)
    {
        var agent = await repo.CreateAsync(
            "demo-a", slug, "名稱", "說明", definition, SkillHash.Sha256(definition), "admin-a", default);
        Assert.NotNull(agent);
        Assert.True(await repo.MarkValidatedAsync(
            "demo-a", agent!.Id, agent.DraftVersion, definition, SkillHash.Sha256(definition), default));
        return agent;
    }

    [Fact]
    public async Task RepositoryPublishAndRestore_RecheckExecutionSnapshotContract()
    {
        var repo = new InMemoryAgentRepository(new InMemorySkillRepository());
        var invalidDefinition = AgentDefinition(
            systemPrompt: new string('p', AgentExecutionContract.MaxSystemPromptLength + 1),
            timeoutSeconds: -1);
        var invalid = await ValidatedAgentAsync(repo, "invalid-contract", invalidDefinition);

        var rejectedPublish = await repo.PublishAsync(
            "demo-a", invalid.Id, invalid.DraftVersion,
            invalidDefinition, SkillHash.Sha256(invalidDefinition), "admin-a", default);

        Assert.Equal(AgentWriteStatus.InvalidReference, rejectedPublish.Status);
        Assert.Contains(rejectedPublish.Errors!, error => error.Field == "system_prompt");
        Assert.Contains(
            rejectedPublish.Errors!, error => error.Field == "runtime_limits.timeout_seconds");

        var validDefinition = AgentDefinition();
        var valid = await ValidatedAgentAsync(repo, "valid-contract", validDefinition);
        Assert.Equal(
            AgentWriteStatus.Success,
            (await repo.PublishAsync(
                "demo-a", valid.Id, valid.DraftVersion,
                validDefinition, SkillHash.Sha256(validDefinition), "admin-a", default)).Status);

        var rejectedRestore = await repo.RestoreAsync(
            "demo-a", valid.Id, 1,
            invalidDefinition, SkillHash.Sha256(invalidDefinition), "admin-a", default);
        Assert.Equal(AgentWriteStatus.InvalidReference, rejectedRestore.Status);
        Assert.Single(await repo.ListRevisionsAsync("demo-a", valid.Id, default));
    }

    /// <summary>
    /// 契約上限的 on-point / off-point:剛好等於上限必須發得出去(擋住 &gt; 誤寫成 &gt;=),
    /// 上限 +1 必須被擋(既有測試只測到 timeout 的下界 -1,沒測過上界)。
    /// </summary>
    [Fact]
    public async Task Publish_AcceptsExactContractMaximums_RejectsOneOverTimeout()
    {
        var repo = new InMemoryAgentRepository(new InMemorySkillRepository());
        var atMax = AgentDefinition(
            systemPrompt: new string('p', AgentExecutionContract.MaxSystemPromptLength),
            timeoutSeconds: AgentExecutionContract.MaxTimeoutSeconds);
        var boundary = await ValidatedAgentAsync(repo, "contract-at-max", atMax);
        Assert.Equal(
            AgentWriteStatus.Success,
            (await repo.PublishAsync(
                "demo-a", boundary.Id, boundary.DraftVersion,
                atMax, SkillHash.Sha256(atMax), "admin-a", default)).Status);

        var overMax = AgentDefinition(
            timeoutSeconds: AgentExecutionContract.MaxTimeoutSeconds + 1);
        var rejectedAgent = await ValidatedAgentAsync(repo, "contract-over-max", overMax);

        var rejected = await repo.PublishAsync(
            "demo-a", rejectedAgent.Id, rejectedAgent.DraftVersion,
            overMax, SkillHash.Sha256(overMax), "admin-a", default);

        Assert.Equal(AgentWriteStatus.InvalidReference, rejected.Status);
        Assert.Contains(
            rejected.Errors!, error => error.Field == "runtime_limits.timeout_seconds");
        Assert.Empty(await repo.ListRevisionsAsync("demo-a", rejectedAgent.Id, default));
    }

    /// <summary>
    /// publish 的防篡改守衛:鎖定 draft 後,唯一允許的差異是 Workflow 回寫的 business_rules。
    /// 其他任何欄位漂移都是 TOCTOU,必須以 VersionConflict 擋下而非靜默發布。
    /// </summary>
    [Fact]
    public async Task Publish_RejectsDefinitionDriftBeyondBusinessRuleCanonicalization()
    {
        var repo = new InMemoryAgentRepository(new InMemorySkillRepository());
        var draft = AgentDefinition();

        var ruleOnlyAgent = await ValidatedAgentAsync(repo, "drift-rules-only", draft);
        using var canonicalRules = JsonDocument.Parse(
            """{"version":1,"rules":[{"id":"r","onUnknown":[{"action":"deny"}]}]}""");
        var ruleOnly = AgentCanonicalizer.WithBusinessRules(draft, canonicalRules.RootElement);
        Assert.NotEqual(draft, ruleOnly);
        Assert.Equal(
            AgentWriteStatus.Success,
            (await repo.PublishAsync(
                "demo-a", ruleOnlyAgent.Id, ruleOnlyAgent.DraftVersion,
                ruleOnly, SkillHash.Sha256(ruleOnly), "admin-a", default)).Status);

        var tamperedAgent = await ValidatedAgentAsync(repo, "drift-prompt", draft);
        var tamperedNode = JsonNode.Parse(draft)!.AsObject();
        tamperedNode["system_prompt"] = "被竄改的 prompt";
        var tampered = AgentCanonicalizer.CanonicalizeDefinition(tamperedNode.ToJsonString());
        Assert.Equal(
            AgentWriteStatus.VersionConflict,
            (await repo.PublishAsync(
                "demo-a", tamperedAgent.Id, tamperedAgent.DraftVersion,
                tampered, SkillHash.Sha256(tampered), "admin-a", default)).Status);
        Assert.Empty(await repo.ListRevisionsAsync("demo-a", tamperedAgent.Id, default));

        // 守衛本身:候選不是 JSON、或根本沒有 business_rules 欄位 → 一律 false(fail closed)。
        Assert.False(AgentCanonicalizer.IsBusinessRuleOnlyCanonicalization(draft, "not json"));
        Assert.False(AgentCanonicalizer.IsBusinessRuleOnlyCanonicalization(draft, "{}"));
    }
}
