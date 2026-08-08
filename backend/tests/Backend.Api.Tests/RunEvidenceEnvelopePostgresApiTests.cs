using System.Net;
using System.Net.Http.Json;
using Backend.Api.Agents;
using Backend.Api.AgentRuns;
using Backend.Api.OperationsGovernance;
using Backend.Api.Skills;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Backend.Api.Tests;

/// <summary>
/// E1 run evidence envelope against the **real Dapper + Postgres** authority: tenant/run/snapshot
/// fail-closed and the dual-write reconcile endpoint both need a genuine <c>agent_run</c> row to
/// validate against, which the InMemory/lite repository (see <see cref="RunEvidenceEnvelopeApiTests"/>)
/// does not enforce. Isolation is tenant prefixing (per OperationsGovernancePostgresApiTests), not a
/// separate database, so this class cleans up its own rows before and after.
/// </summary>
[Collection("Postgres")]
public sealed class RunEvidenceEnvelopePostgresApiTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string TenantPrefix = "e1-evidence-";

    public Task InitializeAsync() => CleanupAsync();
    public Task DisposeAsync() => CleanupAsync();

    [SkippableFact]
    public async Task TenantAndSnapshotMismatch_FailClosed_ButLegacyMetricPathStillWrites()
    {
        fixture.SkipIfUnavailable();

        var tenant = TenantPrefix + Guid.NewGuid().ToString("N");
        var otherTenant = tenant + "-other";
        var runId = await CreateRunAsync(tenant);

        using var factory = new DapperEvidenceFactory(fixture.DataSource!);
        using var sameTenantClient = factory.CreateInternalClient().WithTenant(tenant).WithUser("workflow").WithRole("SYSTEM");
        using var crossTenantClient = factory.CreateInternalClient().WithTenant(otherTenant).WithUser("workflow").WithRole("SYSTEM");

        // Tenant mismatch: telemetry posted for a run that belongs to a *different* tenant.
        var crossTenantEvent = Guid.NewGuid();
        var crossTenantResponse = await crossTenantClient.PostAsJsonAsync("/api/operations/telemetry", new
        {
            run_id = runId, event_id = crossTenantEvent, kind = "model", node_id = "model_step",
            usage_units = 5, evidence = new { outcome = "success" },
        });
        Assert.Equal(HttpStatusCode.OK, crossTenantResponse.StatusCode);

        // Snapshot mismatch: correct tenant/run, but an asserted snapshot_sha256 that disagrees
        // with the run's own pinned snapshot.
        var snapshotMismatchEvent = Guid.NewGuid();
        var snapshotMismatchResponse = await sameTenantClient.PostAsJsonAsync("/api/operations/telemetry", new
        {
            run_id = runId, event_id = snapshotMismatchEvent, kind = "model", node_id = "model_step",
            usage_units = 5, evidence = new { outcome = "success", snapshot_sha256 = new string('f', 64) },
        });
        Assert.Equal(HttpStatusCode.OK, snapshotMismatchResponse.StatusCode);

        await using var connection = await fixture.DataSource!.OpenConnectionAsync();
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM operations_run_evidence WHERE tenant_id=@otherTenant", new { otherTenant }));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM operations_run_evidence WHERE tenant_id=@tenant AND event_id=@snapshotMismatchEvent",
            new { tenant, snapshotMismatchEvent }));
        // Tenant mismatch was already fail-closed at the pre-existing metric layer (the run
        // simply doesn't belong to otherTenant), so nothing is written there either -- unchanged,
        // pre-E1 behavior. The snapshot check is the E1-specific, envelope-only rejection: same
        // tenant/run, so the pre-existing metric write path still succeeds untouched.
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM operations_execution_metric WHERE tenant_id=@otherTenant", new { otherTenant }));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM operations_execution_metric WHERE tenant_id=@tenant AND event_id=@snapshotMismatchEvent",
            new { tenant, snapshotMismatchEvent }));
    }

    /// <summary>
    /// The other half of the snapshot decision table: a caller that asserts the run's *own* pinned
    /// snapshot_sha256 passes the WHERE fence and the row is written. Omitting the field only proves
    /// the `IS NULL` short circuit, and the mismatch case above only proves rejection -- neither
    /// shows that a correct non-null assertion is actually honoured rather than always failing closed.
    /// </summary>
    [SkippableFact]
    public async Task SnapshotSha256_AssertedEqualToTheRunsOwnPinnedSnapshot_IsAcceptedAndWritten()
    {
        fixture.SkipIfUnavailable();

        var tenant = TenantPrefix + Guid.NewGuid().ToString("N");
        var runId = await CreateRunAsync(tenant);

        await using var connection = await fixture.DataSource!.OpenConnectionAsync();
        var pinnedSha = await connection.ExecuteScalarAsync<string?>(
            "SELECT snapshot_sha256 FROM agent_run WHERE id=@runId", new { runId });
        Assert.NotNull(pinnedSha); // otherwise the caller assertion below would be the IS NULL branch

        using var factory = new DapperEvidenceFactory(fixture.DataSource!);
        using var client = factory.CreateInternalClient().WithTenant(tenant).WithUser("workflow").WithRole("SYSTEM");
        var eventId = Guid.NewGuid();
        var response = await client.PostAsJsonAsync("/api/operations/telemetry", new
        {
            run_id = runId, event_id = eventId, kind = "model", node_id = "model_step",
            usage_units = 5, evidence = new { outcome = "success", snapshot_sha256 = pinnedSha },
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(pinnedSha, await connection.ExecuteScalarAsync<string?>(
            "SELECT snapshot_sha256 FROM operations_run_evidence WHERE tenant_id=@tenant AND run_id=@runId AND event_id=@eventId",
            new { tenant, runId, eventId }));
    }

    /// <summary>
    /// Lineage for a run that IS an orchestrator child: root_run_id/child_id must resolve from the
    /// run's own <c>orchestrator_run_child</c> row. Every other run in this class is a plain
    /// direct-agent run (root_run_id = its own id, so the COALESCE falls through to NULL), which
    /// never takes this branch.
    /// </summary>
    [SkippableFact]
    public async Task OrchestratorChildRun_ResolvesRootAndChildLineage_FromItsOwnChildLinkage()
    {
        fixture.SkipIfUnavailable();

        var tenant = TenantPrefix + Guid.NewGuid().ToString("N");
        var runId = await CreateRunAsync(tenant);
        var rootRunId = Guid.NewGuid();
        var childId = Guid.NewGuid();

        await using var connection = await fixture.DataSource!.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO orchestrator_run
              (id,tenant_id,user_id,caller_role,orchestrator_id,orchestrator_revision,conversation_id,
               workflow_id,workflow_revision,execution_snapshot,execution_snapshot_canonical,snapshot_sha256,
               request_sha256,idempotency_key_sha256,status,deadline_at)
            VALUES
              (@rootRunId,@tenant,'owner','ADMIN',@orchestrator,1,'e1-evidence-root',@workflow,1,
               '{}'::jsonb,decode('7b7d','hex'),@hash,@hash,@hash,'running',now()+interval '1 hour');
            INSERT INTO orchestrator_run_child
              (id,orchestrator_root_run_id,task_id,attempt,run_kind,agent_id,agent_revision,
               workflow_id,workflow_revision,agent_snapshot_sha256,agent_run_id,dispatch_artifact,status)
              SELECT @childId,@rootRunId,'task-1',1,'worker',agent_id,agent_revision,
                     workflow_id,workflow_revision,snapshot_sha256,id,'{}'::jsonb,'running'
              FROM agent_run WHERE id=@runId;
            UPDATE agent_run SET orchestrator_root_run_id=@rootRunId WHERE id=@runId;
            """,
            new
            {
                rootRunId,
                childId,
                tenant,
                runId,
                orchestrator = Guid.NewGuid(),
                workflow = Guid.Parse(AgentDefaults.RuntimeWorkflowId),
                hash = new string('a', 64),
            });

        using var factory = new DapperEvidenceFactory(fixture.DataSource!);
        using var client = factory.CreateInternalClient().WithTenant(tenant).WithUser("workflow").WithRole("SYSTEM");
        var eventId = Guid.NewGuid();
        var response = await client.PostAsJsonAsync("/api/operations/telemetry", new
        {
            run_id = runId, event_id = eventId, kind = "node", node_id = "worker_step",
            usage_units = 3, evidence = new { outcome = "success" },
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(rootRunId, await connection.ExecuteScalarAsync<Guid?>(
            "SELECT root_run_id FROM operations_run_evidence WHERE tenant_id=@tenant AND run_id=@runId AND event_id=@eventId",
            new { tenant, runId, eventId }));
        Assert.Equal(childId, await connection.ExecuteScalarAsync<Guid?>(
            "SELECT child_id FROM operations_run_evidence WHERE tenant_id=@tenant AND run_id=@runId AND event_id=@eventId",
            new { tenant, runId, eventId }));
    }

    [SkippableFact]
    public async Task DualWrite_IsIdempotent_AndReconcilesWithoutASecondUsageCostAuthority()
    {
        fixture.SkipIfUnavailable();

        var tenant = TenantPrefix + Guid.NewGuid().ToString("N");
        var runId = await CreateRunAsync(tenant);
        using var factory = new DapperEvidenceFactory(fixture.DataSource!);
        using var client = factory.CreateInternalClient().WithTenant(tenant).WithUser("workflow").WithRole("SYSTEM");

        var eventId = Guid.NewGuid();
        var body = new
        {
            run_id = runId, event_id = eventId, kind = "model", node_id = "model_step",
            usage_units = 12, cost_units = 1.5m, latency_ms = 30,
            evidence = new { outcome = "success", observation_quality = "measured" },
        };
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/operations/telemetry", body)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/operations/telemetry", body)).StatusCode); // replay

        await using (var connection = await fixture.DataSource!.OpenConnectionAsync())
        {
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM operations_run_evidence WHERE tenant_id=@tenant AND run_id=@runId AND event_id=@eventId",
                new { tenant, runId, eventId }));
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM operations_execution_metric WHERE tenant_id=@tenant AND run_id=@runId AND event_id=@eventId",
                new { tenant, runId, eventId }));
            // reserved_budget_units is read from the run's own pinned snapshot (never the caller):
            // the fixture agent publishes with RuntimeLimits.TokenBudget=1024, nested under
            // execution_snapshot->'agent'->'runtime_limits' (see AgentRunSnapshotBuilder), not at
            // the snapshot root -- a wrong jsonb path silently resolves to NULL instead of erroring.
            Assert.Equal(1024L, await connection.ExecuteScalarAsync<long?>(
                "SELECT reserved_budget_units FROM operations_run_evidence WHERE tenant_id=@tenant AND run_id=@runId AND event_id=@eventId",
                new { tenant, runId, eventId }));
        }

        using var admin = factory.CreateInternalClient().WithTenant(tenant).WithUser("operator").WithRole("SYSTEM_ADMIN");
        admin.DefaultRequestHeaders.Add("X-User-Capabilities", "workflow.manage");
        var reconcileResponse = await admin.GetAsync("/api/admin/operations/evidence-reconcile");
        Assert.Equal(HttpStatusCode.OK, reconcileResponse.StatusCode);
        var reconcile = await reconcileResponse.ReadJsonAsync();
        Assert.Equal(1, reconcile["metric_event_count"]!.GetValue<int>());
        Assert.Equal(1, reconcile["envelope_event_count"]!.GetValue<int>());
        Assert.Equal(0, reconcile["mismatched_event_count"]!.GetValue<int>());
        Assert.Equal(12, reconcile["measured_usage_units_sum"]!.GetValue<long>());
    }

    [SkippableFact]
    public async Task PromptManifestSha256_IsResolvedServerSide_FromTheRunsPublishedAgentRevision_NeverTheCaller()
    {
        fixture.SkipIfUnavailable();

        var tenant = TenantPrefix + Guid.NewGuid().ToString("N");
        var runId = await CreateRunAsync(tenant);
        var authoritativeSha = new string('a', 64);

        // Simulate a P1-pinned agent_revision (bypassing the full prompt-artifacts publish flow,
        // which is out of scope here) so the run's OWN published revision carries an authoritative
        // prompt_manifest_sha256 distinct from anything the caller could supply.
        await using (var connection = await fixture.DataSource!.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE agent_revision SET prompt_manifest_sha256=@sha"
                + " FROM agent_run r WHERE agent_revision.agent_id=r.agent_id"
                + " AND agent_revision.revision=r.agent_revision AND r.id=@runId",
                new { sha = authoritativeSha, runId });
        }

        using var factory = new DapperEvidenceFactory(fixture.DataSource!);
        using var client = factory.CreateInternalClient().WithTenant(tenant).WithUser("workflow").WithRole("SYSTEM");
        var eventId = Guid.NewGuid();
        var response = await client.PostAsJsonAsync("/api/operations/telemetry", new
        {
            run_id = runId, event_id = eventId, kind = "model", node_id = "model_step",
            evidence = new { outcome = "success", prompt_manifest_sha256 = new string('b', 64) },
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var readConnection = await fixture.DataSource!.OpenConnectionAsync();
        var stored = await readConnection.ExecuteScalarAsync<string?>(
            "SELECT prompt_manifest_sha256 FROM operations_run_evidence WHERE tenant_id=@tenant AND run_id=@runId AND event_id=@eventId",
            new { tenant, runId, eventId });
        Assert.Equal(authoritativeSha, stored);
    }

    /// <summary>租戶前綴清理:依外鍵相依由葉往根刪,讓共用的 springaitest 不留 E1 殘列。
    /// otherTenant 恆為 "&lt;tenant&gt;-other",仍以同一個 TenantPrefix LIKE 掃到。</summary>
    private async Task CleanupAsync()
    {
        if (!fixture.Available) return;
        await using var connection = await fixture.DataSource!.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            DELETE FROM operations_run_evidence WHERE tenant_id LIKE @prefix;
            DELETE FROM operations_execution_metric WHERE tenant_id LIKE @prefix;
            DELETE FROM agent_run_event WHERE run_id IN (SELECT id FROM agent_run WHERE tenant_id LIKE @prefix);
            DELETE FROM agent_run_command WHERE run_id IN (SELECT id FROM agent_run WHERE tenant_id LIKE @prefix);
            DELETE FROM agent_run_skill WHERE run_id IN (SELECT id FROM agent_run WHERE tenant_id LIKE @prefix);
            DELETE FROM orchestrator_run_child WHERE orchestrator_root_run_id IN (SELECT id FROM orchestrator_run WHERE tenant_id LIKE @prefix);
            DELETE FROM agent_run WHERE tenant_id LIKE @prefix;
            DELETE FROM orchestrator_run WHERE tenant_id LIKE @prefix;
            DELETE FROM agent_revision_skill WHERE agent_id IN (SELECT id FROM agent WHERE tenant_id LIKE @prefix);
            DELETE FROM agent_revision WHERE agent_id IN (SELECT id FROM agent WHERE tenant_id LIKE @prefix);
            DELETE FROM agent WHERE tenant_id LIKE @prefix;
            """,
            new { prefix = TenantPrefix + "%" });
    }

    private async Task<Guid> CreateRunAsync(string tenant)
    {
        var definition = AgentCanonicalizer.Canonicalize(new AgentUpsert(
            Slug: null,
            Name: null,
            Description: null,
            SystemPrompt: "E1 evidence fixture",
            ExecutionRoles: new[] { "worker" },
            Capabilities: null,
            OutputContract: null,
            Audience: new[] { "ADMIN" },
            AllowedTools: Array.Empty<string>(),
            SkillBindings: null,
            KnowledgeSources: Array.Empty<string>(),
            BusinessRules: null,
            RuntimeLimits: new AgentRuntimeLimits(TokenBudget: 1024, StepBudget: 8),
            RuntimeWorkflow: new AgentWorkflowRef(
                AgentDefaults.RuntimeWorkflowId,
                AgentDefaults.RuntimeWorkflowRevision)));
        var hash = SkillHash.Sha256(definition);
        var agents = new AgentRepository(fixture.DataSource!);
        var agent = await agents.CreateAsync(
            tenant,
            "e1-evidence-agent-" + Guid.NewGuid().ToString("N"),
            "E1 evidence",
            "fixture",
            definition,
            hash,
            "operator",
            default);
        Assert.NotNull(agent);
        Assert.True(await agents.MarkValidatedAsync(tenant, agent!.Id, agent.DraftVersion, definition, hash, default));
        Assert.Equal(
            AgentWriteStatus.Success,
            (await agents.PublishAsync(tenant, agent.Id, agent.DraftVersion, definition, hash, "operator", default)).Status);
        var created = await new AgentRunRepository(fixture.DataSource!).CreateDirectAsync(
            tenant, "owner", "ADMIN", agent.Id, "evidence", "e1-evidence-run-" + Guid.NewGuid().ToString("N"), default);
        Assert.Equal(AgentRunWriteStatus.Success, created.Status);
        return created.Run!.Id;
    }

    private sealed class DapperEvidenceFactory(NpgsqlDataSource dataSource)
        : PostgresTestWebAppFactory(dataSource)
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("RUN_EVIDENCE_ENABLED", "true");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IOperationsGovernanceRepository>();
                services.AddScoped<IOperationsGovernanceRepository, OperationsGovernanceRepository>();
            });
        }
    }
}
