using System.Text.Json;
using Dapper;
using Npgsql;

namespace Backend.Api.RunDiscovery;

/// <summary>
/// PostgreSQL authority for the O2 unified list. Combines <c>agent_run</c> (direct-agent/worker/
/// verifier) and <c>orchestrator_run</c> (orchestrator root) with a UNION ALL CTE, following the
/// same cross-table combination shape as <c>CheckpointRetentionRepository</c>. Both CTE branches
/// are always tenant+owner scoped; the agent_run branch additionally short-circuits to empty when
/// the caller is not ADMIN (see <see cref="IRunDiscoveryRepository"/>).
/// </summary>
public sealed class RunDiscoveryRepository(NpgsqlDataSource dataSource) : IRunDiscoveryRepository
{
    private const string Sql = """
        WITH agent_items AS (
            SELECT
                a.id AS Id,
                a.run_kind AS Kind,
                a.status AS Status,
                a.orchestrator_root_run_id AS OrchestratorRootRunId,
                a.task_id AS TaskId,
                a.agent_id AS AgentId,
                a.agent_revision AS AgentRevision,
                NULL::uuid AS OrchestratorId,
                NULL::int AS OrchestratorRevision,
                a.workflow_id AS WorkflowId,
                a.workflow_revision AS WorkflowRevision,
                (a.cancel_requested_at IS NOT NULL) AS CancelRequested,
                (a.execution_snapshot->'agent'->'runtime_limits')::text AS BudgetSummary,
                (SELECT e.event_type FROM agent_run_event e WHERE e.run_id = a.id ORDER BY e.sequence DESC LIMIT 1) AS LastEventType,
                (SELECT e.created_at FROM agent_run_event e WHERE e.run_id = a.id ORDER BY e.sequence DESC LIMIT 1) AS LastEventAt,
                NULL::text AS ChildProgress,
                a.error_code AS ErrorClass,
                (a.status = 'waiting_approval') AS PendingApproval,
                (a.status IN ('queued','running') AND a.lease_expires_at IS NOT NULL AND a.lease_expires_at < now()) AS NeedsRecovery,
                a.started_at AS StartedAt,
                a.created_at AS CreatedAt,
                a.updated_at AS UpdatedAt,
                a.completed_at AS CompletedAt
            FROM agent_run a
            WHERE a.tenant_id = @tenantId AND a.user_id = @userId AND @includeAgentRuns::boolean
        ),
        orchestrator_items AS (
            SELECT
                r.id AS Id,
                'orchestrator' AS Kind,
                r.status AS Status,
                NULL::uuid AS OrchestratorRootRunId,
                NULL::text AS TaskId,
                NULL::uuid AS AgentId,
                NULL::int AS AgentRevision,
                r.orchestrator_id AS OrchestratorId,
                r.orchestrator_revision AS OrchestratorRevision,
                r.workflow_id AS WorkflowId,
                r.workflow_revision AS WorkflowRevision,
                (r.cancel_requested_at IS NOT NULL) AS CancelRequested,
                jsonb_build_object('limits', r.execution_snapshot->'limits', 'token_budget', r.execution_snapshot->'token_budget')::text AS BudgetSummary,
                (SELECT e.event_type FROM orchestrator_run_event e WHERE e.run_id = r.id ORDER BY e.sequence DESC LIMIT 1) AS LastEventType,
                (SELECT e.created_at FROM orchestrator_run_event e WHERE e.run_id = r.id ORDER BY e.sequence DESC LIMIT 1) AS LastEventAt,
                (SELECT jsonb_build_object(
                    'total', count(*),
                    'queued', count(*) FILTER (WHERE COALESCE(ac.status, c.status) = 'queued'),
                    'running', count(*) FILTER (WHERE COALESCE(ac.status, c.status) = 'running'),
                    'completed', count(*) FILTER (WHERE COALESCE(ac.status, c.status) = 'completed'),
                    'failed', count(*) FILTER (WHERE COALESCE(ac.status, c.status) = 'failed'),
                    'cancelled', count(*) FILTER (WHERE COALESCE(ac.status, c.status) = 'cancelled'))::text
                 -- COALESCE(ac.status, c.status) matches OrchestratorRunRepository.GetChildAsync's
                 -- own precedence: the live agent_run row is authoritative, orchestrator_run_child's
                 -- own status column is only a fallback cache that write paths don't keep in sync.
                 FROM orchestrator_run_child c LEFT JOIN agent_run ac ON ac.id = c.agent_run_id
                 WHERE c.orchestrator_root_run_id = r.id) AS ChildProgress,
                r.error_code AS ErrorClass,
                EXISTS (
                    SELECT 1 FROM orchestrator_run_child c
                    JOIN agent_run ac ON ac.id = c.agent_run_id
                    WHERE c.orchestrator_root_run_id = r.id AND ac.status = 'waiting_approval'
                ) AS PendingApproval,
                EXISTS (
                    SELECT 1 FROM orchestrator_run_command cm
                    WHERE cm.run_id = r.id AND cm.completed_at IS NULL
                      AND cm.claim_expires_at IS NOT NULL AND cm.claim_expires_at < now()
                ) AS NeedsRecovery,
                NULL::timestamptz AS StartedAt,
                r.created_at AS CreatedAt,
                r.updated_at AS UpdatedAt,
                r.completed_at AS CompletedAt
            FROM orchestrator_run r
            WHERE r.tenant_id = @tenantId AND r.user_id = @userId
        ),
        combined AS (
            SELECT * FROM agent_items
            UNION ALL
            SELECT * FROM orchestrator_items
        )
        SELECT * FROM combined
        WHERE (@kind::text IS NULL OR Kind = @kind)
          AND (@status::text IS NULL OR Status = @status)
          AND (@agentId::uuid IS NULL OR AgentId = @agentId::uuid)
          AND (@orchestratorId::uuid IS NULL OR OrchestratorId = @orchestratorId::uuid)
          AND (@createdFrom::timestamptz IS NULL OR CreatedAt >= @createdFrom::timestamptz)
          AND (@createdTo::timestamptz IS NULL OR CreatedAt <= @createdTo::timestamptz)
          AND (@pendingApproval::boolean IS NULL OR PendingApproval = @pendingApproval::boolean)
          AND (@needsRecovery::boolean IS NULL OR NeedsRecovery = @needsRecovery::boolean)
          AND (@cursorAt::timestamptz IS NULL OR (CreatedAt, Id) < (@cursorAt::timestamptz, @cursorId::uuid))
        ORDER BY CreatedAt DESC, Id DESC
        LIMIT @limit
        """;

    public async Task<IReadOnlyList<RunSummaryItem>> ListAsync(
        string tenantId,
        string userId,
        bool callerIsAdmin,
        RunDiscoveryFilter filter,
        RunDiscoveryPosition? position,
        int limit,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<Row>(new CommandDefinition(
            Sql,
            new
            {
                tenantId,
                userId,
                includeAgentRuns = callerIsAdmin,
                kind = filter.Kind,
                status = filter.Status,
                agentId = filter.AgentId,
                orchestratorId = filter.OrchestratorId,
                createdFrom = filter.CreatedFrom,
                createdTo = filter.CreatedTo,
                pendingApproval = filter.PendingApproval,
                needsRecovery = filter.NeedsRecovery,
                cursorAt = position?.CreatedAt,
                cursorId = position?.Id,
                limit,
            },
            cancellationToken: ct));
        return rows.Select(ToItem).ToArray();
    }

    private static RunSummaryItem ToItem(Row row)
    {
        var now = DateTime.UtcNow;
        var start = row.StartedAt ?? row.CreatedAt;
        var end = row.CompletedAt ?? now;
        return new RunSummaryItem(
            row.Id,
            row.Kind,
            row.Status,
            row.OrchestratorRootRunId,
            row.TaskId,
            row.AgentId,
            row.AgentRevision,
            row.OrchestratorId,
            row.OrchestratorRevision,
            row.WorkflowId,
            row.WorkflowRevision,
            row.CancelRequested,
            row.BudgetSummary is null ? EmptyObject : JsonDocument.Parse(row.BudgetSummary).RootElement.Clone(),
            row.LastEventType,
            row.LastEventAt,
            row.ChildProgress is null ? null : JsonSerializer.Deserialize<RunChildProgress>(row.ChildProgress),
            row.ErrorClass,
            row.PendingApproval,
            row.NeedsRecovery,
            row.StartedAt,
            row.CreatedAt,
            row.UpdatedAt,
            row.CompletedAt,
            Math.Max(0, (end - start).TotalSeconds));
    }

    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    private sealed record Row(
        Guid Id, string Kind, string Status, Guid? OrchestratorRootRunId, string? TaskId,
        Guid? AgentId, int? AgentRevision, Guid? OrchestratorId, int? OrchestratorRevision,
        Guid WorkflowId, int WorkflowRevision, bool CancelRequested, string? BudgetSummary,
        string? LastEventType, DateTime? LastEventAt, string? ChildProgress, string? ErrorClass,
        bool PendingApproval, bool NeedsRecovery, DateTime? StartedAt, DateTime CreatedAt,
        DateTime UpdatedAt, DateTime? CompletedAt);
}
