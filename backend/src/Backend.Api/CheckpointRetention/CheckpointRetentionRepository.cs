using Dapper;
using Npgsql;

namespace Backend.Api.CheckpointRetention;

public interface ICheckpointRetentionRepository
{
    Task<IReadOnlyList<CheckpointRetentionRow>> ListAsync(
        DateTime retentionBefore,
        DateTime recoveryBefore,
        CheckpointRetentionPosition? cursor,
        int take,
        CancellationToken ct);

    Task AckAsync(
        string kind,
        Guid runId,
        int deletedThreads,
        int deletedRootContexts,
        string? evidenceRef,
        CancellationToken ct);
}

public sealed class CheckpointRetentionRepository(NpgsqlDataSource dataSource)
    : ICheckpointRetentionRepository
{
    public async Task<IReadOnlyList<CheckpointRetentionRow>> ListAsync(
        DateTime retentionBefore,
        DateTime recoveryBefore,
        CheckpointRetentionPosition? cursor,
        int take,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<CheckpointRetentionRow>(new CommandDefinition(
            """
            WITH authority_candidates AS (
                SELECT 1 AS KindOrder, 'agent_thread' AS Kind,
                       CASE WHEN a.run_kind='direct-agent' THEN 'd3' ELSE 'd5-child' END AS Source,
                       a.tenant_id AS TenantId, a.user_id AS UserId, a.id AS RunId,
                       a.snapshot_sha256 AS SnapshotSha256,
                       a.lease_generation AS MaxGeneration,
                       a.checkpoint_ref AS CheckpointRef,
                       a.completed_at AS CompletedAt
                FROM agent_run a
                WHERE a.status IN ('completed','failed','cancelled')
                  AND a.completed_at IS NOT NULL
                  AND a.completed_at <= @retentionBefore
                  AND a.updated_at <= @recoveryBefore
                  AND (a.lease_expires_at IS NULL OR a.lease_expires_at <= @recoveryBefore)
                  AND NOT EXISTS (
                      SELECT 1 FROM agent_run_command c
                      WHERE c.run_id=a.id AND c.dispatch_completed_at IS NULL)
                  AND NOT EXISTS (
                      SELECT 1 FROM agent_run_approval p
                      WHERE p.run_id=a.id AND p.status='pending')
                UNION ALL
                SELECT 2, 'root_context', 'd5-root',
                       r.tenant_id, r.user_id, r.id, r.snapshot_sha256,
                       0, r.checkpoint_ref, r.completed_at
                FROM orchestrator_run r
                WHERE r.status IN ('completed','failed','cancelled','timed_out')
                  AND r.completed_at IS NOT NULL
                  AND r.completed_at <= @retentionBefore
                  AND r.updated_at <= @recoveryBefore
                  AND NOT EXISTS (
                      SELECT 1 FROM orchestrator_run_command c
                      WHERE c.run_id=r.id AND c.completed_at IS NULL)
            )
            SELECT c.*
            FROM authority_candidates c
            LEFT JOIN checkpoint_retention_ack a
              ON a.kind=c.Kind AND a.run_id=c.RunId
            WHERE a.run_id IS NULL
              AND (CAST(@cursorAt AS timestamptz) IS NULL
                   OR (c.CompletedAt,c.KindOrder,c.RunId)
                      > (CAST(@cursorAt AS timestamptz),CAST(@cursorKind AS integer),CAST(@cursorRunId AS uuid)))
            ORDER BY c.CompletedAt,c.KindOrder,c.RunId
            LIMIT @take
            """,
            new
            {
                retentionBefore,
                recoveryBefore,
                cursorAt = cursor?.CompletedAt,
                cursorKind = cursor?.Kind,
                cursorRunId = cursor?.RunId,
                take,
            },
            cancellationToken: ct));
        return rows.AsList();
    }

    public async Task AckAsync(
        string kind,
        Guid runId,
        int deletedThreads,
        int deletedRootContexts,
        string? evidenceRef,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO checkpoint_retention_ack(
                kind,run_id,deleted_threads,deleted_root_contexts,evidence_ref)
            VALUES(@kind,@runId,@deletedThreads,@deletedRootContexts,@evidenceRef)
            ON CONFLICT(kind,run_id) DO NOTHING
            """,
            new { kind, runId, deletedThreads, deletedRootContexts, evidenceRef },
            cancellationToken: ct));
    }
}

public sealed class InMemoryCheckpointRetentionRepository : ICheckpointRetentionRepository
{
    private readonly HashSet<(string Kind, Guid RunId)> _acks = [];
    private readonly Lock _gate = new();

    public Task<IReadOnlyList<CheckpointRetentionRow>> ListAsync(
        DateTime retentionBefore,
        DateTime recoveryBefore,
        CheckpointRetentionPosition? cursor,
        int take,
        CancellationToken ct)
        => Task.FromResult<IReadOnlyList<CheckpointRetentionRow>>([]);

    public Task AckAsync(
        string kind,
        Guid runId,
        int deletedThreads,
        int deletedRootContexts,
        string? evidenceRef,
        CancellationToken ct)
    {
        lock (_gate)
        {
            _acks.Add((kind, runId));
        }
        return Task.CompletedTask;
    }
}
