using Backend.Api.Agents;
using Backend.Api.Skills;
using Dapper;
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
            "DELETE FROM agent_revision_skill WHERE agent_id IN (SELECT id FROM agent WHERE tenant_id LIKE 'agentrepo-%');"
            + " DELETE FROM agent_revision WHERE agent_id IN (SELECT id FROM agent WHERE tenant_id LIKE 'agentrepo-%');"
            + " DELETE FROM agent WHERE tenant_id LIKE 'agentrepo-%';"
            + " DELETE FROM skill_revision WHERE skill_id IN (SELECT id FROM skill WHERE tenant_id LIKE 'agentrepo-%');"
            + " DELETE FROM skill WHERE tenant_id LIKE 'agentrepo-%';");
    }

    private static string Def(
        string prompt = "你是研究助手", string[]? roles = null,
        string[]? bindings = null, string[]? allowedTools = null,
        string? workflowId = null)
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
            BusinessRules: null,
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
        Assert.True(await Repo.MarkValidatedAsync(tenant, agent!.Id, agent.DraftVersion, default));
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

        var def = Def(bindings: new[] { "bound-skill" }); // allowed_tools 省略 → canonical []
        var agent = await CreateValidatedAsync(tenant, "pub-slug", def);

        var result = await Repo.PublishAsync(
            tenant, agent.Id, agent.DraftVersion, "publisher", default);

        Assert.Equal(AgentWriteStatus.Success, result.Status);
        Assert.Equal(1, result.Revision);

        await using var conn = await _fx.DataSource!.OpenConnectionAsync();
        var row = await conn.QuerySingleAsync<RevRow>(
            "SELECT status AS Status, system_prompt AS SystemPrompt, allowed_tools::text AS AllowedTools,"
            + " runtime_workflow_id AS RuntimeWorkflowId, runtime_workflow_revision AS RuntimeWorkflowRevision,"
            + " definition_sha256 AS DefinitionSha256"
            + " FROM agent_revision WHERE agent_id = @id AND revision = 1",
            new { id = agent.Id });
        Assert.Equal("published", row.Status);
        Assert.Equal("你是研究助手", row.SystemPrompt);
        Assert.Equal("[]", row.AllowedTools); // A-DATA-11:空集合 pin 進 revision,非全開
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
            tenant, agent!.Id, agent.DraftVersion, "p", default);
        Assert.Equal(AgentWriteStatus.VersionConflict, result.Status);
    }

    [SkippableFact]
    public async Task Publish_RejectsUnknownWorkflowInsideTransaction()
    {
        _fx.SkipIfUnavailable();
        const string tenant = "agentrepo-bad-workflow";
        var def = Def(workflowId: Guid.NewGuid().ToString());
        var agent = await CreateValidatedAsync(tenant, "bad-workflow", def);

        var result = await Repo.PublishAsync(
            tenant, agent.Id, agent.DraftVersion, "p", default);

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
            tenant, agent.Id, agent.DraftVersion, "p", default);

        Assert.Equal(AgentWriteStatus.InvalidReference, result.Status);
        Assert.Contains(result.Errors!, e =>
            e.Field == "skill_bindings" && e.Message.Contains("disable-me"));
    }

    // ---- restore:原封複製快照 + bindings 成新 revision,不改寫歷史(A-DATA-06)----

    [SkippableFact]
    public async Task Restore_CopiesSnapshotAndBindings_AsNewRevision()
    {
        _fx.SkipIfUnavailable();
        const string tenant = "agentrepo-restore";
        await InsertSkillAsync(tenant, "restore-skill", currentRevision: 2);

        var def1 = Def(prompt: "第一版", bindings: new[] { "restore-skill" });
        var agent = await CreateValidatedAsync(tenant, "restore-slug", def1);
        await Repo.PublishAsync(
            tenant, agent.Id, agent.DraftVersion, "p", default); // revision 1

        // 改 draft 到第二版(無 binding),驗證後發布 → revision 2。
        var def2 = Def(prompt: "第二版");
        var updated = await Repo.UpdateDraftAsync(
            tenant, agent.Id, agent.DraftVersion, "研究助手", "說明", def2, Sha(def2), default);
        Assert.Equal(AgentWriteStatus.Success, updated.Status);
        await Repo.MarkValidatedAsync(tenant, agent.Id, updated.Agent!.DraftVersion, default);
        await Repo.PublishAsync(
            tenant, agent.Id, updated.Agent.DraftVersion, "p", default);

        // rollback 到 revision 1 → 新 revision 3,快照(hash)與固定 binding 與 rev1 相同。
        var restore = await Repo.RestoreAsync(tenant, agent.Id, 1, "p", default);
        Assert.Equal(AgentWriteStatus.Success, restore.Status);
        Assert.Equal(3, restore.Revision);

        var revisions = await Repo.ListRevisionsAsync(tenant, agent.Id, default);
        Assert.Equal(new[] { 3, 2, 1 }, revisions.Select(r => r.Revision).ToArray());
        Assert.Equal("published", revisions[0].Status);
        Assert.Equal("superseded", revisions[1].Status);
        Assert.Equal("superseded", revisions[2].Status);

        var rev3 = revisions[0];
        var rev1 = revisions[2];
        Assert.Equal(rev1.DefinitionSha256, rev3.DefinitionSha256); // 同一份快照(hash 相同)
        Assert.Equal(Sha(def1), rev3.DefinitionSha256);
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
        await Repo.PublishAsync(tenant, agent.Id, agent.DraftVersion, "p", default);

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
            "agentrepo-iso-b", agent.Id, 1, "p", default);
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

    private sealed record RevRow(
        string Status, string SystemPrompt, string AllowedTools,
        Guid RuntimeWorkflowId, int RuntimeWorkflowRevision, string DefinitionSha256);

    private sealed record BindRow(int SkillRevision, string Name);
}
