using System.Text.Json;
using System.Text.Json.Nodes;
using Backend.Api.Agents;
using Backend.Api.AgentRuns;
using Backend.Api.Data.InMemory;
using Backend.Api.OrchestratorRuns;
using Backend.Api.Orchestrators;
using Backend.Api.RunDiscovery;
using Backend.Api.Skills;
using Backend.Api.Workflows;

namespace Backend.Api.Tests;

/// <summary>
/// Lite-mode parity for O2's <see cref="InMemoryRunDiscoveryRepository"/>
/// (04-operations-trigger-plan.md §3). The equivalent real-Postgres scenarios -- including the
/// riskier hand-written UNION CTE/jsonb aggregation and the
/// <c>COALESCE(live agent_run status, orchestrator_run_child's cached status)</c> precedence -- are
/// separately proved by <see cref="RunDiscoveryRepositoryPostgresTests"/>; this file exercises the
/// same filter/pagination/visibility contract against the InMemory authority so both engines are
/// held to the same behavior.
/// </summary>
public sealed class RunDiscoveryRepositoryTests
{
    [Fact]
    public async Task ListAsync_AdminGatesAgentRunSourcedKinds_AndIsOwnerAndTenantScoped()
    {
        var (agentRuns, agent) = await PublishedAgentAsync("gate");
        var orchestratorRuns = EmptyOrchestratorRuns();
        var repo = new InMemoryRunDiscoveryRepository(agentRuns, orchestratorRuns);

        var mine = await agentRuns.CreateDirectAsync(
            "t", "admin-a", "ADMIN", Array.Empty<string>(), Array.Empty<string>(),
            agent.Id, "hello", "gate-start", default);
        Assert.Equal(AgentRunWriteStatus.Success, mine.Status);

        // Different owner, same tenant, same repository instance (agent_run is one shared table,
        // not owner-partitioned storage): must never appear -- matches GET /api/runs/{id}'s exact
        // tenant+user_id scoping, so the unified list can show only what the caller could also
        // fetch one at a time.
        await agentRuns.CreateDirectAsync(
            "t", "someone-else", "ADMIN", Array.Empty<string>(), Array.Empty<string>(),
            agent.Id, "hello", "other-owner-start", default);

        var adminView = await repo.ListAsync(
            "t", "admin-a", callerIsAdmin: true, EmptyFilter, position: null, limit: 10, default);
        Assert.Equal(RunDiscoveryKinds.DirectAgent, Assert.Single(adminView).Kind);
        Assert.Equal(mine.Run!.Id, adminView[0].Id);

        // Non-ADMIN caller: agent_run-sourced kinds vanish entirely, matching
        // AgentRunController's class-level [AdminOnly] on GET /api/runs/{id}.
        var nonAdminView = await repo.ListAsync(
            "t", "admin-a", callerIsAdmin: false, EmptyFilter, position: null, limit: 10, default);
        Assert.Empty(nonAdminView);

        // Cross-tenant: same owner id, different tenant -- indistinguishable from "nothing here".
        var crossTenantView = await repo.ListAsync(
            "other-tenant", "admin-a", callerIsAdmin: true, EmptyFilter, position: null, limit: 10, default);
        Assert.Empty(crossTenantView);
    }

    [Fact]
    public async Task ListAsync_AppliesKindStatusAgentAndCreatedRangeFilters()
    {
        var (agentRuns, agent) = await PublishedAgentAsync("filters");
        var repo = new InMemoryRunDiscoveryRepository(agentRuns, EmptyOrchestratorRuns());

        var run = await agentRuns.CreateDirectAsync(
            "t", "admin-a", "ADMIN", Array.Empty<string>(), Array.Empty<string>(),
            agent.Id, "hello", "filter-start", default);
        var created = (await repo.ListAsync("t", "admin-a", true, EmptyFilter, null, 10, default))[0].CreatedAt;

        Assert.Single(await repo.ListAsync(
            "t", "admin-a", true, EmptyFilter with { Kind = RunDiscoveryKinds.DirectAgent }, null, 10, default));
        Assert.Empty(await repo.ListAsync(
            "t", "admin-a", true, EmptyFilter with { Kind = RunDiscoveryKinds.Orchestrator }, null, 10, default));

        Assert.Single(await repo.ListAsync(
            "t", "admin-a", true, EmptyFilter with { Status = AgentRunStatuses.Queued }, null, 10, default));
        Assert.Empty(await repo.ListAsync(
            "t", "admin-a", true, EmptyFilter with { Status = AgentRunStatuses.Running }, null, 10, default));

        Assert.Single(await repo.ListAsync(
            "t", "admin-a", true, EmptyFilter with { AgentId = agent.Id }, null, 10, default));
        Assert.Empty(await repo.ListAsync(
            "t", "admin-a", true, EmptyFilter with { AgentId = Guid.NewGuid() }, null, 10, default));

        // On-point/off-point around the created range boundary.
        Assert.Single(await repo.ListAsync(
            "t", "admin-a", true, EmptyFilter with { CreatedFrom = created, CreatedTo = created }, null, 10, default));
        Assert.Empty(await repo.ListAsync(
            "t", "admin-a", true, EmptyFilter with { CreatedFrom = created.AddSeconds(1) }, null, 10, default));

        Assert.Empty(await repo.ListAsync(
            "t", "admin-a", true, EmptyFilter with { PendingApproval = true }, null, 10, default));
        Assert.Single(await repo.ListAsync(
            "t", "admin-a", true, EmptyFilter with { PendingApproval = false }, null, 10, default));
    }

    [Fact]
    public async Task ListAsync_KeysetPaginationAdvancesWithoutOverlap()
    {
        var (agentRuns, agent) = await PublishedAgentAsync("page");
        var repo = new InMemoryRunDiscoveryRepository(agentRuns, EmptyOrchestratorRuns());
        for (var i = 0; i < 3; i++)
        {
            await agentRuns.CreateDirectAsync(
                "t", "admin-a", "ADMIN", Array.Empty<string>(), Array.Empty<string>(),
                agent.Id, "hello " + i, "page-start-" + i, default);
        }

        var first = await repo.ListAsync("t", "admin-a", true, EmptyFilter, null, 2, default);
        Assert.Equal(2, first.Count);
        var cursor = new RunDiscoveryPosition(first[^1].CreatedAt, first[^1].Id);
        var second = await repo.ListAsync("t", "admin-a", true, EmptyFilter, cursor, 2, default);
        Assert.Single(second);
        Assert.Empty(first.Select(x => x.Id).Intersect(second.Select(x => x.Id)));
    }

    [Fact]
    public async Task ListAsync_OrchestratorRootIsVisibleToNonAdminCaller_WithComputedSummaryFields()
    {
        var runs = await OrchestratorFixtureAsync();
        var repo = new InMemoryRunDiscoveryRepository(new InMemoryAgentRunRepository(
            new InMemoryAgentRepository(new InMemorySkillRepository()), new InMemorySkillRepository()), runs.Runs);

        var created = await runs.Runs.CreateAsync(
            "t", "u", "USER", ["ops"], ["workflow.manage"], runs.OrchestratorId,
            "console-1", "plan it", "root-start", default);
        Assert.Equal(OrchestratorRunWriteStatus.Success, created.Status);

        // A USER-role caller with no ADMIN capability still sees their own orchestrator root --
        // GET /api/orchestrator-runs/{id} has no ADMIN gate, only tenant+owner scoping.
        var items = await repo.ListAsync(
            "t", "u", callerIsAdmin: false, EmptyFilter, position: null, limit: 10, default);
        var item = Assert.Single(items);
        Assert.Equal(RunDiscoveryKinds.Orchestrator, item.Kind);
        Assert.Equal(created.Run!.Id, item.Id);
        Assert.Equal(runs.OrchestratorId, item.OrchestratorId);
        Assert.Null(item.AgentId);
        Assert.Equal(0, item.ChildProgress!.Total);
        Assert.False(item.PendingApproval);
        Assert.False(item.NeedsRecovery);
        Assert.True(item.BudgetSummary.TryGetProperty("maxChildRuns", out _));

        Assert.Empty(await repo.ListAsync(
            "other-tenant", "u", false, EmptyFilter, null, 10, default));
    }

    private static readonly RunDiscoveryFilter EmptyFilter = new(null, null, null, null, null, null, null, null);

    private static InMemoryOrchestratorRunRepository EmptyOrchestratorRuns()
    {
        var workflows = new InMemoryWorkflowRepository();
        var workerId = Guid.NewGuid();
        var verifierId = Guid.NewGuid();
        var orchestrators = new StubOrchestrators(Guid.NewGuid(), "{}");
        return new InMemoryOrchestratorRunRepository(
            orchestrators, workflows, new StubAgents(workerId, verifierId, Guid.Empty, Guid.Empty));
    }

    private sealed record OrchestratorFixture(InMemoryOrchestratorRunRepository Runs, Guid OrchestratorId);

    private static async Task<OrchestratorFixture> OrchestratorFixtureAsync()
    {
        var workflows = new InMemoryWorkflowRepository();
        var root = await workflows.CreateAsync("t", "root", "orchestrator", "{\"schemaVersion\":1}", "{}", "u", default);
        Assert.True(await workflows.MarkValidatedAsync("t", root.Workflow!.Id, 1, "{\"schemaVersion\":1}", "{}", default));
        await workflows.PublishAsync("t", root.Workflow.Id, 1, "{\"schemaVersion\":1}", "{}", WorkflowCompilerContracts.Current, "u", default);
        var workerWorkflow = await CreateWorkflow(workflows, "worker", "agent-runtime");
        var verifierWorkflow = await CreateWorkflow(workflows, "verifier", "agent-runtime");
        var workerId = Guid.NewGuid();
        var verifierId = Guid.NewGuid();
        var orchestrators = new StubOrchestrators(Guid.NewGuid(), Definition(root.Workflow.Id, workerId, verifierId));
        var agents = new StubAgents(workerId, verifierId, workerWorkflow, verifierWorkflow);
        // agentRuns intentionally omitted (null): this fixture never creates children, so it never
        // needs the D3 friend-interface seam RunDiscoveryRepositoryPostgresTests exercises instead.
        return new OrchestratorFixture(
            new InMemoryOrchestratorRunRepository(orchestrators, workflows, agents), orchestrators.Id);
    }

    private static async Task<Guid> CreateWorkflow(InMemoryWorkflowRepository workflows, string name, string kind)
    {
        var item = await workflows.CreateAsync("t", name, kind, "{\"schemaVersion\":1}", "{}", "u", default);
        await workflows.MarkValidatedAsync("t", item.Workflow!.Id, 1, "{\"schemaVersion\":1}", "{}", default);
        await workflows.PublishAsync("t", item.Workflow.Id, 1, "{\"schemaVersion\":1}", "{}", WorkflowCompilerContracts.Current, "u", default);
        return item.Workflow.Id;
    }

    private static string Definition(Guid workflow, Guid worker, Guid verifier) => new JsonObject
    {
        ["instructions"] = "root",
        ["policy"] = new JsonObject { { "dispatchMode", "bounded-parallel" }, { "joinPolicy", "repair" }, { "repairPolicy", "redispatch" }, { "aggregationPolicy", "verified-only" }, { "denialPolicy", "fail-closed" } },
        ["workflow"] = new JsonObject { { "id", workflow.ToString("D") }, { "revision", 1 } },
        ["verifier"] = new JsonObject { { "agentId", verifier.ToString("D") }, { "revision", 1 }, { "variant", "read-only" }, { "independent", true }, { "outputContract", new JsonObject { { "type", "verification-report" } } } },
        ["workerPool"] = new JsonArray(new JsonObject { { "agentId", worker.ToString("D") }, { "revision", 1 } }),
        ["workerPolicy"] = new JsonObject { { "requiredAudience", new JsonArray() }, { "requiredCapabilities", new JsonArray() }, { "selection", "pinned-only" } },
        ["context"] = new JsonObject { { "readOnly", true }, { "allowedTools", new JsonArray() }, { "knowledgeSources", new JsonArray() } },
        ["audience"] = new JsonArray(),
        ["capabilities"] = new JsonArray(),
        ["budgets"] = new JsonObject { { "maxContextRounds", 1 }, { "maxTasks", 2 }, { "maxChildRuns", 3 }, { "maxConcurrency", 1 }, { "maxRepairRounds", 1 }, { "tokenBudget", 10 }, { "timeoutSeconds", 10 } },
    }.ToJsonString();

    private static async Task<(InMemoryAgentRunRepository Runs, Agent Agent)> PublishedAgentAsync(string slug)
    {
        var skills = new InMemorySkillRepository();
        var agents = new InMemoryAgentRepository(skills);
        var runs = new InMemoryAgentRunRepository(agents, skills);
        var definition = AgentCanonicalizer.Canonicalize(new AgentUpsert(
            Slug: null, Name: null, Description: null, SystemPrompt: "test", ExecutionRoles: new[] { "worker" },
            Capabilities: null, OutputContract: null, Audience: new[] { "ADMIN" }, AllowedTools: Array.Empty<string>(),
            SkillBindings: null, KnowledgeSources: Array.Empty<string>(), BusinessRules: null,
            RuntimeLimits: new AgentRuntimeLimits(TimeoutSeconds: 3600, StepBudget: 4),
            RuntimeWorkflow: new AgentWorkflowRef(AgentDefaults.RuntimeWorkflowId, AgentDefaults.RuntimeWorkflowRevision)));
        var hash = SkillHash.Sha256(definition);
        var agent = await agents.CreateAsync("t", slug, "測試", "O2 test", definition, hash, "admin-a", default);
        Assert.NotNull(agent);
        Assert.True(await agents.MarkValidatedAsync("t", agent!.Id, agent.DraftVersion, definition, hash, default));
        Assert.Equal(AgentWriteStatus.Success,
            (await agents.PublishAsync("t", agent.Id, agent.DraftVersion, definition, hash, "admin-a", default)).Status);
        return (runs, agent);
    }

    private sealed class StubOrchestrators(Guid id, string definition, params Guid[] additionalIds) : IOrchestratorRepository
    {
        public Guid Id => id;
        private bool Has(Guid requested) => requested == id || additionalIds.Contains(requested);
        public Task<Orchestrator?> GetAsync(string t, Guid requested, CancellationToken ct) => Task.FromResult(Has(requested) ? new Orchestrator(requested, "root", "", true, 1, null, 1, definition, DateTime.UtcNow, DateTime.UtcNow) : null);
        public Task<string?> RevisionAsync(string t, Guid requested, int revision, CancellationToken ct) => Task.FromResult<string?>(Has(requested) && revision == 1 ? definition : null);
        public Task<IReadOnlyList<OrchestratorInfo>> ListAsync(string t, CancellationToken ct) => throw new NotSupportedException(); public Task<OrchestratorWriteResult> CreateAsync(string a, string b, string c, string d, string e, CancellationToken f) => throw new NotSupportedException(); public Task<OrchestratorWriteResult> UpdateAsync(string a, Guid b, long c, string d, string e, string f, CancellationToken g) => throw new NotSupportedException(); public Task<IReadOnlyList<string>> ValidateReferencesAsync(string a, string b, CancellationToken c) => throw new NotSupportedException(); public Task<bool> MarkValidatedAsync(string a, Guid b, long c, string d, CancellationToken e) => throw new NotSupportedException(); public Task<OrchestratorWriteResult> PublishAsync(string a, Guid b, long c, string d, string e, CancellationToken f) => throw new NotSupportedException(); public Task<IReadOnlyList<OrchestratorRevisionInfo>> RevisionsAsync(string a, Guid b, CancellationToken c) => throw new NotSupportedException(); public Task<OrchestratorWriteResult> RestoreAsync(string a, Guid b, int c, string d, string e, CancellationToken f) => throw new NotSupportedException(); public Task<bool> SetEnabledAsync(string a, Guid b, bool c, CancellationToken d) => throw new NotSupportedException();
    }

    private sealed class StubAgents(Guid worker, Guid verifier, Guid workerWorkflow, Guid verifierWorkflow) : Agents.IAgentRepository
    {
        private static string Definition(Guid workflow, string role) => new JsonObject { ["system_prompt"] = "", ["execution_roles"] = new JsonArray(role), ["capabilities"] = new JsonArray("research"), ["output_contract"] = new JsonObject(), ["audience"] = new JsonArray("role:ADMIN"), ["allowed_tools"] = new JsonArray(), ["skill_bindings"] = new JsonArray(), ["knowledge_sources"] = new JsonArray(), ["business_rules"] = new JsonObject { { "version", 1 }, { "rules", new JsonArray() } }, ["runtime_limits"] = new JsonObject(), ["runtime_workflow"] = new JsonObject { { "id", workflow.ToString("D") }, { "revision", 1 } } }.ToJsonString();
        private (Guid Workflow, string Role) Source(Guid id) => (id == worker ? (workerWorkflow, "worker") : (verifierWorkflow, "verifier"));
        public Task<Agent?> GetAsync(string t, Guid id, CancellationToken ct) { if (id != worker && id != verifier) return Task.FromResult<Agent?>(null); var x = Source(id); var d = Definition(x.Workflow, x.Role); return Task.FromResult<Agent?>(new(id, x.Role, x.Role, "", true, 1, null, 1, d, SkillHash.Sha256(d), DateTime.UtcNow, DateTime.UtcNow)); }
        public Task<string?> GetRevisionDefinitionAsync(string t, Guid id, int r, CancellationToken ct) { var x = Source(id); return Task.FromResult<string?>(r == 1 && (id == worker || id == verifier) ? Definition(x.Workflow, x.Role) : null); }
        public async Task<IReadOnlyList<AgentRevisionInfo>> ListRevisionsAsync(string t, Guid id, CancellationToken ct) { var d = await GetRevisionDefinitionAsync(t, id, 1, ct); if (d is null) return []; var x = Source(id); return [new(1, "published", SkillHash.Sha256(d), x.Workflow, 1, [], "u", DateTime.UtcNow)]; }
        public Task<IReadOnlyList<AgentInfo>> ListAsync(string a, CancellationToken b) => throw new NotSupportedException(); public Task<Agent?> CreateAsync(string a, string b, string c, string d, string e, string f, string g, CancellationToken h) => throw new NotSupportedException(); public Task<AgentDraftResult> UpdateDraftAsync(string a, Guid b, long c, string d, string e, string f, string g, CancellationToken h) => throw new NotSupportedException(); public Task<IReadOnlyList<AgentValidationError>> ValidateReferencesAsync(string a, string b, CancellationToken c) => throw new NotSupportedException(); public Task<bool> MarkValidatedAsync(string a, Guid b, long c, string d, string e, CancellationToken f) => throw new NotSupportedException(); public Task<AgentPublishResult> PublishAsync(string a, Guid b, long c, string d, string e, string f, CancellationToken g, Backend.Api.PromptArtifacts.PromptManifestPin? h = null) => throw new NotSupportedException(); public Task<AgentPublishResult> RestoreAsync(string a, Guid b, int c, string d, string e, string f, CancellationToken g) => throw new NotSupportedException(); public Task<bool> SetEnabledAsync(string a, Guid b, bool c, CancellationToken d) => throw new NotSupportedException();
    }
}
