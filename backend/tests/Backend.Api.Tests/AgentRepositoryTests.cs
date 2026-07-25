using Backend.Api.Agents;
using Backend.Api.AgentRuns;
using Backend.Api.Skills;
using Backend.Api.Workflows;
using Dapper;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Backend.Api.Tests;

/// <summary>
/// AgentRepository 的**真 PostgreSQL**驗收(手寫 fake 背書不了的 SQL 事實:publish 由 draft_definition
/// jsonb 抽欄落 revision、skill binding 於交易內 join 解析 skill_id 並 pin revision、restore 用
/// INSERT...SELECT 原封複製快照與 bindings、軟停用保留 revision、跨租戶不可見)。與 InMemoryAgentRepository
/// 維持行為 parity(A-DATA-07);API 層(InMemory 路徑)由 AgentsApiTests 背書。
/// 共用 PostgresFixture("Postgres" collection 序列化);appdb 不可達則 SkipIfUnavailable(不假綠)。
/// </summary>
[Collection("Postgres")]
public sealed class AgentRepositoryTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;

    public AgentRepositoryTests(PostgresFixture fx) => _fx = fx;

    private AgentRepository Repo => new(_fx.DataSource!);

    public async Task InitializeAsync() => await CleanupAsync();

    public async Task DisposeAsync() => await CleanupAsync();

    private async Task CleanupAsync()
    {
        if (!_fx.Available)
        {
            return;
        }

        await using var conn = await _fx.DataSource!.OpenConnectionAsync();
        await conn.ExecuteAsync(
            "DELETE FROM agent_run_command WHERE tenant_id LIKE 'agentrepo-%';"
            + " DELETE FROM agent_run_event WHERE run_id IN (SELECT id FROM agent_run WHERE tenant_id LIKE 'agentrepo-%');"
            + " DELETE FROM agent_run_skill WHERE run_id IN (SELECT id FROM agent_run WHERE tenant_id LIKE 'agentrepo-%');"
            + " DELETE FROM agent_run WHERE tenant_id LIKE 'agentrepo-%';"
            + " DELETE FROM agent_revision_skill WHERE agent_id IN (SELECT id FROM agent WHERE tenant_id LIKE 'agentrepo-%');"
            + " DELETE FROM agent_revision WHERE agent_id IN (SELECT id FROM agent WHERE tenant_id LIKE 'agentrepo-%');"
            + " DELETE FROM agent WHERE tenant_id LIKE 'agentrepo-%';"
            + " DELETE FROM skill_revision WHERE skill_id IN (SELECT id FROM skill WHERE tenant_id LIKE 'agentrepo-%');"
            + " DELETE FROM skill WHERE tenant_id LIKE 'agentrepo-%';");
    }

    private static string Def(
        string prompt = "你是研究助手", string[]? roles = null,
        string[]? bindings = null, string[]? allowedTools = null,
        string? workflowId = null, string? businessRules = null)
    {
        var req = new AgentUpsert(
            Slug: null, Name: null, Description: null,
            SystemPrompt: prompt,
            ExecutionRoles: roles ?? new[] { "worker" },
            Capabilities: null,
            OutputContract: null,
            Audience: null,
            AllowedTools: allowedTools,
            SkillBindings: bindings?.Select(b => new AgentSkillBinding(b, "latest")).ToList(),
            KnowledgeSources: null,
            BusinessRules: businessRules is null
                ? null
                : JsonDocument.Parse(businessRules).RootElement.Clone(),
            RuntimeLimits: null,
            RuntimeWorkflow: new AgentWorkflowRef(
                workflowId ?? AgentDefaults.RuntimeWorkflowId, AgentDefaults.RuntimeWorkflowRevision));
        return AgentCanonicalizer.Canonicalize(req);
    }

    private static string Sha(string def) => SkillHash.Sha256(def);

    private async Task InsertSkillAsync(string tenant, string name, int currentRevision)
    {
        await using var conn = await _fx.DataSource!.OpenConnectionAsync();
        await conn.ExecuteAsync(
            "WITH inserted AS ("
            + " INSERT INTO skill (tenant_id, name, description, current_revision)"
            + " VALUES (@tenant, @name, 'binding', @rev)"
            + " RETURNING id)"
            + " INSERT INTO skill_revision"
            + " (skill_id, revision, definition, definition_sha256, created_by, kind)"
            + " SELECT id, @rev, 'test', @sha, 'test', 'flow' FROM inserted",
            new
            {
                tenant,
                name,
                rev = currentRevision,
                sha = SkillHash.Sha256("test"),
            });
    }

    private async Task<Agent> CreateValidatedAsync(string tenant, string slug, string def)
    {
        var agent = await Repo.CreateAsync(tenant, slug, "研究助手", "說明", def, Sha(def), "author", default);
        Assert.NotNull(agent);
        Assert.True(await Repo.MarkValidatedAsync(
            tenant, agent!.Id, agent.DraftVersion, def, Sha(def), default));
        return agent;
    }

    // ---- 建立 / 取回 / 重複 slug ----

    [SkippableFact]
    public async Task Create_And_Get_RoundTrips()
    {
        _fx.SkipIfUnavailable();
        const string tenant = "agentrepo-create";
        var def = Def();

        var created = await Repo.CreateAsync(tenant, "slug-1", "名稱", "說明", def, Sha(def), "author", default);

        Assert.NotNull(created);
        Assert.Equal(1, created!.DraftVersion);
        Assert.Null(created.DraftValidatedVersion);
        Assert.Null(created.PublishedRevision);

        var fetched = await Repo.GetAsync(tenant, created.Id, default);
        Assert.Equal("slug-1", fetched!.Slug);
        Assert.Equal(Sha(def), fetched.DraftDefinitionSha256);
    }

    [SkippableFact]
    public async Task MarkValidated_FullCanonicalHash_IsStableAcrossJsonbRoundTrip()
    {
        _fx.SkipIfUnavailable();
        const string tenant = "agentrepo-canonical-roundtrip";
        const string draftRules =
            """{"version":1,"rules":[{"when":{"value":5000,"op":"gt","fact":"action.amount"},"then":[{"action":"deny"}],"id":"stable-rule"}]}""";
        const string workflowRules =
            """{"rules":[{"when":{"op":"gt","fact":"action.amount","value":5000},"then":[{"action":"deny"}],"priority":100,"onUnknown":[{"action":"deny"}],"name":"","id":"stable-rule","enabled":true}],"version":1}""";
        var definition = Def(businessRules: draftRules);
        var created = await Repo.CreateAsync(
            tenant,
            "canonical-roundtrip",
            "名稱",
            "說明",
            definition,
            Sha(definition),
            "author",
            default);
        Assert.NotNull(created);

        // Dapper Get 會經過 jsonb::text；同一份 Workflow canonicalRuleSet 不可因此得到不同 bytes/hash。
        var dbRoundTripped = await Repo.GetAsync(tenant, created!.Id, default);
        using var canonicalDoc = JsonDocument.Parse(workflowRules);
        var expected = AgentCanonicalizer.WithBusinessRules(
            definition,
            canonicalDoc.RootElement);
        var fromDapper = AgentCanonicalizer.WithBusinessRules(
            dbRoundTripped!.DraftDefinition,
            canonicalDoc.RootElement);
        Assert.Equal(expected, fromDapper);
        Assert.True(await Repo.MarkValidatedAsync(
            tenant,
            created.Id,
            created.DraftVersion,
            fromDapper,
            Sha(fromDapper),
            default));

        var stored = await Repo.GetAsync(tenant, created.Id, default);
        Assert.Equal(Sha(expected), stored!.DraftDefinitionSha256);
        Assert.Equal(expected, stored.DraftDefinition);

        var publish = await Repo.PublishAsync(
            tenant, created.Id, created.DraftVersion, expected, Sha(expected), "publisher", default);
        Assert.Equal(AgentWriteStatus.Success, publish.Status);
        var revision = Assert.Single(await Repo.ListRevisionsAsync(tenant, created.Id, default));
        Assert.Equal(Sha(expected), revision.DefinitionSha256);
    }

    [SkippableFact]
    public async Task Create_DuplicateSlug_ReturnsNull_ButOtherTenantSucceeds()
    {
        _fx.SkipIfUnavailable();
        var def = Def();
        await Repo.CreateAsync("agentrepo-dup-a", "shared", "n", "d", def, Sha(def), "a", default);

        Assert.Null(await Repo.CreateAsync("agentrepo-dup-a", "shared", "n2", "d", def, Sha(def), "a", default));
        Assert.NotNull(await Repo.CreateAsync("agentrepo-dup-b", "shared", "n", "d", def, Sha(def), "a", default));
    }

    // ---- publish:由 draft_definition 抽欄落 revision + pin skill(A-DATA-04/11)----

    [SkippableFact]
    public async Task Publish_ExtractsColumnsFromDraft_AndPinsSkillRevision()
    {
        _fx.SkipIfUnavailable();
        const string tenant = "agentrepo-pub";
        await InsertSkillAsync(tenant, "bound-skill", currentRevision: 3);

        const string rules =
            """{"version":1,"rules":[{"id":"refund","onUnknown":[{"action":"deny"}],"priority":100,"then":[{"action":"require_approval","role":"ADMIN"}],"when":{"fact":"action.amount","op":"gt","value":5000}}]}""";
        var def = Def(bindings: new[] { "bound-skill" }, businessRules: rules); // allowed_tools 省略 → canonical []
        var agent = await CreateValidatedAsync(tenant, "pub-slug", def);

        var result = await Repo.PublishAsync(
            tenant, agent.Id, agent.DraftVersion, def, Sha(def), "publisher", default);

        Assert.Equal(AgentWriteStatus.Success, result.Status);
        Assert.Equal(1, result.Revision);

        await using var conn = await _fx.DataSource!.OpenConnectionAsync();
        var row = await conn.QuerySingleAsync<RevRow>(
            "SELECT status AS Status, system_prompt AS SystemPrompt, allowed_tools::text AS AllowedTools,"
            + " business_rules::text AS BusinessRules,"
            + " runtime_workflow_id AS RuntimeWorkflowId, runtime_workflow_revision AS RuntimeWorkflowRevision,"
            + " definition_sha256 AS DefinitionSha256"
            + " FROM agent_revision WHERE agent_id = @id AND revision = 1",
            new { id = agent.Id });
        Assert.Equal("published", row.Status);
        Assert.Equal("你是研究助手", row.SystemPrompt);
        Assert.Equal("[]", row.AllowedTools); // A-DATA-11:空集合 pin 進 revision,非全開
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse(rules),
            JsonNode.Parse(row.BusinessRules)));
        Assert.Equal(Guid.Parse(AgentDefaults.RuntimeWorkflowId), row.RuntimeWorkflowId);
        Assert.Equal(AgentDefaults.RuntimeWorkflowRevision, row.RuntimeWorkflowRevision);
        Assert.Equal(Sha(def), row.DefinitionSha256);

        // binding 於交易內 join 解析出 skill_id,並 pin skill_revision=3。
        var binding = await conn.QuerySingleAsync<BindRow>(
            "SELECT rs.skill_revision AS SkillRevision, s.name AS Name"
            + " FROM agent_revision_skill rs JOIN skill s ON s.id = rs.skill_id"
            + " WHERE rs.agent_id = @id AND rs.agent_revision = 1",
            new { id = agent.Id });
        Assert.Equal("bound-skill", binding.Name);
        Assert.Equal(3, binding.SkillRevision);
    }

    [SkippableFact]
    public async Task Publish_Unvalidated_ReturnsVersionConflict()
    {
        _fx.SkipIfUnavailable();
        const string tenant = "agentrepo-unval";
        var def = Def();
        var agent = await Repo.CreateAsync(tenant, "unval", "n", "d", def, Sha(def), "a", default);

        // 未 MarkValidated → draft_validated_version != draft_version → 拒絕。
        var result = await Repo.PublishAsync(
            tenant, agent!.Id, agent.DraftVersion, def, Sha(def), "p", default);
        Assert.Equal(AgentWriteStatus.VersionConflict, result.Status);
    }

    [SkippableFact]
    public async Task Publish_ConcurrentSameVersion_AppendsExactlyOneRevision()
    {
        _fx.SkipIfUnavailable();
        const string tenant = "agentrepo-concurrent-publish";
        var def = Def();
        var agent = await CreateValidatedAsync(tenant, "concurrent-publish", def);

        var first = Repo.PublishAsync(
            tenant, agent.Id, agent.DraftVersion, def, Sha(def), "publisher-1", default);
        var second = Repo.PublishAsync(
            tenant, agent.Id, agent.DraftVersion, def, Sha(def), "publisher-2", default);
        var results = await Task.WhenAll(first, second);

        Assert.Single(results, result => result.Status == AgentWriteStatus.Success);
        Assert.Single(results, result => result.Status == AgentWriteStatus.VersionConflict);
        var revision = Assert.Single(await Repo.ListRevisionsAsync(tenant, agent.Id, default));
        Assert.Equal(1, revision.Revision);
    }

    [SkippableFact]
    public async Task Publish_RejectsUnknownWorkflowInsideTransaction()
    {
        _fx.SkipIfUnavailable();
        const string tenant = "agentrepo-bad-workflow";
        var def = Def(workflowId: Guid.NewGuid().ToString());
        var agent = await CreateValidatedAsync(tenant, "bad-workflow", def);

        var result = await Repo.PublishAsync(
            tenant, agent.Id, agent.DraftVersion, def, Sha(def), "p", default);

        Assert.Equal(AgentWriteStatus.InvalidReference, result.Status);
        Assert.Contains(result.Errors!, e => e.Field == "runtime_workflow");

        await using var conn = await _fx.DataSource!.OpenConnectionAsync();
        Assert.False(await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM agent_revision WHERE agent_id = @id)",
            new { id = agent.Id }));
    }

    [SkippableFact]
    public async Task Publish_RechecksAndRejectsSkillDisabledAfterValidation()
    {
        _fx.SkipIfUnavailable();
        const string tenant = "agentrepo-disabled-binding";
        await InsertSkillAsync(tenant, "disable-me", currentRevision: 1);
        var def = Def(bindings: new[] { "disable-me" });
        var agent = await CreateValidatedAsync(tenant, "disabled-binding", def);

        await using (var conn = await _fx.DataSource!.OpenConnectionAsync())
        {
            await conn.ExecuteAsync(
                "UPDATE skill SET enabled = false WHERE tenant_id = @tenant AND name = 'disable-me'",
                new { tenant });
        }

        var result = await Repo.PublishAsync(
            tenant, agent.Id, agent.DraftVersion, def, Sha(def), "p", default);

        Assert.Equal(AgentWriteStatus.InvalidReference, result.Status);
        Assert.Contains(result.Errors!, e =>
            e.Field == "skill_bindings" && e.Message.Contains("disable-me"));
    }

    [SkippableFact]
    public async Task PublishAndRestore_RejectDefinitionsOutsideExecutionSnapshotContract()
    {
        _fx.SkipIfUnavailable();
        const string tenant = "agentrepo-execution-contract";
        var invalidNode = JsonNode.Parse(Def(
            prompt: new string(
                'p', AgentExecutionContract.MaxSystemPromptLength + 1)))!.AsObject();
        invalidNode["runtime_limits"]!["timeout_seconds"] = -1;
        var invalidDefinition =
            AgentCanonicalizer.CanonicalizeDefinition(invalidNode.ToJsonString());
        var invalid = await CreateValidatedAsync(
            tenant, "invalid-execution-contract", invalidDefinition);

        var rejectedPublish = await Repo.PublishAsync(
            tenant,
            invalid.Id,
            invalid.DraftVersion,
            invalidDefinition,
            Sha(invalidDefinition),
            "p",
            default);

        Assert.Equal(AgentWriteStatus.InvalidReference, rejectedPublish.Status);
        Assert.Contains(
            rejectedPublish.Errors!,
            error => error.Field == "system_prompt");
        Assert.Contains(
            rejectedPublish.Errors!,
            error => error.Field == "runtime_limits.timeout_seconds");

        var validDefinition = Def();
        var valid = await CreateValidatedAsync(
            tenant, "valid-execution-contract", validDefinition);
        Assert.Equal(
            AgentWriteStatus.Success,
            (await Repo.PublishAsync(
                tenant,
                valid.Id,
                valid.DraftVersion,
                validDefinition,
                Sha(validDefinition),
                "p",
                default)).Status);

        var rejectedRestore = await Repo.RestoreAsync(
            tenant,
            valid.Id,
            1,
            invalidDefinition,
            Sha(invalidDefinition),
            "p",
            default);
        Assert.Equal(AgentWriteStatus.InvalidReference, rejectedRestore.Status);
        Assert.Single(await Repo.ListRevisionsAsync(
            tenant, valid.Id, default));
    }

    // ---- restore:原封複製快照 + bindings 成新 revision,不改寫歷史(A-DATA-06)----

    [SkippableFact]
    public async Task Restore_UsesNewCanonicalDefinition_CopiesBindings_WithoutMutatingSource()
    {
        _fx.SkipIfUnavailable();
        const string tenant = "agentrepo-restore";
        await InsertSkillAsync(tenant, "restore-skill", currentRevision: 2);

        const string originalRules =
            """{"version":1,"rules":[{"id":"restore-rule","when":{"fact":"action.amount","op":"gt","value":5000},"then":[{"action":"deny"}]}]}""";
        var def1 = Def(
            prompt: "第一版",
            bindings: new[] { "restore-skill" },
            businessRules: originalRules);
        var agent = await CreateValidatedAsync(tenant, "restore-slug", def1);
        await Repo.PublishAsync(
            tenant, agent.Id, agent.DraftVersion, def1, Sha(def1), "p", default); // revision 1

        // 改 draft 到第二版(無 binding),驗證後發布 → revision 2。
        var def2 = Def(prompt: "第二版");
        var updated = await Repo.UpdateDraftAsync(
            tenant, agent.Id, agent.DraftVersion, "研究助手", "說明", def2, Sha(def2), default);
        Assert.Equal(AgentWriteStatus.Success, updated.Status);
        await Repo.MarkValidatedAsync(
            tenant, agent.Id, updated.Agent!.DraftVersion, def2, Sha(def2), default);
        await Repo.PublishAsync(
            tenant, agent.Id, updated.Agent.DraftVersion, def2, Sha(def2), "p", default);

        // 模擬目前 Workflow validator 為舊 AST 補上新的 canonical default。restore 必須把它寫進
        // 新 revision，不能修改 source revision；pinned binding 仍要原封複製。
        var sourceBefore = await Repo.GetRevisionDefinitionAsync(tenant, agent.Id, 1, default);
        Assert.NotNull(sourceBefore);
        using var currentRules = JsonDocument.Parse(
            """{"version":1,"rules":[{"id":"restore-rule","when":{"fact":"action.amount","op":"gt","value":5000},"then":[{"action":"deny"}],"onUnknown":[{"action":"deny"}]}]}""");
        var restoreDefinition = AgentCanonicalizer.WithBusinessRules(
            sourceBefore!,
            currentRules.RootElement);
        var restore = await Repo.RestoreAsync(
            tenant,
            agent.Id,
            1,
            restoreDefinition!,
            Sha(restoreDefinition!),
            "p",
            default);
        Assert.Equal(AgentWriteStatus.Success, restore.Status);
        Assert.Equal(3, restore.Revision);

        var revisions = await Repo.ListRevisionsAsync(tenant, agent.Id, default);
        Assert.Equal(new[] { 3, 2, 1 }, revisions.Select(r => r.Revision).ToArray());
        Assert.Equal("published", revisions[0].Status);
        Assert.Equal("superseded", revisions[1].Status);
        Assert.Equal("superseded", revisions[2].Status);

        var rev3 = revisions[0];
        var rev1 = revisions[2];
        Assert.Equal(Sha(def1), rev1.DefinitionSha256);
        Assert.Equal(Sha(restoreDefinition), rev3.DefinitionSha256);
        Assert.NotEqual(rev1.DefinitionSha256, rev3.DefinitionSha256);
        Assert.Equal(
            sourceBefore,
            await Repo.GetRevisionDefinitionAsync(tenant, agent.Id, 1, default));
        Assert.Equal(
            restoreDefinition,
            await Repo.GetRevisionDefinitionAsync(tenant, agent.Id, 3, default));
        var pin = Assert.Single(rev3.SkillBindings);
        Assert.Equal("restore-skill", pin.Skill);
        Assert.Equal(2, pin.SkillRevision);
    }

    // ---- 軟停用保留 revision;跨租戶不可見 ----

    [SkippableFact]
    public async Task SoftDisable_HidesEnabledFlag_ButKeepsRevisions()
    {
        _fx.SkipIfUnavailable();
        const string tenant = "agentrepo-disable";
        var def = Def();
        var agent = await CreateValidatedAsync(tenant, "disable-slug", def);
        await Repo.PublishAsync(
            tenant, agent.Id, agent.DraftVersion, def, Sha(def), "p", default);

        Assert.True(await Repo.SetEnabledAsync(tenant, agent.Id, false, default));
        Assert.False((await Repo.GetAsync(tenant, agent.Id, default))!.Enabled);
        Assert.Single(await Repo.ListRevisionsAsync(tenant, agent.Id, default)); // revision 保留
    }

    [SkippableFact]
    public async Task CrossTenant_WritesAndReads_AreInvisible()
    {
        _fx.SkipIfUnavailable();
        var def = Def();
        var agent = await Repo.CreateAsync("agentrepo-iso-a", "iso", "n", "d", def, Sha(def), "a", default);

        Assert.Null(await Repo.GetAsync("agentrepo-iso-b", agent!.Id, default));
        Assert.False(await Repo.SetEnabledAsync("agentrepo-iso-b", agent.Id, false, default));

        var draft = await Repo.UpdateDraftAsync(
            "agentrepo-iso-b", agent.Id, 1, "n", "d", def, Sha(def), default);
        Assert.Equal(AgentWriteStatus.NotFound, draft.Status);

        var publish = await Repo.PublishAsync(
            "agentrepo-iso-b", agent.Id, 1, def, Sha(def), "p", default);
        Assert.Equal(AgentWriteStatus.NotFound, publish.Status);
    }

    // ---- draft optimistic concurrency(A-DATA-08 的 SQL 面)----

    [SkippableFact]
    public async Task UpdateDraft_WrongVersion_Conflicts_CorrectVersion_BumpsAndClearsValidated()
    {
        _fx.SkipIfUnavailable();
        const string tenant = "agentrepo-etag";
        var def = Def();
        var agent = await CreateValidatedAsync(tenant, "etag-slug", def);

        var stale = await Repo.UpdateDraftAsync(tenant, agent.Id, 99, "n", "d", def, Sha(def), default);
        Assert.Equal(AgentWriteStatus.VersionConflict, stale.Status);

        var ok = await Repo.UpdateDraftAsync(tenant, agent.Id, 1, "改", "說明", def, Sha(def), default);
        Assert.Equal(AgentWriteStatus.Success, ok.Status);
        Assert.Equal(2, ok.Agent!.DraftVersion);
        Assert.Null(ok.Agent.DraftValidatedVersion); // 改過就清空
    }

    // ---- 種子:Default Agent-Runtime Workflow current immutable revision 存在且 published ----

    [SkippableFact]
    public async Task Seed_DefaultAgentRuntimeWorkflow_ExistsPublishedCurrentRevision()
    {
        _fx.SkipIfUnavailable();
        await using var conn = await _fx.DataSource!.OpenConnectionAsync();
        var id = Guid.Parse(AgentDefaults.RuntimeWorkflowId);

        Assert.Equal("agent-runtime",
            await conn.ExecuteScalarAsync<string>("SELECT kind FROM workflow WHERE id = @id", new { id }));
        Assert.True(
            await conn.ExecuteScalarAsync<bool>("SELECT enabled FROM workflow WHERE id = @id", new { id }));
        Assert.Equal(AgentDefaults.RuntimeWorkflowRevision,
            await conn.ExecuteScalarAsync<int?>("SELECT published_revision FROM workflow WHERE id = @id", new { id }));
        Assert.True(await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM workflow_revision WHERE workflow_id = @id AND revision = @revision)",
            new { id, revision = AgentDefaults.RuntimeWorkflowRevision }));

        var definition = await conn.ExecuteScalarAsync<string>(
            "SELECT definition::text FROM workflow_revision WHERE workflow_id = @id AND revision = @revision",
            new { id, revision = AgentDefaults.RuntimeWorkflowRevision });
        var graph = JsonNode.Parse(definition!)!.AsObject();
        Assert.Single(graph["nodes"]!.AsArray(), n => n!["type"]!.GetValue<string>() == "start");
        Assert.Single(graph["nodes"]!.AsArray(), n => n!["type"]!.GetValue<string>() == "end");
        Assert.Empty(AgentDefaults.ValidateRuntimeWorkflowFixture());
    }

    [SkippableFact]
    public async Task Bootstrap_UpgradesPriorRev2WithoutOverwritingItsImmutableBytes()
    {
        _fx.SkipIfUnavailable();
        const string tenant = "agentrepo-runtime-upgrade";
        var priorRev2 = AgentDefaults.PreviousRuntimeWorkflowDefinition;
        var id = Guid.Parse(AgentDefaults.RuntimeWorkflowId);
        var priorHash = SkillHash.Sha256(priorRev2);
        Assert.Equal(AgentDefaults.PreviousRuntimeWorkflowSha256, priorHash);

        await using var connection = await _fx.DataSource!.OpenConnectionAsync();
        await connection.ExecuteAsync("INSERT INTO workflow_revision(workflow_id,revision,status,schema_version,definition,ui_metadata,definition_canonical,ui_metadata_canonical,definition_sha256,ui_metadata_sha256,compiler_contract_version,created_by) VALUES(@id,2,'published',1,@definition::jsonb,'{}'::jsonb,convert_to(@definition,'UTF8'),convert_to('{}','UTF8'),@hash,@uiHash,@contract,'upgrade-test') ON CONFLICT(workflow_id,revision) DO UPDATE SET status=EXCLUDED.status,schema_version=EXCLUDED.schema_version,definition=EXCLUDED.definition,ui_metadata=EXCLUDED.ui_metadata,definition_canonical=EXCLUDED.definition_canonical,ui_metadata_canonical=EXCLUDED.ui_metadata_canonical,definition_sha256=EXCLUDED.definition_sha256,ui_metadata_sha256=EXCLUDED.ui_metadata_sha256,compiler_contract_version=EXCLUDED.compiler_contract_version", new { id, definition = priorRev2, hash = priorHash, uiHash = SkillHash.Sha256("{}"), contract = WorkflowCompilerContracts.Current });
        await connection.ExecuteAsync("UPDATE workflow SET published_revision=2,draft_definition=@definition::jsonb,draft_definition_canonical=convert_to(@definition,'UTF8'),draft_definition_sha256=@hash WHERE id=@id", new { id, definition = priorRev2, hash = priorHash });

        var historicalAgentDefinition = AgentCanonicalizer.Canonicalize(new AgentUpsert(
            Slug: null, Name: null, Description: null, SystemPrompt: "historical worker",
            ExecutionRoles: ["worker"], Capabilities: null, OutputContract: null,
            Audience: ["ADMIN"], AllowedTools: [], SkillBindings: null,
            KnowledgeSources: null, BusinessRules: null, RuntimeLimits: null,
            RuntimeWorkflow: new AgentWorkflowRef(AgentDefaults.RuntimeWorkflowId, AgentDefaults.PreviousRuntimeWorkflowRevision)));
        var historicalAgentHash = SkillHash.Sha256(historicalAgentDefinition);
        var historicalAgentId = Guid.NewGuid();
        await connection.ExecuteAsync("INSERT INTO agent(id,tenant_id,slug,name,draft_definition,draft_definition_canonical,draft_definition_sha256,draft_validated_version,published_revision,created_by) VALUES(@agentId,@tenant,'historical-worker','Historical Worker',@definition::jsonb,@bytes,@hash,1,1,'upgrade-test'); INSERT INTO agent_revision(agent_id,revision,status,system_prompt,execution_roles,audience,business_rules,allowed_tools,knowledge_sources,runtime_limits,runtime_workflow_id,runtime_workflow_revision,definition_sha256,canonical_definition,created_by) VALUES(@agentId,1,'published','historical worker','[\"worker\"]'::jsonb,'[\"ADMIN\"]'::jsonb,'{}'::jsonb,'[]'::jsonb,'[]'::jsonb,'{}'::jsonb,@workflowId,@workflowRevision,@hash,@bytes,'upgrade-test')", new { agentId = historicalAgentId, tenant, definition = historicalAgentDefinition, bytes = Encoding.UTF8.GetBytes(historicalAgentDefinition), hash = historicalAgentHash, workflowId = id, workflowRevision = AgentDefaults.PreviousRuntimeWorkflowRevision });

        await Backend.Api.Data.DbBootstrap.RunAsync(_fx.DataSource!, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        Assert.Equal(AgentDefaults.RuntimeWorkflowRevision,
            await connection.ExecuteScalarAsync<int>("SELECT published_revision FROM workflow WHERE id=@id", new { id }));
        Assert.Equal(Encoding.UTF8.GetBytes(priorRev2),
            await connection.ExecuteScalarAsync<byte[]>("SELECT definition_canonical FROM workflow_revision WHERE workflow_id=@id AND revision=2", new { id }));
        Assert.Equal(priorHash,
            await connection.ExecuteScalarAsync<string>("SELECT definition_sha256 FROM workflow_revision WHERE workflow_id=@id AND revision=2", new { id }));
        Assert.Equal(Encoding.UTF8.GetBytes(AgentDefaults.RuntimeWorkflowDefinition),
            await connection.ExecuteScalarAsync<byte[]>("SELECT definition_canonical FROM workflow_revision WHERE workflow_id=@id AND revision=@revision", new { id, revision = AgentDefaults.RuntimeWorkflowRevision }));
        Assert.Equal(AgentDefaults.PreviousRuntimeWorkflowRevision,
            await connection.ExecuteScalarAsync<int>("SELECT runtime_workflow_revision FROM agent_revision WHERE agent_id=@agentId AND revision=1", new { agentId = historicalAgentId }));

        var historicalRunRepository = new AgentRunRepository(_fx.DataSource!);
        var started = await historicalRunRepository.CreateDirectAsync(tenant, "admin-a", "ADMIN", historicalAgentId, "replay historical pin", "historical-start", default);
        Assert.Equal(AgentRunWriteStatus.Success, started.Status);
        Assert.Equal(AgentDefaults.PreviousRuntimeWorkflowRevision, started.Run!.WorkflowRevision);
        var replayed = await historicalRunRepository.CreateDirectAsync(tenant, "admin-a", "ADMIN", historicalAgentId, "replay historical pin", "historical-start", default);
        Assert.Equal(AgentRunWriteStatus.Replay, replayed.Status);

        // Some pre-D5 local databases contain a different, self-consistent rev2 row. Bootstrap
        // must not rewrite that historical row, and already-published Agent pins still replay it.
        const string preD5ResidueModel = """{"edges":[],"governance":{"maxConcurrency":1,"maxSteps":1},"kind":"agent-runtime","nodes":[],"schemaVersion":1}""";
        var residueHash = SkillHash.Sha256(preD5ResidueModel);
        await connection.ExecuteAsync("UPDATE workflow_revision SET definition=@definition::jsonb,definition_canonical=convert_to(@definition,'UTF8'),definition_sha256=@hash WHERE workflow_id=@id AND revision=2", new { id, definition = preD5ResidueModel, hash = residueHash });
        await Backend.Api.Data.DbBootstrap.RunAsync(_fx.DataSource!, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        Assert.Equal(Encoding.UTF8.GetBytes(preD5ResidueModel),
            await connection.ExecuteScalarAsync<byte[]>("SELECT definition_canonical FROM workflow_revision WHERE workflow_id=@id AND revision=2", new { id }));
        var residueRun = await historicalRunRepository.CreateDirectAsync(tenant, "admin-a", "ADMIN", historicalAgentId, "execute residue pin", "residue-start", default);
        Assert.Equal(AgentRunWriteStatus.Success, residueRun.Status);
        Assert.Equal(AgentDefaults.PreviousRuntimeWorkflowRevision, residueRun.Run!.WorkflowRevision);

        var rejected = await Repo.ValidateReferencesAsync(tenant, historicalAgentDefinition, default);
        Assert.Contains(rejected, error => error.Field == "runtime_workflow");
        var omittedDefault = AgentCanonicalizer.Canonicalize(new AgentUpsert(
            Slug: null, Name: null, Description: null, SystemPrompt: "new worker",
            ExecutionRoles: ["worker"], Capabilities: null, OutputContract: null,
            Audience: ["ADMIN"], AllowedTools: [], SkillBindings: null,
            KnowledgeSources: null, BusinessRules: null, RuntimeLimits: null,
            RuntimeWorkflow: null));
        Assert.Equal(AgentDefaults.RuntimeWorkflowRevision, AgentCanonicalizer.WorkflowOf(omittedDefault).Revision);
        Assert.Empty(await Repo.ValidateReferencesAsync(tenant, omittedDefault, default));
    }

    [Fact]
    public void DefaultAgentRuntimeWorkflow_MatchesSharedFixtureExactly()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory); string? path = null;
        while (current is not null) { var candidate = Path.Combine(current.FullName, "plans", "agent-platform-redesign", "fixtures", "default-agent-runtime-workflow.json"); if (File.Exists(candidate)) { path = candidate; break; } current = current.Parent; }
        Assert.NotNull(path);
        var fixtureBytes = File.ReadAllBytes(path!); var backendBytes = Encoding.UTF8.GetBytes(AgentDefaults.RuntimeWorkflowDefinition);
        Assert.Equal(2003, fixtureBytes.Length);
        Assert.Equal(fixtureBytes, backendBytes);
        Assert.Equal((byte)'{', backendBytes[0]); Assert.Equal((byte)'}', backendBytes[^1]);
        Assert.Empty(AgentDefaults.ValidateRuntimeWorkflowFixture());
        Assert.Equal("1bcd5a670a62858a79fe7958b3a953fa922ef282200977be9ef6eacb43ed7f57", SkillHash.Sha256(AgentDefaults.RuntimeWorkflowDefinition));
    }

    [SkippableFact]
    public async Task Bootstrap_RejectsExistingRuntimeRevisionCanonicalByteDrift()
    {
        _fx.SkipIfUnavailable(); var id = Guid.Parse(AgentDefaults.RuntimeWorkflowId);
        await using var connection = await _fx.DataSource!.OpenConnectionAsync();
        await connection.ExecuteAsync("UPDATE workflow_revision SET ui_metadata_canonical=convert_to('{\"drift\":true}','UTF8') WHERE workflow_id=@id AND revision=@revision", new { id, revision = AgentDefaults.RuntimeWorkflowRevision });
        try { await Assert.ThrowsAsync<InvalidOperationException>(() => Backend.Api.Data.DbBootstrap.RunAsync(_fx.DataSource!, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)); }
        finally { await connection.ExecuteAsync("UPDATE workflow_revision SET ui_metadata_canonical=convert_to('{}','UTF8') WHERE workflow_id=@id AND revision=@revision", new { id, revision = AgentDefaults.RuntimeWorkflowRevision }); }
    }

    private sealed record RevRow(
        string Status, string SystemPrompt, string AllowedTools, string BusinessRules,
        Guid RuntimeWorkflowId, int RuntimeWorkflowRevision, string DefinitionSha256);

    private sealed record BindRow(int SkillRevision, string Name);
}
