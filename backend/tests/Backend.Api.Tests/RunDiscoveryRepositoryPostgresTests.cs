using System.Text;
using System.Text.Json;
using Backend.Api.AgentRuns;
using Backend.Api.Agents;
using Backend.Api.RunDiscovery;
using Backend.Api.Skills;
using Dapper;

namespace Backend.Api.Tests;

/// <summary>
/// Real-PostgreSQL coverage for O2's <see cref="RunDiscoveryRepository"/> UNION CTE
/// (04-operations-trigger-plan.md §3): the InMemory-backed parity tests in
/// <see cref="RunDiscoveryRepositoryTests"/> prove the same scenarios can never diverge behind the
/// two authorities, but only real Postgres proves the hand-written casts/jsonb aggregation actually
/// execute — in particular the ChildProgress/PendingApproval subqueries' <c>COALESCE(ac.status,
/// c.status)</c> precedence, which mirrors <c>OrchestratorRunRepository.GetChildAsync</c>'s own
/// "live agent_run wins over the write-path cache" rule.
/// </summary>
[Collection("Postgres")]
public sealed class RunDiscoveryRepositoryPostgresTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    public RunDiscoveryRepositoryPostgresTests(PostgresFixture fixture) => _fixture = fixture;

    private RunDiscoveryRepository Runs => new(_fixture.DataSource!);
    private AgentRunRepository AgentRuns => new(_fixture.DataSource!);
    private AgentRepository Agents => new(_fixture.DataSource!);

    public Task InitializeAsync() => CleanupAsync();
    public Task DisposeAsync() => CleanupAsync();

    private async Task CleanupAsync()
    {
        if (!_fixture.Available) return;
        await using var connection = await _fixture.DataSource!.OpenConnectionAsync();
        await connection.ExecuteAsync(
            // agent_run rows (including O2's synthetic worker children) carry a FK to
            // orchestrator_run via orchestrator_root_run_id -- they must go first.
            "DELETE FROM orchestrator_run_child WHERE orchestrator_root_run_id IN (SELECT id FROM orchestrator_run WHERE tenant_id LIKE 'rundiscovery-%');"
            + " DELETE FROM agent_run_command WHERE tenant_id LIKE 'rundiscovery-%';"
            + " DELETE FROM agent_run_event WHERE run_id IN (SELECT id FROM agent_run WHERE tenant_id LIKE 'rundiscovery-%');"
            + " DELETE FROM agent_run_skill WHERE run_id IN (SELECT id FROM agent_run WHERE tenant_id LIKE 'rundiscovery-%');"
            + " DELETE FROM agent_run WHERE tenant_id LIKE 'rundiscovery-%';"
            + " DELETE FROM orchestrator_run_command WHERE run_id IN (SELECT id FROM orchestrator_run WHERE tenant_id LIKE 'rundiscovery-%');"
            + " DELETE FROM orchestrator_run_event WHERE run_id IN (SELECT id FROM orchestrator_run WHERE tenant_id LIKE 'rundiscovery-%');"
            + " DELETE FROM orchestrator_run WHERE tenant_id LIKE 'rundiscovery-%';"
            + " DELETE FROM agent_revision_skill WHERE agent_id IN (SELECT id FROM agent WHERE tenant_id LIKE 'rundiscovery-%');"
            + " DELETE FROM agent_revision WHERE agent_id IN (SELECT id FROM agent WHERE tenant_id LIKE 'rundiscovery-%');"
            + " DELETE FROM agent WHERE tenant_id LIKE 'rundiscovery-%';");
    }

    [SkippableFact]
    public async Task ListAsync_CombinesBothSourcesWithAdminGateAndComputedFieldsThroughDapper()
    {
        _fixture.SkipIfUnavailable();
        var tenant = "rundiscovery-combine-" + Guid.NewGuid().ToString("N");
        var agent = await PublishedAgentAsync(tenant, "combine");

        var direct = await AgentRuns.CreateDirectAsync(
            tenant, "admin-a", "ADMIN", Array.Empty<string>(), Array.Empty<string>(),
            agent.Id, "hello", "combine-start", default);
        Assert.Equal(AgentRunWriteStatus.Success, direct.Status);

        var rootId = Guid.NewGuid();
        await InsertOrchestratorRunAsync(tenant, rootId, "running");
        // A stale command claim (completed_at IS NULL, claim_expires_at in the past) marks the root
        // needing recovery — the same "an active command whose claim has expired" signal O4 will
        // later surface with more detail.
        await ExecuteAsync(
            "INSERT INTO orchestrator_run_command(id,run_id,command_type,claim_token_sha256,claim_expires_at) VALUES(@id,@run,'start',@hash,now() - interval '1 minute')",
            new { id = Guid.NewGuid(), run = rootId, hash = SkillHash.Sha256("stale") });

        // The child's live agent_run row is waiting_approval; orchestrator_run_child's own status
        // column deliberately stays the stale creation-time value ('queued') to prove the query
        // reads the live row (COALESCE(ac.status, c.status)), not the write-path cache.
        var childAgentRunId = Guid.NewGuid();
        await InsertChildAgentRunAsync(
            tenant, childAgentRunId, rootId, agent.Id, agent.PublishedRevision!.Value, "waiting_approval");
        await ExecuteAsync(
            "INSERT INTO orchestrator_run_child(id,orchestrator_root_run_id,task_id,attempt,run_kind,agent_id,agent_revision,workflow_id,workflow_revision,agent_snapshot_sha256,agent_run_id,dispatch_artifact,status) VALUES(@id,@root,'t1',1,'worker',@agentId,@agentRevision,@workflowId,@workflowRevision,@hash,@agentRunId,'{}'::jsonb,'queued')",
            new
            {
                id = Guid.NewGuid(),
                root = rootId,
                agentId = agent.Id,
                agentRevision = agent.PublishedRevision!.Value,
                workflowId = Guid.Parse(AgentDefaults.RuntimeWorkflowId),
                workflowRevision = AgentDefaults.RuntimeWorkflowRevision,
                hash = new string('a', 64),
                agentRunId = childAgentRunId,
            });

        var admin = await Runs.ListAsync(
            tenant, "admin-a", callerIsAdmin: true, EmptyFilter, position: null, limit: 10, default);
        // Three items: the direct-agent run, the orchestrator root, and the worker child itself
        // (agent_run rows are listed individually -- ChildProgress on the root is a summary, not a
        // replacement for the child's own entry).
        Assert.Equal(
            new[] { RunDiscoveryKinds.DirectAgent, RunDiscoveryKinds.Orchestrator, RunDiscoveryKinds.Worker },
            admin.Select(x => x.Kind).OrderBy(x => x, StringComparer.Ordinal));

        var root = Assert.Single(admin, x => x.Kind == RunDiscoveryKinds.Orchestrator);
        Assert.Equal(1, root.ChildProgress!.Total);
        // "waiting_approval" is not one of the five tracked buckets, so it must not be
        // miscounted into any of them -- only Total advances.
        Assert.Equal(0, root.ChildProgress.Queued + root.ChildProgress.Running
            + root.ChildProgress.Completed + root.ChildProgress.Failed + root.ChildProgress.Cancelled);
        Assert.True(root.PendingApproval);
        Assert.True(root.NeedsRecovery);
        Assert.NotNull(root.OrchestratorId);
        Assert.Null(root.AgentId);
        Assert.True(root.BudgetSummary.TryGetProperty("limits", out _));
        Assert.True(root.BudgetSummary.TryGetProperty("token_budget", out _));

        var directItem = Assert.Single(admin, x => x.Kind == RunDiscoveryKinds.DirectAgent);
        Assert.Equal(agent.Id, directItem.AgentId);
        Assert.Null(directItem.OrchestratorId);
        Assert.False(directItem.PendingApproval);

        var workerItem = Assert.Single(admin, x => x.Kind == RunDiscoveryKinds.Worker);
        Assert.Equal("waiting_approval", workerItem.Status);
        Assert.Equal(rootId, workerItem.OrchestratorRootRunId);
        Assert.True(workerItem.PendingApproval);

        // Non-ADMIN caller: the agent_run-sourced kind disappears entirely (matches
        // AgentRunController's class-level [AdminOnly] on GET /api/runs/{id}); the orchestrator
        // root item -- which has no ADMIN gate on its own detail endpoint -- still appears.
        var nonAdmin = await Runs.ListAsync(
            tenant, "admin-a", callerIsAdmin: false, EmptyFilter, position: null, limit: 10, default);
        Assert.Equal(RunDiscoveryKinds.Orchestrator, Assert.Single(nonAdmin).Kind);

        // kind/status filters run through the same SQL casts.
        var kindFiltered = await Runs.ListAsync(
            tenant, "admin-a", true, EmptyFilter with { Kind = RunDiscoveryKinds.DirectAgent },
            null, 10, default);
        Assert.Equal(RunDiscoveryKinds.DirectAgent, Assert.Single(kindFiltered).Kind);

        var statusFiltered = await Runs.ListAsync(
            tenant, "admin-a", true, EmptyFilter with { Status = "running" }, null, 10, default);
        Assert.Equal(RunDiscoveryKinds.Orchestrator, Assert.Single(statusFiltered).Kind);

        var pendingFiltered = await Runs.ListAsync(
            tenant, "admin-a", true, EmptyFilter with { PendingApproval = false }, null, 10, default);
        Assert.All(pendingFiltered, x => Assert.False(x.PendingApproval));
    }

    [SkippableFact]
    public async Task ListAsync_KeysetPaginationAdvancesWithoutOverlapThroughDapper()
    {
        _fixture.SkipIfUnavailable();
        var tenant = "rundiscovery-page-" + Guid.NewGuid().ToString("N");
        var agent = await PublishedAgentAsync(tenant, "page");
        for (var i = 0; i < 3; i++)
        {
            var created = await AgentRuns.CreateDirectAsync(
                tenant, "admin-a", "ADMIN", Array.Empty<string>(), Array.Empty<string>(),
                agent.Id, "hello " + i, "page-start-" + i, default);
            Assert.Equal(AgentRunWriteStatus.Success, created.Status);
            // agent_run.created_at defaults to now(); force distinct, ordered timestamps so the
            // keyset cursor's tie-break is exercised deterministically instead of relying on
            // sub-millisecond clock resolution.
            await ExecuteAsync(
                "UPDATE agent_run SET created_at = now() - make_interval(secs => @offset) WHERE id=@id",
                new { offset = 3 - i, id = created.Run!.Id });
        }

        var firstPage = await Runs.ListAsync(
            tenant, "admin-a", true, EmptyFilter, position: null, limit: 2, default);
        Assert.Equal(2, firstPage.Count);
        var cursor = new RunDiscoveryPosition(firstPage[^1].CreatedAt, firstPage[^1].Id);

        var secondPage = await Runs.ListAsync(
            tenant, "admin-a", true, EmptyFilter, position: cursor, limit: 2, default);
        Assert.Single(secondPage);
        Assert.Empty(firstPage.Select(x => x.Id).Intersect(secondPage.Select(x => x.Id)));
    }

    private static readonly RunDiscoveryFilter EmptyFilter = new(null, null, null, null, null, null, null, null);

    private async Task InsertOrchestratorRunAsync(string tenant, Guid id, string status)
    {
        const string snapshot = """{"limits":{"max_context_rounds":1,"max_tasks":1,"max_child_runs":1,"max_concurrency":1,"max_repair_rounds":0,"timeout_seconds":30},"token_budget":1}""";
        await ExecuteAsync(
            """
            INSERT INTO orchestrator_run
            (id,tenant_id,user_id,caller_role,orchestrator_id,orchestrator_revision,conversation_id,workflow_id,workflow_revision,execution_snapshot,execution_snapshot_canonical,snapshot_sha256,request_sha256,idempotency_key_sha256,status,checkpoint_ref,checkpoint_version,deadline_at)
            VALUES (@id,@tenant,'admin-a','ADMIN',@orchestrator,1,@conversation,@workflow,1,@snapshot::jsonb,@bytes,@hash,@hash,@keyHash,@status,'rctx1:test:hash:mac',1,clock_timestamp()+interval '1 hour');
            """,
            new
            {
                id,
                tenant,
                orchestrator = Guid.NewGuid(),
                conversation = "conv-" + id.ToString("N"),
                workflow = Guid.NewGuid(),
                snapshot,
                bytes = Encoding.UTF8.GetBytes(snapshot),
                hash = new string('a', 64),
                keyHash = SkillHash.Sha256(Guid.NewGuid().ToString("N")),
                status,
            });
    }

    private async Task InsertChildAgentRunAsync(
        string tenant, Guid id, Guid orchestratorRootRunId, Guid agentId, int agentRevision, string status)
    {
        var snapshot = JsonSerializer.Serialize(new
        {
            caller = new { tenant_id = tenant, user_id = "admin-a", role = "ADMIN" },
            agent = new { runtime_limits = new { } },
        });
        await ExecuteAsync(
            """
            INSERT INTO agent_run
            (id,tenant_id,root_run_id,parent_run_id,task_id,run_kind,user_id,caller_role,agent_id,agent_revision,workflow_id,workflow_revision,execution_snapshot,snapshot_sha256,status,deadline_at,orchestrator_root_run_id)
            VALUES (@id,@tenant,@id,NULL,'t1','worker','admin-a','ADMIN',@agentId,@agentRevision,@workflowId,@workflowRevision,@snapshot::jsonb,@hash,@status,clock_timestamp()+interval '1 hour',@rootRunId);
            """,
            new
            {
                id,
                tenant,
                agentId,
                agentRevision,
                workflowId = Guid.Parse(AgentDefaults.RuntimeWorkflowId),
                workflowRevision = AgentDefaults.RuntimeWorkflowRevision,
                snapshot,
                hash = new string('b', 64),
                status,
                rootRunId = orchestratorRootRunId,
            });
    }

    private async Task ExecuteAsync(string sql, object parameters)
    {
        await using var connection = await _fixture.DataSource!.OpenConnectionAsync();
        await connection.ExecuteAsync(sql, parameters);
    }

    private async Task<Agent> PublishedAgentAsync(string tenant, string slug)
    {
        var definition = AgentCanonicalizer.Canonicalize(new AgentUpsert(
            Slug: null,
            Name: null,
            Description: null,
            SystemPrompt: "你是可靠的研究助手",
            ExecutionRoles: new[] { "worker" },
            Capabilities: null,
            OutputContract: null,
            Audience: new[] { "ADMIN" },
            AllowedTools: Array.Empty<string>(),
            SkillBindings: null,
            KnowledgeSources: null,
            BusinessRules: null,
            RuntimeLimits: new AgentRuntimeLimits(StepBudget: 8),
            RuntimeWorkflow: new AgentWorkflowRef(
                AgentDefaults.RuntimeWorkflowId,
                AgentDefaults.RuntimeWorkflowRevision)));
        var hash = SkillHash.Sha256(definition);
        var agent = await Agents.CreateAsync(
            tenant, slug, "研究助手", "O2 test", definition, hash, "admin-a", default);
        Assert.NotNull(agent);
        Assert.True(await Agents.MarkValidatedAsync(
            tenant, agent!.Id, agent.DraftVersion, definition, hash, default));
        var publish = await Agents.PublishAsync(
            tenant, agent.Id, agent.DraftVersion, definition, hash, "admin-a", default);
        Assert.Equal(AgentWriteStatus.Success, publish.Status);
        return (await Agents.GetAsync(tenant, agent.Id, default))!;
    }
}
