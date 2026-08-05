using Backend.Api.CheckpointRetention;
using Backend.Api.Data;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Backend.Api.Tests;

public sealed class CheckpointRetentionPostgresTests
{
    [SkippableFact]
    public async Task AuthorityQuery_ExcludesActiveRecentRecoveringAndUnfinished_AndAckIsRaceSafe()
    {
        var databaseName = $"retention_test_{Guid.NewGuid():N}";
        var baseConnectionString = Environment.GetEnvironmentVariable("DB_CONNECTION_STRING")
            ?? "Host=localhost;Port=5433;Username=postgres;Password=postgres;Database=springaitest";
        var adminBuilder = new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            Database = "postgres",
            Pooling = false,
        };

        try
        {
            await using var probe = new NpgsqlConnection(adminBuilder.ConnectionString);
            await probe.OpenAsync();
        }
        catch (Exception ex)
        {
            Skip.If(true, $"PostgreSQL unavailable for isolated retention test: {ex.Message}");
        }

        await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
        await admin.OpenAsync();
        await admin.ExecuteAsync($"CREATE DATABASE \"{databaseName}\"");
        try
        {
            var testBuilder = new NpgsqlConnectionStringBuilder(baseConnectionString)
            {
                Database = databaseName,
                Pooling = false,
            };
            await using var dataSource = NpgsqlDataSource.Create(testBuilder.ConnectionString);
            await DbBootstrap.RunAsync(dataSource, NullLogger.Instance);
            await SeedRootsAsync(dataSource);

            var repository = new CheckpointRetentionRepository(dataSource);
            var candidates = await repository.ListAsync(
                DateTime.UtcNow.AddDays(-30),
                DateTime.UtcNow.AddDays(-7),
                cursor: null,
                take: 100,
                CancellationToken.None);

            var eligible = Assert.Single(candidates);
            Assert.Equal(EligibleId, eligible.RunId);
            Assert.Equal("root_context", eligible.Kind);
            Assert.Equal("d5-root", eligible.Source);

            await Task.WhenAll(
                repository.AckAsync(eligible.Kind, eligible.RunId, 0, 1, "evidence:test", CancellationToken.None),
                repository.AckAsync(eligible.Kind, eligible.RunId, 0, 1, "evidence:test", CancellationToken.None));

            await using var connection = await dataSource.OpenConnectionAsync();
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM checkpoint_retention_ack WHERE kind='root_context' AND run_id=@id",
                new { id = EligibleId }));
            Assert.Empty(await repository.ListAsync(
                DateTime.UtcNow.AddDays(-30),
                DateTime.UtcNow.AddDays(-7),
                cursor: null,
                take: 100,
                CancellationToken.None));
        }
        finally
        {
            await admin.ExecuteAsync($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)");
        }
    }

    private static readonly Guid EligibleId = Guid.Parse("10000000-0000-0000-0000-000000000001");

    private static async Task SeedRootsAsync(NpgsqlDataSource dataSource)
    {
        var old = DateTime.UtcNow.AddDays(-40);
        var recent = DateTime.UtcNow.AddDays(-2);
        await using var connection = await dataSource.OpenConnectionAsync();
        const string insert =
            """
            INSERT INTO orchestrator_run(
                id,tenant_id,user_id,caller_role,orchestrator_id,orchestrator_revision,
                conversation_id,workflow_id,workflow_revision,execution_snapshot,
                execution_snapshot_canonical,snapshot_sha256,request_sha256,
                idempotency_key_sha256,status,deadline_at,updated_at,completed_at)
            VALUES(
                @id,'retention-tenant','retention-user','ADMIN',@orchestratorId,1,
                @conversationId,@workflowId,1,'{}'::jsonb,'{}'::bytea,repeat('a',64),
                repeat('b',64),@idempotency,@status,@deadline,@updatedAt,@completedAt)
            """;

        async Task Insert(Guid id, string status, DateTime updatedAt, DateTime? completedAt)
            => await connection.ExecuteAsync(insert, new
            {
                id,
                orchestratorId = Guid.NewGuid(),
                conversationId = $"conversation-{id:N}",
                workflowId = Guid.NewGuid(),
                idempotency = id.ToString("N").PadRight(64, '0'),
                status,
                deadline = old.AddDays(1),
                updatedAt,
                completedAt,
            });

        await Insert(EligibleId, "completed", old, old);
        await Insert(Guid.Parse("10000000-0000-0000-0000-000000000002"), "running", old, null);
        await Insert(Guid.Parse("10000000-0000-0000-0000-000000000003"), "waiting_input", old, null);
        await Insert(Guid.Parse("10000000-0000-0000-0000-000000000004"), "completed", recent, recent);
        await Insert(Guid.Parse("10000000-0000-0000-0000-000000000005"), "completed", recent, old);

        var unfinishedId = Guid.Parse("10000000-0000-0000-0000-000000000006");
        await Insert(unfinishedId, "completed", old, old);
        await connection.ExecuteAsync(
            """
            INSERT INTO orchestrator_run_command(run_id,command_type,completed_at)
            VALUES(@runId,'start',NULL)
            """,
            new { runId = unfinishedId });
    }
}
