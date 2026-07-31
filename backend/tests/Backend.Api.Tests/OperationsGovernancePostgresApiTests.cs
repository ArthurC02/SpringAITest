using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Backend.Api.Agents;
using Backend.Api.AgentRuns;
using Backend.Api.OperationsGovernance;
using Backend.Api.Skills;
using Dapper;
using Npgsql;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Backend.Api.Tests;

/// <summary>
/// D7 governance/metrics/redaction 的**真 Dapper + HTTP** 驗收。
/// 隔離手段是租戶前綴(每跑一次都是新的隨機 "d7-ops-&lt;guid&gt;",且 repository 的每一條查詢都帶
/// tenant_id 條件),不是獨立資料庫 —— 因此本類自己負責前後清理殘留,比照
/// OrchestratorRunRepositoryPostgresTests。曾經用「資料庫名須以 d7evidence_ 開頭」當護欄,
/// 結果是日常與 CI 一律 skipped、整條 Dapper D7 路徑零覆蓋,比殘留更糟。
/// </summary>
[Collection("Postgres")]
public sealed class OperationsGovernancePostgresApiTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string TenantPrefix = "d7-ops-";

    // CleanupAsync 自己在 appdb 不可達時是 no-op,所以 lifetime 不丟 SkipException
    // (skip 的判定留在測試方法本體,由 SkippableFact 的 discoverer 正確回報 Skipped)。
    public Task InitializeAsync() => CleanupAsync();

    public Task DisposeAsync() => CleanupAsync();

    [SkippableFact]
    public async Task D7_DapperHttp_ReleaseGateRolloutTelemetryMetricsAndRedaction()
    {
        fixture.SkipIfUnavailable();

        var tenant = TenantPrefix + Guid.NewGuid().ToString("N");
        var otherTenant = tenant + "-other";
        var run = await CreateRunAsync(tenant);
        var roots = await SeedRootMetricsAsync(tenant, run.Run!.Id);
        using var factory = new DapperOperationsFactory();
        using var admin = Client(factory, tenant, "operator", manage: true);

        var failed = await admin.PostAsJsonAsync(
            "/api/admin/operations/regressions",
            new { suite = "d7-release", passed = false, evidence_ref = "private/evidence/ref" });
        Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
        var blocked = await admin.PutAsJsonAsync(
            "/api/admin/operations/rollout",
            new { enabled = true, orchestrator_id = Guid.NewGuid(), revision = 2, canary_user_ids = new[] { "user-a" } });
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);

        static HttpRequestMessage OverrideRequest()
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/operations/regression-overrides")
            {
                Content = JsonContent.Create(new { reason = "reviewed break-glass release" }),
            };
            request.Headers.Add("Idempotency-Key", "d7-governance-logical-attempt");
            return request;
        }
        using var approve = OverrideRequest();
        using var replay = OverrideRequest();
        var concurrent = await Task.WhenAll(admin.SendAsync(approve), admin.SendAsync(replay));
        Assert.All(concurrent, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));

        await using (var connection = await fixture.DataSource!.OpenConnectionAsync())
        {
            Assert.Null(await connection.ExecuteScalarAsync<Guid?>(
                "SELECT default_orchestrator_id FROM tenant_runtime_binding WHERE tenant_id=@tenant",
                new { tenant }));
            Assert.Equal(2, await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM operations_release_audit WHERE tenant_id=@tenant",
                new { tenant }));
        }

        var rolloutId = Guid.NewGuid();
        var enabled = await admin.PutAsJsonAsync(
            "/api/admin/operations/rollout",
            new { enabled = true, orchestrator_id = rolloutId, revision = 2, canary_user_ids = new[] { "user-a" } });
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        Assert.True((await enabled.ReadJsonAsync())["new_roots_only"]!.GetValue<bool>());
        var rollback = await admin.PutAsJsonAsync(
            "/api/admin/operations/rollout",
            new { enabled = false, orchestrator_id = rolloutId, revision = 2, canary_user_ids = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.OK, rollback.StatusCode);

        using var telemetry = factory.CreateInternalClient()
            .WithTenant(tenant).WithUser("workflow").WithRole("SYSTEM");
        var eventId = Guid.NewGuid();
        var metric = new
        {
            run_id = run.Run.Id,
            event_id = eventId,
            kind = "tool",
            node_id = "approved_write",
            tool_name = "runtime.write_evidence",
            skill_name = "refund",
            skill_revision = 3,
            agent_id = run.Run.AgentId.ToString("D"),
            agent_revision = run.Run.AgentRevision,
            usage_units = 12,
            cost_units = 1.25m,
            latency_ms = 17,
        };
        Assert.Equal(HttpStatusCode.OK, (await telemetry.PostAsJsonAsync("/api/operations/telemetry", metric)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await telemetry.PostAsJsonAsync("/api/operations/telemetry", metric)).StatusCode);
        using var crossTenant = factory.CreateInternalClient()
            .WithTenant(otherTenant).WithUser("workflow").WithRole("SYSTEM");
        Assert.Equal(HttpStatusCode.OK, (await crossTenant.PostAsJsonAsync(
            "/api/operations/telemetry",
            metric with { event_id = Guid.NewGuid() })).StatusCode);

        var metricsResponse = await admin.GetAsync("/api/admin/operations/metrics");
        var metricsText = await metricsResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, metricsResponse.StatusCode);
        Assert.DoesNotContain("private/evidence/ref", metricsText, StringComparison.Ordinal);
        Assert.DoesNotContain("reviewed break-glass", metricsText, StringComparison.Ordinal);
        var metrics = JsonNode.Parse(metricsText)!;
        Assert.Equal(4, metrics["release_gate"]!["audit_entries"]!.GetValue<int>());
        var multi = metrics["multi_agent"]!;
        Assert.Equal(2, multi["rollout_events"]!.GetValue<int>());
        Assert.Equal(2, multi["root_runs"]!.GetValue<int>());
        Assert.Equal(1, multi["child_runs"]!.GetValue<int>());
        Assert.Equal(1, multi["child_success"]!.GetValue<int>());
        Assert.Equal(1, multi["verifier_reject"]!.GetValue<int>());
        Assert.Equal(1, multi["repair_rounds"]!.GetValue<int>());
        Assert.Single(multi["agents"]!.AsArray());
        Assert.Single(multi["skills"]!.AsArray());
        Assert.Single(multi["tools"]!.AsArray());
        var node = Assert.Single(multi["nodes"]!.AsArray())!;
        Assert.Equal("approved_write", node["nodeId"]!.GetValue<string>());
        Assert.Equal(17, node["averageLatencyMs"]!.GetValue<long>());
        Assert.Equal(17, node["maxLatencyMs"]!.GetValue<long>());

        var comparison = await (await admin.GetAsync(
            "/api/admin/operations/version-comparison")).ReadJsonAsync();
        Assert.True(comparison["active_runs_keep_immutable_snapshot"]!.GetValue<bool>());
        Assert.Equal(2, comparison["revisions"]!.AsArray().Count);
        Assert.NotNull(comparison["selected_vs_previous"]);

        var inventoryResponse = await admin.GetAsync("/api/admin/operations/legacy-inventory");
        var inventoryText = await inventoryResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, inventoryResponse.StatusCode);
        Assert.DoesNotContain("token", inventoryText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private/evidence/ref", inventoryText, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync(
            "/api/admin/operations/regressions",
            new { suite = "d7-release-next", passed = false, evidence_ref = "private/evidence/next" })).StatusCode);
        using var staleKey = OverrideRequest();
        Assert.Equal(HttpStatusCode.Conflict, (await admin.SendAsync(staleKey)).StatusCode);

        using var otherAdmin = Client(factory, otherTenant, "operator-other", manage: true);
        var otherMetrics = await (await otherAdmin.GetAsync("/api/admin/operations/metrics")).ReadJsonAsync();
        Assert.Empty(otherMetrics["multi_agent"]!["agents"]!.AsArray());
        Assert.Empty(otherMetrics["multi_agent"]!["tools"]!.AsArray());
        Assert.Equal(0, otherMetrics["multi_agent"]!["root_runs"]!.GetValue<int>());

        Assert.Equal(2, roots.Length);
    }

    /// <summary>
    /// active_runs_keep_immutable_snapshot 的 false 等價類:GetVersionComparisonAsync 只在有
    /// execution_snapshot_canonical IS NULL 的 root 時回 false,而 orchestrator_run 的該欄是
    /// bytea NOT NULL —— 那種列根本插不進去。這支把「為什麼永遠是 true」釘成可執行證據:
    /// 少了不可變快照的 root 被 23502 擋在門外,快照完整旗標仍是 true。日後若有人放寬 NOT NULL,
    /// 這支會轉紅,提醒 false 分支重新變得可達、必須真的驗一次。
    /// </summary>
    [SkippableFact]
    public async Task D7_DapperHttp_VersionComparisonSnapshotIntegrityIsSchemaEnforced()
    {
        fixture.SkipIfUnavailable();

        var tenant = TenantPrefix + Guid.NewGuid().ToString("N");
        await using (var connection = await fixture.DataSource!.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO orchestrator_run
                  (id,tenant_id,user_id,caller_role,orchestrator_id,orchestrator_revision,conversation_id,
                   workflow_id,workflow_revision,execution_snapshot,execution_snapshot_canonical,snapshot_sha256,
                   request_sha256,idempotency_key_sha256,status,deadline_at)
                VALUES
                  (@id,@tenant,'owner','ADMIN',@orchestrator,1,'d7-snapshot-intact',@workflow,1,
                   '{}'::jsonb,decode('7b7d','hex'),@hash,@hash,@key,'running',now()+interval '1 hour');
                """,
                new
                {
                    id = Guid.NewGuid(),
                    tenant,
                    orchestrator = Guid.NewGuid(),
                    workflow = Guid.Parse(AgentDefaults.RuntimeWorkflowId),
                    hash = new string('a', 64),
                    key = new string('b', 64),
                });

            var violation = await Assert.ThrowsAsync<PostgresException>(async () => await connection.ExecuteAsync(
                """
                INSERT INTO orchestrator_run
                  (id,tenant_id,user_id,caller_role,orchestrator_id,orchestrator_revision,conversation_id,
                   workflow_id,workflow_revision,execution_snapshot,execution_snapshot_canonical,snapshot_sha256,
                   request_sha256,idempotency_key_sha256,status,deadline_at)
                VALUES
                  (@id,@tenant,'owner','ADMIN',@orchestrator,1,'d7-snapshot-missing',@workflow,1,
                   '{}'::jsonb,NULL,@hash,@hash,@key,'running',now()+interval '1 hour');
                """,
                new
                {
                    id = Guid.NewGuid(),
                    tenant,
                    orchestrator = Guid.NewGuid(),
                    workflow = Guid.Parse(AgentDefaults.RuntimeWorkflowId),
                    hash = new string('a', 64),
                    key = new string('c', 64),
                }));
            Assert.Equal(PostgresErrorCodes.NotNullViolation, violation.SqlState);
        }

        using var factory = new DapperOperationsFactory();
        using var admin = Client(factory, tenant, "operator", manage: true);
        var comparison = await (await admin.GetAsync(
            "/api/admin/operations/version-comparison")).ReadJsonAsync();
        Assert.True(comparison["active_runs_keep_immutable_snapshot"]!.GetValue<bool>());
        Assert.Single(comparison["revisions"]!.AsArray());
    }

    /// <summary>
    /// Agent 指標的 Failed 等價類:SQL 把 'failed' 與 'cancelled' 都算成 Failed,而既有大案只把
    /// run 推到 completed,失敗那一半從沒被填過。取 'cancelled' 當代表值(它是兩者中比較不直覺的
    /// 那個:被取消也計為失敗)。
    /// </summary>
    [SkippableFact]
    public async Task D7_DapperHttp_AgentMetricsCountCancelledRunAsFailed()
    {
        fixture.SkipIfUnavailable();

        var tenant = TenantPrefix + Guid.NewGuid().ToString("N");
        var run = await CreateRunAsync(tenant);
        await using (var connection = await fixture.DataSource!.OpenConnectionAsync())
        {
            Assert.Equal(1, await connection.ExecuteAsync(
                "UPDATE agent_run SET status='cancelled',completed_at=now() WHERE id=@id",
                new { id = run.Run!.Id }));
        }

        using var factory = new DapperOperationsFactory();
        using var admin = Client(factory, tenant, "operator", manage: true);
        var metrics = await (await admin.GetAsync("/api/admin/operations/metrics")).ReadJsonAsync();

        var agent = Assert.Single(metrics["multi_agent"]!["agents"]!.AsArray())!;
        Assert.Equal(run.Run.AgentId.ToString("D"), agent["agentId"]!.GetValue<string>());
        Assert.Equal(1, agent["runs"]!.GetValue<int>());
        Assert.Equal(0, agent["completed"]!.GetValue<int>());
        Assert.Equal(1, agent["failed"]!.GetValue<int>());
    }

    /// <summary>
    /// 少了 workflow.manage 的呼叫者:Dapper 這側的拒絕路徑(既有大案一律帶 capability,等於沒驗過)。
    /// 決策表兩半都要:403 + ApiError 形狀,以及被擋下的寫入請求在真 DB 一列都不能留。
    /// </summary>
    [SkippableFact]
    public async Task D7_DapperHttp_MissingManageCapabilityIsForbiddenAndWritesNothing()
    {
        fixture.SkipIfUnavailable();

        var tenant = TenantPrefix + Guid.NewGuid().ToString("N");
        using var factory = new DapperOperationsFactory();
        using var denied = Client(factory, tenant, "ordinary", manage: false);

        var read = await denied.GetAsync("/api/admin/operations/metrics");
        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        var error = await read.ReadJsonAsync();
        Assert.Equal(403, error["status"]!.GetValue<int>());
        Assert.Equal("workflow.manage capability is required", error["message"]!.GetValue<string>());
        Assert.Empty(error["fieldErrors"]!.AsObject());

        var write = await denied.PostAsJsonAsync(
            "/api/admin/operations/regressions",
            new { suite = "d7-denied", passed = false, evidence_ref = "private/evidence/denied" });
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);

        await using var connection = await fixture.DataSource!.OpenConnectionAsync();
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM operations_regression_result WHERE tenant_id=@tenant",
            new { tenant }));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM operations_release_audit WHERE tenant_id=@tenant",
            new { tenant }));
    }

    /// <summary>租戶前綴清理:依外鍵相依由葉往根刪,讓共用的 springaitest 不留 D7 殘列。</summary>
    private async Task CleanupAsync()
    {
        if (!fixture.Available) return;
        await using var connection = await fixture.DataSource!.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            DELETE FROM operations_execution_metric WHERE tenant_id LIKE @prefix;
            DELETE FROM agent_run_event WHERE run_id IN (SELECT id FROM agent_run WHERE tenant_id LIKE @prefix);
            DELETE FROM agent_run_command WHERE run_id IN (SELECT id FROM agent_run WHERE tenant_id LIKE @prefix);
            DELETE FROM agent_run_skill WHERE run_id IN (SELECT id FROM agent_run WHERE tenant_id LIKE @prefix);
            DELETE FROM orchestrator_run_child WHERE orchestrator_root_run_id IN (SELECT id FROM orchestrator_run WHERE tenant_id LIKE @prefix);
            DELETE FROM agent_run WHERE tenant_id LIKE @prefix;
            DELETE FROM orchestrator_run_event WHERE run_id IN (SELECT id FROM orchestrator_run WHERE tenant_id LIKE @prefix);
            DELETE FROM orchestrator_run_command WHERE run_id IN (SELECT id FROM orchestrator_run WHERE tenant_id LIKE @prefix);
            DELETE FROM orchestrator_run WHERE tenant_id LIKE @prefix;
            DELETE FROM agent_revision_skill WHERE agent_id IN (SELECT id FROM agent WHERE tenant_id LIKE @prefix);
            DELETE FROM agent_revision WHERE agent_id IN (SELECT id FROM agent WHERE tenant_id LIKE @prefix);
            DELETE FROM agent WHERE tenant_id LIKE @prefix;
            DELETE FROM operations_regression_override WHERE tenant_id LIKE @prefix;
            DELETE FROM operations_regression_result WHERE tenant_id LIKE @prefix;
            DELETE FROM operations_release_audit WHERE tenant_id LIKE @prefix;
            DELETE FROM tenant_runtime_binding WHERE tenant_id LIKE @prefix;
            """,
            new { prefix = TenantPrefix + "%" });
    }

    private async Task<AgentRunWriteResult> CreateRunAsync(string tenant)
    {
        var definition = AgentCanonicalizer.Canonicalize(new AgentUpsert(
            Slug: null,
            Name: null,
            Description: null,
            SystemPrompt: "D7 metrics fixture",
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
            "d7-metrics-" + Guid.NewGuid().ToString("N"),
            "D7 metrics",
            "fixture",
            definition,
            hash,
            "operator",
            default);
        Assert.NotNull(agent);
        Assert.True(await agents.MarkValidatedAsync(
            tenant, agent!.Id, agent.DraftVersion, definition, hash, default));
        Assert.Equal(
            AgentWriteStatus.Success,
            (await agents.PublishAsync(
                tenant, agent.Id, agent.DraftVersion, definition, hash, "operator", default)).Status);
        var created = await new AgentRunRepository(fixture.DataSource!).CreateDirectAsync(
            tenant, "owner", "ADMIN", agent.Id, "metrics", "d7-metrics-run", default);
        Assert.Equal(AgentRunWriteStatus.Success, created.Status);
        return created;
    }

    private async Task<Guid[]> SeedRootMetricsAsync(string tenant, Guid agentRunId)
    {
        var root1 = Guid.NewGuid();
        var root2 = Guid.NewGuid();
        await using var connection = await fixture.DataSource!.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO orchestrator_run
              (id,tenant_id,user_id,caller_role,orchestrator_id,orchestrator_revision,conversation_id,
               workflow_id,workflow_revision,execution_snapshot,execution_snapshot_canonical,snapshot_sha256,
               request_sha256,idempotency_key_sha256,status,deadline_at,completed_at)
            VALUES
              (@root1,@tenant,'owner','ADMIN',@orchestrator,1,'d7-root-1',@workflow,1,
               '{}'::jsonb,decode('7b7d','hex'),@hash,@hash,@request1,'completed',now()+interval '1 hour',now()),
              (@root2,@tenant,'owner','ADMIN',@orchestrator,2,'d7-root-2',@workflow,1,
               '{}'::jsonb,decode('7b7d','hex'),@hash,@hash,@request2,'running',now()+interval '1 hour',NULL);
            INSERT INTO orchestrator_run_event(run_id,sequence,event_type,snapshot_sha256,payload)
              VALUES(@root1,1,'verification_completed',@hash,'{"verdicts":["NEEDS_REPAIR"]}'::jsonb);
            INSERT INTO orchestrator_run_child
              (id,orchestrator_root_run_id,task_id,attempt,run_kind,agent_id,agent_revision,
               workflow_id,workflow_revision,agent_snapshot_sha256,agent_run_id,dispatch_artifact,status)
              VALUES(@child,@root1,'task-1',1,'worker',@agent,1,@workflow,1,@hash,@agentRun,
               '{}'::jsonb,'completed');
            UPDATE agent_run SET orchestrator_root_run_id=@root1,status='completed',completed_at=now()
              WHERE id=@agentRun;
            INSERT INTO agent_run_event
              (run_id,sequence,event_id,event_type,node_id,snapshot_sha256,payload)
              SELECT id,(SELECT COALESCE(max(sequence),0)+1 FROM agent_run_event WHERE run_id=@agentRun),
                     @event,'node_completed','approved_write',snapshot_sha256,'{}'::jsonb
              FROM agent_run WHERE id=@agentRun;
            """,
            new
            {
                root1,
                root2,
                tenant,
                orchestrator = Guid.NewGuid(),
                workflow = Guid.Parse(AgentDefaults.RuntimeWorkflowId),
                hash = new string('a', 64),
                request1 = new string('b', 64),
                request2 = new string('c', 64),
                child = Guid.NewGuid(),
                agentRun = agentRunId,
                agent = (await connection.ExecuteScalarAsync<Guid>(
                    "SELECT agent_id FROM agent_run WHERE id=@agentRun",
                    new { agentRun = agentRunId })),
                @event = Guid.NewGuid(),
            });
        return [root1, root2];
    }

    private static HttpClient Client(
        TestWebAppFactory factory,
        string tenant,
        string user,
        bool manage)
    {
        var client = factory.CreateInternalClient()
            .WithTenant(tenant)
            .WithUser(user)
            .WithRole("SYSTEM_ADMIN");
        if (manage)
            client.DefaultRequestHeaders.Add("X-User-Capabilities", "workflow.manage");
        return client;
    }

    private sealed class DapperOperationsFactory : TestWebAppFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IOperationsGovernanceRepository>();
                services.AddScoped<IOperationsGovernanceRepository, OperationsGovernanceRepository>();
                services.RemoveAll<Backend.Api.RuntimeDiscovery.IRuntimeBindingRepository>();
                services.AddScoped<
                    Backend.Api.RuntimeDiscovery.IRuntimeBindingRepository,
                    Backend.Api.RuntimeDiscovery.RuntimeBindingRepository>();
            });
        }
    }
}
