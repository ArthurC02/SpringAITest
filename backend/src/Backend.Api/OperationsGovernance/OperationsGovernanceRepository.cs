using Dapper;
using Npgsql;
using Backend.Api.RuntimeDiscovery;
using System.Text.Json;

namespace Backend.Api.OperationsGovernance;

/// <summary>PostgreSQL implementation. No release decision is held in process memory.</summary>
public sealed class OperationsGovernanceRepository(NpgsqlDataSource dataSource) : IOperationsGovernanceRepository
{
    public async Task<RegressionGate> RecordRegressionAsync(string tenantId, string suite, bool passed, string evidenceRef, string actorId, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("INSERT INTO operations_regression_result(id,tenant_id,suite,passed,evidence_ref,recorded_by) VALUES(@id,@tenantId,@suite,@passed,@evidenceRef,@actorId); INSERT INTO operations_release_audit(tenant_id,kind,outcome,actor_id,detail) VALUES(@tenantId,'regression',@outcome,@actorId,@suite);", new { id, tenantId, suite, passed, evidenceRef, actorId, outcome = passed ? "passed" : "failed" }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return (await GateAsync(conn, tenantId, ct))!;
    }

    public async Task<RegressionGate?> GetCurrentGateAsync(string tenantId, CancellationToken ct)
    { await using var conn = await dataSource.OpenConnectionAsync(ct); return await GateAsync(conn, tenantId, ct); }

    public async Task<OverrideWriteResult> CreateOverrideAsync(string tenantId, Guid regressionId, string idempotencyKeyHash, string reason, string actorId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var latest = await conn.QuerySingleOrDefaultAsync<RegressionRow>(new CommandDefinition("SELECT id AS Id,passed AS Passed FROM operations_regression_result WHERE tenant_id=@tenantId ORDER BY recorded_at DESC,id DESC LIMIT 1 FOR UPDATE", new { tenantId }, tx, cancellationToken: ct));
        if (latest is null || latest.Id != regressionId) { await tx.CommitAsync(ct); return new(OverrideWriteStatus.GateChanged, await GateAsync(conn, tenantId, ct)); }
        if (latest.Passed) { await tx.CommitAsync(ct); return new(OverrideWriteStatus.NoLongerRequired, await GateAsync(conn, tenantId, ct)); }
        var inserted = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "INSERT INTO operations_regression_override(tenant_id,idempotency_key_sha256,regression_id,reason,actor_id)"
            + " VALUES(@tenantId,@idempotencyKeyHash,@regressionId,@reason,@actorId)"
            + " ON CONFLICT(tenant_id,idempotency_key_sha256) DO NOTHING RETURNING regression_id",
            new { tenantId, idempotencyKeyHash, regressionId, reason, actorId }, tx, cancellationToken: ct));
        if (inserted is null)
        {
            var existing = await conn.QuerySingleAsync<OverrideRow>(new CommandDefinition(
                "SELECT regression_id AS RegressionId,reason AS Reason FROM operations_regression_override"
                + " WHERE tenant_id=@tenantId AND idempotency_key_sha256=@idempotencyKeyHash",
                new { tenantId, idempotencyKeyHash }, tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
            return existing.RegressionId == regressionId
                && string.Equals(existing.Reason, reason, StringComparison.Ordinal)
                ? new(OverrideWriteStatus.Replay, await GateAsync(conn, tenantId, ct))
                : new(OverrideWriteStatus.GateChanged, await GateAsync(conn, tenantId, ct));
        }
        await conn.ExecuteAsync(new CommandDefinition("INSERT INTO operations_release_audit(tenant_id,kind,outcome,actor_id,detail) VALUES(@tenantId,'regression_override','accepted',@actorId,'reason recorded');", new { tenantId, actorId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return new(OverrideWriteStatus.Accepted, await GateAsync(conn, tenantId, ct));
    }

    public async Task<RolloutWriteStatus> ApplyRolloutAsync(string tenantId, TenantRuntimeBinding binding, string actorId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var gate = await conn.QuerySingleOrDefaultAsync<RegressionRow>(new CommandDefinition(
            "SELECT id AS Id,passed AS Passed FROM operations_regression_result"
            + " WHERE tenant_id=@tenantId ORDER BY recorded_at DESC,id DESC LIMIT 1 FOR UPDATE",
            new { tenantId }, tx, cancellationToken: ct));
        if (binding.Enabled && gate is { Passed: false })
        {
            var overridden = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS(SELECT 1 FROM operations_regression_override"
                + " WHERE tenant_id=@tenantId AND regression_id=@id)",
                new { tenantId, gate.Id }, tx, cancellationToken: ct));
            if (!overridden)
            {
                await tx.RollbackAsync(ct);
                return RolloutWriteStatus.RegressionBlocked;
            }
        }
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO tenant_runtime_binding"
            + "(tenant_id,enabled,default_orchestrator_id,default_orchestrator_revision,canary_user_ids,updated_at)"
            + " VALUES(@tenantId,@enabled,@id,@revision,@users::jsonb,clock_timestamp())"
            + " ON CONFLICT(tenant_id) DO UPDATE SET enabled=EXCLUDED.enabled,"
            + "default_orchestrator_id=EXCLUDED.default_orchestrator_id,"
            + "default_orchestrator_revision=EXCLUDED.default_orchestrator_revision,"
            + "canary_user_ids=EXCLUDED.canary_user_ids,updated_at=clock_timestamp();"
            + " INSERT INTO operations_release_audit(tenant_id,kind,outcome,actor_id,detail)"
            + " VALUES(@tenantId,@kind,@outcome,@actorId,@detail)",
            new
            {
                tenantId,
                enabled = binding.Enabled,
                id = binding.DefaultOrchestratorId,
                revision = binding.DefaultOrchestratorRevision,
                users = JsonSerializer.Serialize(binding.CanaryUserIds),
                kind = binding.Enabled ? "rollout" : "rollback",
                outcome = binding.Enabled ? "updated" : "accepted",
                actorId,
                detail = binding.DefaultOrchestratorRevision is int r ? $"revision:{r}" : "disabled",
            }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return RolloutWriteStatus.Applied;
    }

    public async Task RecordTelemetryAsync(string tenantId, OperationsTelemetry telemetry, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        // The run join is the tenant fence; an event cannot be adopted across tenants.
        await conn.ExecuteAsync(new CommandDefinition("INSERT INTO operations_execution_metric(tenant_id,run_id,event_id,kind,node_id,tool_name,skill_name,skill_revision,agent_id,agent_revision,usage_units,cost_units,latency_ms) SELECT @tenantId,@runId,@eventId,@kind,@nodeId,@toolName,@skillName,@skillRevision,@agentId,@agentRevision,@usageUnits,@costUnits,@latencyMs WHERE EXISTS(SELECT 1 FROM agent_run WHERE id=@runId AND tenant_id=@tenantId) ON CONFLICT(tenant_id,run_id,event_id) DO NOTHING", new { tenantId, telemetry.RunId, telemetry.EventId, telemetry.Kind, telemetry.NodeId, telemetry.ToolName, telemetry.SkillName, telemetry.SkillRevision, telemetry.AgentId, telemetry.AgentRevision, telemetry.UsageUnits, telemetry.CostUnits, telemetry.LatencyMs }, cancellationToken: ct));
    }

    public async Task RecordEvidenceAsync(string tenantId, RunEvidenceEnvelope e, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        // Root/child lineage, snapshot_sha256 and prompt_manifest_sha256 all come only from the
        // run's own authoritative rows -- never from the caller. snapshot_sha256 is the run's own
        // pinned value (WHERE-filtered against a caller-supplied assertion, fail closed on
        // mismatch); prompt_manifest_sha256 is resolved server-side from the exact published
        // agent_revision the run pinned (P1's authoritative column), so a caller-supplied value
        // could never even be wrong -- it is accepted on the wire but simply not read here.
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO operations_run_evidence"
            + "(tenant_id,run_id,event_id,root_run_id,child_id,kind,outcome,error_class,snapshot_sha256,"
            + "agent_id,agent_revision,orchestrator_revision,skill_name,skill_revision,"
            + "prompt_manifest_sha256,context_revision,role_view,policy_revision,"
            + "model_provider,model_deployment,model_id,model_fingerprint,model_settings_hash,"
            + "tool_name,tool_revision,node_id,trace_id,span_id,reserved_budget_units,"
            + "usage_units,cost_units,latency_ms,observation_quality,verifier_verdict,case_verdict,redaction_note)"
            + " SELECT @tenantId,@runId,@eventId,COALESCE(ch.orchestrator_root_run_id,NULLIF(ar.root_run_id,ar.id)),ch.id,"
            + "@kind,@outcome,@errorClass,ar.snapshot_sha256,"
            + "@agentId,@agentRevision,@orchestratorRevision,@skillName,@skillRevision,"
            + "arv.prompt_manifest_sha256,@contextRevision,@roleView,@policyRevision,"
            + "@modelProvider,@modelDeployment,@modelId,@modelFingerprint,@modelSettingsHash,"
            + "@toolName,@toolRevision,@nodeId,@traceId,@spanId,"
            + "NULLIF(ar.execution_snapshot->'agent'->'runtime_limits'->>'token_budget','')::bigint,"
            + "@usageUnits,@costUnits,@latencyMs,@observationQuality,@verifierVerdict,@caseVerdict,@redactionNote"
            + " FROM agent_run ar LEFT JOIN orchestrator_run_child ch ON ch.agent_run_id=ar.id"
            + " LEFT JOIN agent_revision arv ON arv.agent_id=ar.agent_id AND arv.revision=ar.agent_revision"
            + " WHERE ar.id=@runId AND ar.tenant_id=@tenantId AND (@snapshotSha256::text IS NULL OR ar.snapshot_sha256=@snapshotSha256)"
            + " ON CONFLICT(tenant_id,run_id,event_id) DO NOTHING",
            new
            {
                tenantId, runId = e.RunId, eventId = e.EventId, kind = e.Kind, outcome = e.Outcome, errorClass = e.ErrorClass,
                snapshotSha256 = e.SnapshotSha256, agentId = e.AgentId, agentRevision = e.AgentRevision,
                orchestratorRevision = e.OrchestratorRevision, skillName = e.SkillName, skillRevision = e.SkillRevision,
                contextRevision = e.ContextRevision, roleView = e.RoleView,
                policyRevision = e.PolicyRevision, modelProvider = e.ModelProvider, modelDeployment = e.ModelDeployment,
                modelId = e.ModelId, modelFingerprint = e.ModelFingerprint, modelSettingsHash = e.ModelSettingsHash,
                toolName = e.ToolName, toolRevision = e.ToolRevision, nodeId = e.NodeId, traceId = e.TraceId, spanId = e.SpanId,
                usageUnits = e.UsageUnits, costUnits = e.CostUnits, latencyMs = e.LatencyMs, observationQuality = e.ObservationQuality,
                verifierVerdict = e.VerifierVerdict, caseVerdict = e.CaseVerdict, redactionNote = e.RedactionNote,
            }, cancellationToken: ct));
    }

    public async Task<EvidenceReconcileSummary> GetEvidenceReconcileAsync(string tenantId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleAsync<ReconcileRow>(new CommandDefinition(
            "SELECT (SELECT count(*)::int FROM operations_execution_metric WHERE tenant_id=@tenantId) AS MetricEventCount,"
            + "(SELECT count(*)::int FROM operations_run_evidence WHERE tenant_id=@tenantId) AS EnvelopeEventCount,"
            + "(SELECT count(*)::int FROM operations_execution_metric m JOIN operations_run_evidence ev"
            + " ON ev.tenant_id=m.tenant_id AND ev.run_id=m.run_id AND ev.event_id=m.event_id"
            + " WHERE m.tenant_id=@tenantId AND (m.usage_units IS DISTINCT FROM ev.usage_units"
            + " OR m.cost_units IS DISTINCT FROM ev.cost_units OR m.latency_ms IS DISTINCT FROM ev.latency_ms)) AS MismatchedEventCount,"
            + "(SELECT count(*)::int FROM operations_run_evidence WHERE tenant_id=@tenantId AND observation_quality='unknown') AS UnknownObservationCount,"
            + "(SELECT sum(usage_units) FROM operations_run_evidence WHERE tenant_id=@tenantId AND observation_quality<>'unknown') AS MeasuredUsageUnitsSum",
            new { tenantId }, cancellationToken: ct));
        return new(row.MetricEventCount, row.EnvelopeEventCount, row.MismatchedEventCount, row.UnknownObservationCount, row.MeasuredUsageUnitsSum);
    }

    /// <summary>
    /// W2-02(e) 統計時間窗的裁切界線,一律由資料庫時鐘算出——和 <c>occurred_at</c>/<c>observed_at</c>/
    /// <c>created_at</c> 這些由 PostgreSQL 蓋章的欄位同一個時鐘,不把 .NET 主機的 wall clock 混進來。
    /// </summary>
    private const string WindowStart = "clock_timestamp()-make_interval(days=>@windowDays)";

    public async Task<OperationsMetrics> GetMetricsAsync(string tenantId, int windowDays, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        // 刻意不加窗:release gate 是「最新一筆迴歸結果」的單列讀取(LIMIT 1),不是隨資料量變慢的
        // 彙總;若把它加窗,一個超過 N 天沒跑迴歸的租戶會讀不到 gate,RegressionPassed 就會退回
        // 預設的 true —— 那是把效能調整變成 fail-open。
        var gate = await GateAsync(conn, tenantId, ct);
        var audit = await conn.QuerySingleAsync<AuditMetric>(new CommandDefinition("SELECT count(*)::int AS Entries,count(*) FILTER (WHERE kind IN ('rollout','rollback'))::int AS Rollouts FROM operations_release_audit WHERE tenant_id=@tenantId AND occurred_at>=" + WindowStart, new { tenantId, windowDays }, cancellationToken: ct));
        // root 相關的三個數字一律以 root run 自己的 created_at 裁切,所以一筆 run 的事件與 child 要嘛
        // 全在窗內、要嘛全在窗外,不會出現「事件計入但 run 沒計入」的半截統計。
        var roots = await conn.QuerySingleAsync<RootMetric>(new CommandDefinition("SELECT (SELECT count(*)::int FROM orchestrator_run WHERE tenant_id=@tenantId AND created_at>=" + WindowStart + ") AS Roots,count(*) FILTER (WHERE payload->'verdicts' ? 'NEEDS_REPAIR')::int AS VerifierRejected,(SELECT count(*)::int FROM orchestrator_run_child c JOIN orchestrator_run rr ON rr.id=c.orchestrator_root_run_id WHERE rr.tenant_id=@tenantId AND rr.created_at>=" + WindowStart + " AND c.attempt > 0)::int AS Repairs FROM orchestrator_run_event e JOIN orchestrator_run r ON r.id=e.run_id WHERE r.tenant_id=@tenantId AND r.created_at>=" + WindowStart, new { tenantId, windowDays }, cancellationToken: ct));
        var children = await conn.QuerySingleAsync<ChildMetric>(new CommandDefinition("SELECT count(*)::int AS Children,count(*) FILTER (WHERE status='completed')::int AS Succeeded FROM agent_run WHERE tenant_id=@tenantId AND orchestrator_root_run_id IS NOT NULL AND created_at>=" + WindowStart, new { tenantId, windowDays }, cancellationToken: ct));
        var effects = await conn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*)::int FROM agent_run_write_effect e JOIN agent_run r ON r.id=e.run_id WHERE r.tenant_id=@tenantId AND e.created_at>=" + WindowStart, new { tenantId, windowDays }, cancellationToken: ct));
        var agents = (await conn.QueryAsync<AgentMetricRow>(new CommandDefinition("SELECT r.agent_id::text AS AgentId,r.agent_revision AS Revision,count(*)::int AS Runs,count(*) FILTER (WHERE r.status='completed')::int AS Completed,count(*) FILTER (WHERE r.status IN ('failed','cancelled'))::int AS Failed,COALESCE(avg(EXTRACT(EPOCH FROM (COALESCE(r.completed_at,r.updated_at)-r.created_at))*1000),0)::bigint AS AverageLatencyMs,COALESCE(sum(NULLIF(r.execution_snapshot->'agent'->'runtime_limits'->>'token_budget','')::bigint),0)::bigint AS ReservedBudgetUnits,sum(m.ObservedUsageUnits)::bigint AS ObservedUsageUnits,sum(m.ObservedCostUnits) AS ObservedCostUnits,avg(m.ObservedLatencyMs)::bigint AS ObservedLatencyMs FROM agent_run r LEFT JOIN (SELECT run_id,sum(usage_units) AS ObservedUsageUnits,sum(cost_units) AS ObservedCostUnits,avg(latency_ms) AS ObservedLatencyMs FROM operations_execution_metric WHERE tenant_id=@tenantId AND observed_at>=" + WindowStart + " GROUP BY run_id) m ON m.run_id=r.id WHERE r.tenant_id=@tenantId AND r.created_at>=" + WindowStart + " GROUP BY r.agent_id,r.agent_revision ORDER BY r.agent_id,r.agent_revision", new { tenantId, windowDays }, cancellationToken: ct))).Select(x => new AgentRevisionMetric(x.AgentId, x.Revision, x.Runs, x.Completed, x.Failed, x.AverageLatencyMs, x.ReservedBudgetUnits, x.ObservedUsageUnits, x.ObservedCostUnits, x.ObservedLatencyMs)).ToArray();
        var skills = (await conn.QueryAsync<SkillMetricRow>(new CommandDefinition("SELECT skill_name AS Name,skill_revision AS Revision,count(DISTINCT run_id)::int AS Runs,avg(latency_ms)::bigint AS ObservedLatencyMs,sum(usage_units)::bigint AS ObservedUsageUnits,sum(cost_units) AS ObservedCostUnits,0::bigint AS ReservedBudgetUnits FROM operations_execution_metric WHERE tenant_id=@tenantId AND skill_name IS NOT NULL AND skill_revision IS NOT NULL AND observed_at>=" + WindowStart + " GROUP BY skill_name,skill_revision ORDER BY skill_name,skill_revision", new { tenantId, windowDays }, cancellationToken: ct))).Select(x => new SkillRevisionMetric(x.Name, x.Revision, x.Runs, x.ObservedLatencyMs, x.ObservedUsageUnits, x.ObservedCostUnits, x.ReservedBudgetUnits)).ToArray();
        var tools = (await conn.QueryAsync<ToolMetricRow>(new CommandDefinition("SELECT tool_name AS Kind,count(*)::int AS Count,avg(latency_ms)::bigint AS ObservedLatencyMs,sum(usage_units)::bigint AS ObservedUsageUnits,sum(cost_units) AS ObservedCostUnits,0::bigint AS ReservedBudgetUnits FROM operations_execution_metric WHERE tenant_id=@tenantId AND kind='tool' AND tool_name IS NOT NULL AND observed_at>=" + WindowStart + " GROUP BY tool_name ORDER BY tool_name", new { tenantId, windowDays }, cancellationToken: ct))).Select(x => new ToolMetric(x.Kind, x.Count, x.ObservedLatencyMs, x.ObservedUsageUnits, x.ObservedCostUnits, x.ReservedBudgetUnits)).ToArray();
        var nodes = (await conn.QueryAsync<NodeMetricRow>(new CommandDefinition("SELECT node_id AS NodeId,count(*)::int AS Executions,COALESCE(avg(latency_ms),0)::bigint AS AverageLatencyMs,COALESCE(max(latency_ms),0)::bigint AS MaxLatencyMs FROM operations_execution_metric WHERE tenant_id=@tenantId AND node_id IS NOT NULL AND observed_at>=" + WindowStart + " GROUP BY node_id ORDER BY node_id", new { tenantId, windowDays }, cancellationToken: ct))).Select(x => new NodeMetric(x.NodeId, x.Executions, x.AverageLatencyMs, x.MaxLatencyMs)).ToArray();
        var aggregation = await conn.QuerySingleAsync<AggregateRow>(new CommandDefinition("SELECT count(*) FILTER (WHERE status='completed')::int AS Completed,count(*) FILTER (WHERE status <> 'completed')::int AS PartialOrFailed,COALESCE(avg((SELECT count(*) FROM orchestrator_run_child c WHERE c.orchestrator_root_run_id=r.id)),0)::int AS AverageFanOut,COALESCE(avg(EXTRACT(EPOCH FROM (COALESCE(completed_at,updated_at)-created_at))*1000),0)::bigint AS AverageLatencyMs FROM orchestrator_run r WHERE tenant_id=@tenantId AND created_at>=" + WindowStart, new { tenantId, windowDays }, cancellationToken: ct));
        return new(gate?.Passed ?? true, gate?.OverrideActive ?? false, audit.Entries, audit.Rollouts, roots.Roots, children.Children, children.Succeeded, roots.VerifierRejected, roots.Repairs, effects, agents, skills, tools, nodes, new RootAggregateMetric(aggregation.Completed, aggregation.PartialOrFailed, aggregation.AverageFanOut, aggregation.AverageLatencyMs));
    }

    public async Task<OperationsVersionComparison> GetVersionComparisonAsync(string tenantId, int? selectedRevision, int windowDays, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var events = await conn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*)::int FROM operations_release_audit WHERE tenant_id=@tenantId AND kind IN ('rollout','rollback') AND occurred_at>=" + WindowStart, new { tenantId, windowDays }, cancellationToken: ct));
        var revisions = (await conn.QueryAsync<RevisionMetricRow>(new CommandDefinition("SELECT orchestrator_revision AS Revision,count(*)::int AS Runs,count(*) FILTER (WHERE status='completed')::int AS Completed,count(*) FILTER (WHERE status IN ('failed','cancelled','timed_out'))::int AS Failed,COALESCE(avg(EXTRACT(EPOCH FROM (COALESCE(completed_at,updated_at)-created_at))*1000),0)::bigint AS AverageLatencyMs,COALESCE(sum(NULLIF(execution_snapshot->>'orchestrator_token_cap','')::bigint),0)::bigint AS ReservedCostUnits,count(*) FILTER (WHERE status IN ('queued','running','waiting_input'))::int AS ActiveRuns FROM orchestrator_run WHERE tenant_id=@tenantId AND created_at>=" + WindowStart + " GROUP BY orchestrator_revision ORDER BY orchestrator_revision", new { tenantId, windowDays }, cancellationToken: ct))).Select(x => new RevisionMetric(x.Revision,x.Runs,x.Completed,x.Failed,x.AverageLatencyMs,x.ReservedCostUnits,x.ActiveRuns)).ToArray();
        var selected = selectedRevision is int revision ? revisions.SingleOrDefault(x => x.Revision == revision) : null;
        var previous = selected is null ? null : revisions.Where(x => x.Revision < selected.Revision).OrderByDescending(x => x.Revision).FirstOrDefault();
        var delta = selected is not null && previous is not null ? new RevisionDelta(previous.Revision, selected.Revision, selected.Runs - previous.Runs, selected.Completed - previous.Completed, selected.AverageLatencyMs - previous.AverageLatencyMs, selected.ReservedBudgetUnits - previous.ReservedBudgetUnits) : null;
        // 刻意不加窗:這是「有沒有任何一筆 run 掉了不可變快照」的完整性斷言,不是彙總。加窗會讓窗外的
        // 損壞從 false 悄悄變成 true —— 同樣是把效能調整變成 fail-open。
        var snapshotsIntact = await conn.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT NOT EXISTS(SELECT 1 FROM orchestrator_run WHERE tenant_id=@tenantId AND execution_snapshot_canonical IS NULL)", new { tenantId }, cancellationToken: ct));
        return new(selectedRevision, events, selectedRevision is not null, snapshotsIntact, revisions, delta, windowDays);
    }

    public Task<IReadOnlyList<LegacyInventoryItem>> GetLegacyInventoryAsync(string tenantId, CancellationToken ct)
        => Task.FromResult(LegacyInventory.Items);

    private static async Task<RegressionGate?> GateAsync(NpgsqlConnection conn, string tenantId, CancellationToken ct)
    {
        var row = await conn.QuerySingleOrDefaultAsync<GateRow>(new CommandDefinition("SELECT r.id AS Id,r.passed AS Passed,r.suite AS Suite,r.recorded_at AS RecordedAt,EXISTS(SELECT 1 FROM operations_regression_override o WHERE o.tenant_id=r.tenant_id AND o.regression_id=r.id) AS OverrideActive,(SELECT count(*)::int FROM operations_release_audit a WHERE a.tenant_id=r.tenant_id) AS AuditEntries FROM operations_regression_result r WHERE r.tenant_id=@tenantId ORDER BY r.recorded_at DESC,r.id DESC LIMIT 1", new { tenantId }, cancellationToken: ct));
        return row is null ? null : new(row.Id, row.Passed, row.Suite, row.RecordedAt, row.OverrideActive, row.AuditEntries);
    }
    private sealed class RegressionRow { public Guid Id { get; init; } public bool Passed { get; init; } }
    private sealed class OverrideRow { public Guid RegressionId { get; init; } public string Reason { get; init; } = ""; }
    private sealed class GateRow { public Guid Id { get; init; } public bool Passed { get; init; } public string Suite { get; init; } = ""; public DateTime RecordedAt { get; init; } public bool OverrideActive { get; init; } public int AuditEntries { get; init; } }
    private sealed class AuditMetric { public int Entries { get; init; } public int Rollouts { get; init; } }
    private sealed class RootMetric { public int Roots { get; init; } public int VerifierRejected { get; init; } public int Repairs { get; init; } }
    private sealed class ChildMetric { public int Children { get; init; } public int Succeeded { get; init; } }
    private sealed class AgentMetricRow { public string AgentId { get; init; } = ""; public int Revision { get; init; } public int Runs { get; init; } public int Completed { get; init; } public int Failed { get; init; } public long AverageLatencyMs { get; init; } public long ReservedBudgetUnits { get; init; } public long? ObservedUsageUnits { get; init; } public decimal? ObservedCostUnits { get; init; } public long? ObservedLatencyMs { get; init; } }
    private sealed class SkillMetricRow { public string Name { get; init; } = ""; public int Revision { get; init; } public int Runs { get; init; } public long? ObservedLatencyMs { get; init; } public long? ObservedUsageUnits { get; init; } public decimal? ObservedCostUnits { get; init; } public long ReservedBudgetUnits { get; init; } }
    private sealed class ToolMetricRow { public string Kind { get; init; } = ""; public int Count { get; init; } public long? ObservedLatencyMs { get; init; } public long? ObservedUsageUnits { get; init; } public decimal? ObservedCostUnits { get; init; } public long ReservedBudgetUnits { get; init; } }
    private sealed class NodeMetricRow { public string NodeId { get; init; } = ""; public int Executions { get; init; } public long AverageLatencyMs { get; init; } public long MaxLatencyMs { get; init; } }
    private sealed class AggregateRow { public int Completed { get; init; } public int PartialOrFailed { get; init; } public int AverageFanOut { get; init; } public long AverageLatencyMs { get; init; } }
    private sealed class RevisionMetricRow { public int Revision { get; init; } public int Runs { get; init; } public int Completed { get; init; } public int Failed { get; init; } public long AverageLatencyMs { get; init; } public long ReservedCostUnits { get; init; } public int ActiveRuns { get; init; } }
    private sealed class ReconcileRow { public int MetricEventCount { get; init; } public int EnvelopeEventCount { get; init; } public int MismatchedEventCount { get; init; } public int UnknownObservationCount { get; init; } public long? MeasuredUsageUnitsSum { get; init; } }
}
