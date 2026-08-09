using Backend.Api.Data.InMemory;
using Backend.Api.OrchestratorRuns;
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

/// <summary>
/// Lite-mode parity implementation. Candidate scanning lives in the repositories that own the
/// data, so this type never reads another aggregate's state directly.
/// </summary>
public sealed class InMemoryCheckpointRetentionRepository(
    InMemoryAgentRunRepository agentRuns,
    InMemoryOrchestratorRunRepository orchestratorRuns,
    InMemoryAgentRunApprovalRepository approvals) : ICheckpointRetentionRepository
{
    private readonly HashSet<(string Kind, Guid RunId)> _acks = [];
    private readonly Lock _gate = new();

    public async Task<IReadOnlyList<CheckpointRetentionRow>> ListAsync(
        DateTime retentionBefore,
        DateTime recoveryBefore,
        CheckpointRetentionPosition? cursor,
        int take,
        CancellationToken ct)
    {
        // 鎖的方向是單一且不巢狀的:先在**鎖外**向每個來源要一份值快照(各自只鎖自己的 gate),
        // 全部拿到之後才進本物件的 _gate 讀 _acks 快照,出鎖後才合併/過濾/排序。
        // 絕不可在持有 _gate 時呼叫任何來源 —— 那正是這組 in-memory 倉儲出過 ABBA 死鎖的形狀。
        var candidates = agentRuns.RetentionCandidates(retentionBefore, recoveryBefore).ToList();
        var pendingApprovalRuns = approvals.PendingApprovalRunIds();
        candidates.AddRange(
            await orchestratorRuns.RetentionCandidatesAsync(retentionBefore, recoveryBefore, ct));

        HashSet<(string Kind, Guid RunId)> acked;
        lock (_gate)
        {
            acked = [.. _acks];
        }

        return candidates
            .Where(row => !string.Equals(row.Kind, "agent_thread", StringComparison.Ordinal)
                || !pendingApprovalRuns.Contains(row.RunId))
            .Where(row => !acked.Contains((row.Kind, row.RunId)))
            .Where(row => cursor is null || Follows(row, cursor))
            .OrderBy(row => row.CompletedAt)
            .ThenBy(row => row.KindOrder)
            .ThenBy(row => row.RunId)
            .Take(take)
            .ToArray();
    }

    /// <summary>
    /// Keyset predicate over the same <c>(completed_at, kind_order, run_id)</c> triple the Dapper
    /// authority orders and compares by. Guid ordering is .NET's, not PostgreSQL's byte order —
    /// the cursor comparison and the sort use the same comparer, so pages neither repeat nor skip.
    /// </summary>
    private static bool Follows(CheckpointRetentionRow row, CheckpointRetentionPosition cursor)
        => (row.CompletedAt, row.KindOrder, row.RunId)
            .CompareTo((cursor.CompletedAt, (int)cursor.Kind, cursor.RunId)) > 0;

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
