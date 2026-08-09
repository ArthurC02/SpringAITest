using Backend.Api.Data;
using Backend.Api.Triggers;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Backend.Api.Tests;

/// <summary>
/// Dapper ↔ InMemory parity for the O5 trigger authority against a real appdb (backend/AGENTS.md
/// "Dapper ↔ InMemory parity is not automatic"): the two implementations are deliberately
/// independent, so the semantics <see cref="ITriggerRepository"/> documents — tenant scoping,
/// optimistic lock, exclusive claim, fenced completion, cancel cascade, keyset order — are asserted
/// here against the SQL side with exactly the expectations
/// <see cref="TriggerDispatchTests"/>/<see cref="TriggerApiTests"/> assert against the in-memory one.
/// </summary>
public sealed class TriggerRepositoryPostgresTests
{
    private static readonly Guid Target = Guid.Parse("c0000000-0000-4000-8000-000000000001");
    private static readonly DateTime Due = new(2030, 6, 1, 9, 0, 0, DateTimeKind.Utc);

    private static readonly TriggerPrincipal Principal =
        new("op-a", "ADMIN", ["ops"], ["workflow.manage"]);

    [SkippableFact]
    public async Task DapperAuthority_MatchesTheDocumentedTriggerSemantics()
    {
        var databaseName = $"trigger_test_{Guid.NewGuid():N}";
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
            Skip.If(true, $"PostgreSQL unavailable for isolated trigger test: {ex.Message}");
        }

        await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
        await admin.OpenAsync();
        await admin.ExecuteAsync($"CREATE DATABASE \"{databaseName}\"");
        try
        {
            var testBuilder = new NpgsqlConnectionStringBuilder(baseConnectionString)
            {
                Database = databaseName,
            };
            await using var dataSource = NpgsqlDataSource.Create(testBuilder.ConnectionString);
            await DbBootstrap.RunAsync(dataSource, NullLogger.Instance);
            var repository = new TriggerRepository(dataSource);

            await CreateAndCancel(repository, dataSource);
            await ClaimAndComplete(repository, dataSource);
            await DatabaseClockDecidesDue(repository, dataSource);
            await KeysetAndTenantScope(repository, dataSource);
        }
        finally
        {
            await admin.ExecuteAsync($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)");
        }
    }

    private static async Task CreateAndCancel(TriggerRepository repository, NpgsqlDataSource dataSource)
    {
        var created = await repository.CreateAsync("t", Principal, Input("cancel-me"), default);
        Assert.Equal(TriggerWriteStatus.Success, created.Status);
        var trigger = created.Trigger!;
        Assert.Equal(1, trigger.Version);
        Assert.Equal(TriggerStatuses.Scheduled, trigger.Status);
        // Canonical input mapping survives byte for byte. jsonb would have reordered the keys by
        // (length, bytes) and put "zz" first -- proof the column is text, as the canonical pin needs.
        Assert.Equal(Mapping, trigger.InputMappingCanonical);
        AssertPrincipal(trigger.Principal);

        // The one-shot occurrence is written in the same transaction with the derived stable id.
        var occurrence = Assert.Single((await repository.OccurrencesAsync("t", trigger.Id, null, 10, default))!);
        Assert.Equal(TriggerOccurrenceId.For(trigger.Id, Due), occurrence.Id);
        Assert.Equal(TriggerOccurrenceStatuses.Pending, occurrence.Status);

        Assert.Equal(
            TriggerWriteStatus.Duplicate,
            (await repository.CreateAsync("t", Principal, Input("cancel-me"), default)).Status);
        // Same name, other tenant: not a duplicate -- uniqueness is tenant-scoped.
        Assert.Equal(
            TriggerWriteStatus.Success,
            (await repository.CreateAsync("other", Principal, Input("cancel-me"), default)).Status);

        // Tenant scoping and the optimistic lock, before the successful cancel.
        Assert.Null(await repository.GetAsync("other", trigger.Id, default));
        Assert.Null(await repository.OccurrencesAsync("other", trigger.Id, null, 10, default));
        Assert.Equal(
            TriggerWriteStatus.NotFound,
            (await repository.CancelAsync("other", trigger.Id, 1, default)).Status);
        var stale = await repository.CancelAsync("t", trigger.Id, 99, default);
        Assert.Equal(TriggerWriteStatus.VersionConflict, stale.Status);
        Assert.Equal(1, stale.CurrentVersion);

        var cancelled = await repository.CancelAsync("t", trigger.Id, 1, default);
        Assert.Equal(TriggerWriteStatus.Success, cancelled.Status);
        Assert.Equal(TriggerStatuses.Cancelled, cancelled.Trigger!.Status);
        Assert.Equal(2, cancelled.Trigger.Version);
        Assert.Equal(
            TriggerOccurrenceStatuses.SkippedCancelled,
            Assert.Single((await repository.OccurrencesAsync("t", trigger.Id, null, 10, default))!).Status);
        // Cancel stops future claims: even with the occurrence made due from the database's own point
        // of view, the cancelled state keeps it out of the claim set.
        await MakeDueAsync(dataSource, trigger.Id);
        Assert.DoesNotContain(
            (await repository.ClaimDueAsync("w", 60, 10, default)).Claims,
            claim => claim.Trigger.Id == trigger.Id);

        Assert.Equal(
            TriggerWriteStatus.InvalidState,
            (await repository.CancelAsync("t", trigger.Id, 2, default)).Status);

        // §1's one-shot reschedule path is "cancel then recreate" (no in-place update route): the
        // cancelled row above must free its name back up, or that documented path 409s forever.
        var recreated = await repository.CreateAsync("t", Principal, Input("cancel-me"), default);
        Assert.Equal(TriggerWriteStatus.Success, recreated.Status);
        Assert.NotEqual(trigger.Id, recreated.Trigger!.Id);
        Assert.Equal(TriggerStatuses.Scheduled, recreated.Trigger.Status);
    }

    private static async Task ClaimAndComplete(TriggerRepository repository, NpgsqlDataSource dataSource)
    {
        var trigger = (await repository.CreateAsync("claim", Principal, Input("fire-me"), default)).Trigger!;
        await MakeDueAsync(dataSource, trigger.Id);

        // Concurrent pollers: at most one may ever lease the same occurrence.
        var claims = await Task.WhenAll(
            repository.ClaimDueAsync("worker-a", 60, 10, default),
            repository.ClaimDueAsync("worker-b", 60, 10, default));
        var leased = claims.SelectMany(x => x.Claims).Where(x => x.Trigger.Id == trigger.Id).ToArray();
        var claim = Assert.Single(leased);
        Assert.Equal(TriggerOccurrenceStatuses.Claimed, claim.Occurrence.Status);
        AssertPrincipal(claim.Trigger.Principal);

        // Fencing: only the token that still owns the lease may write the terminal outcome.
        Assert.False(await repository.CompleteOccurrenceAsync(
            claim.Occurrence.Id, "not-the-token", TriggerOccurrenceStatuses.Fired, Guid.NewGuid(), default));

        var rootRunId = Guid.NewGuid();
        Assert.True(await repository.CompleteOccurrenceAsync(
            claim.Occurrence.Id, claim.ClaimToken, TriggerOccurrenceStatuses.Fired, rootRunId, default));
        var fired = Assert.Single((await repository.OccurrencesAsync("claim", trigger.Id, null, 10, default))!);
        Assert.Equal(TriggerOccurrenceStatuses.Fired, fired.Status);
        Assert.Equal(rootRunId, fired.RootRunId);
        Assert.Equal(TriggerStatuses.Fired, (await repository.GetAsync("claim", trigger.Id, default))!.Status);

        // A completed occurrence is never re-leased, and the stale token cannot resurrect it.
        Assert.DoesNotContain(
            (await repository.ClaimDueAsync("worker-a", 60, 10, default)).Claims,
            x => x.Trigger.Id == trigger.Id);
        Assert.False(await repository.CompleteOccurrenceAsync(
            claim.Occurrence.Id, claim.ClaimToken, TriggerOccurrenceStatuses.SkippedMisfired, null, default));

        // The lease is never projected into a read model.
        await using var connection = await dataSource.OpenConnectionAsync();
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM agent_trigger_occurrence WHERE id=@id AND (claim_token_sha256 IS NOT NULL OR claim_owner IS NOT NULL)",
            new { id = claim.Occurrence.Id }));

        // An expired lease is reclaimable without operator action. Expiry is aged out through the
        // database clock (the lease column itself), never by handing the repository a later "now".
        var expiring = (await repository.CreateAsync("claim", Principal, Input("expire-me"), default)).Trigger!;
        await MakeDueAsync(dataSource, expiring.Id);
        var first = Assert.Single(
            (await repository.ClaimDueAsync("worker-a", 60, 10, default)).Claims,
            x => x.Trigger.Id == expiring.Id);
        Assert.DoesNotContain(
            (await repository.ClaimDueAsync("worker-b", 60, 10, default)).Claims,
            x => x.Trigger.Id == expiring.Id);
        await using (var aging = await dataSource.OpenConnectionAsync())
        {
            await aging.ExecuteAsync(
                "UPDATE agent_trigger_occurrence SET claim_expires_at=now() - interval '1 second' WHERE id=@id",
                new { id = first.Occurrence.Id });
        }

        var reclaimed = Assert.Single(
            (await repository.ClaimDueAsync("worker-b", 60, 10, default)).Claims,
            x => x.Trigger.Id == expiring.Id);
        Assert.Equal(first.Occurrence.Id, reclaimed.Occurrence.Id);
        Assert.NotEqual(first.ClaimToken, reclaimed.ClaimToken);
        // The superseded token can no longer complete it -- that is the fence.
        Assert.False(await repository.CompleteOccurrenceAsync(
            first.Occurrence.Id, first.ClaimToken, TriggerOccurrenceStatuses.Fired, Guid.NewGuid(), default));
    }

    /// <summary>
    /// The store owns "now": nothing is handed in, so due-ness, the lease expiry and the returned
    /// instant can only come from the database clock. Both occurrences below are seeded with SQL
    /// relative to <c>now()</c>, and every app-side timestamp in this file is year 2030 — so an
    /// implementation that leaked the host wall clock back in would either claim both rows or stamp
    /// a lease five years out, and each assertion here would fail.
    /// </summary>
    private static async Task DatabaseClockDecidesDue(TriggerRepository repository, NpgsqlDataSource dataSource)
    {
        var past = (await repository.CreateAsync("clock", Principal, Input("already-due"), default)).Trigger!;
        var future = (await repository.CreateAsync("clock", Principal, Input("not-yet-due"), default)).Trigger!;
        await using var connection = await dataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(
            "UPDATE agent_trigger_occurrence SET scheduled_for=now() - interval '1 second' WHERE trigger_id=@id",
            new { id = past.Id });
        await connection.ExecuteAsync(
            "UPDATE agent_trigger_occurrence SET scheduled_for=now() + interval '1 hour' WHERE trigger_id=@id",
            new { id = future.Id });

        var batch = await repository.ClaimDueAsync("clock-worker", 60, 10, default);
        var claim = Assert.Single(batch.Claims, x => x.Trigger.Id == past.Id);
        Assert.DoesNotContain(batch.Claims, x => x.Trigger.Id == future.Id);

        // The returned instant is the database's, not this process's: it is the same statement-stable
        // now() the row was stamped with, so it must equal updated_at exactly.
        Assert.Equal(claim.Occurrence.UpdatedAt, batch.Now);
        Assert.Equal(
            1,
            await connection.ExecuteScalarAsync<int>(
                """
                SELECT count(*) FROM agent_trigger_occurrence
                WHERE id=@id AND claim_expires_at > now() AND claim_expires_at <= now() + interval '60 seconds'
                """,
                new { id = claim.Occurrence.Id }));
    }

    /// <summary>
    /// Makes an occurrence due from the database's point of view. Tests can no longer say "pretend it
    /// is 2030" — the only clock the claim reads is the server's.
    /// </summary>
    private static async Task MakeDueAsync(NpgsqlDataSource dataSource, Guid triggerId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(
            "UPDATE agent_trigger_occurrence SET scheduled_for=now() - interval '1 minute' WHERE trigger_id=@triggerId",
            new { triggerId });
    }

    private static async Task KeysetAndTenantScope(TriggerRepository repository, NpgsqlDataSource dataSource)
    {
        var trigger = (await repository.CreateAsync("page", Principal, Input("paged"), default)).Trigger!;
        // A one-shot trigger only ever owns one occurrence, so the extra rows the keyset query is
        // paginated over are seeded directly -- this asserts the SQL ordering/cursor, not the API.
        await using (var connection = await dataSource.OpenConnectionAsync())
        {
            for (var i = 1; i <= 2; i++)
            {
                await connection.ExecuteAsync(
                    "INSERT INTO agent_trigger_occurrence(id,trigger_id,tenant_id,scheduled_for,status) VALUES(@id,@trigger,'page',@at,'fired')",
                    new { id = Guid.NewGuid(), trigger = trigger.Id, at = Due.AddMinutes(-i) });
            }
        }

        var first = await repository.OccurrencesAsync("page", trigger.Id, null, 2, default);
        Assert.Equal(2, first!.Count);
        Assert.True(first[0].ScheduledFor >= first[1].ScheduledFor);
        var cursor = new TriggerOccurrencePosition(first[^1].ScheduledFor, first[^1].Id);

        var second = await repository.OccurrencesAsync("page", trigger.Id, cursor, 2, default);
        Assert.Single(second!);
        Assert.Empty(first.Select(x => x.Id).Intersect(second!.Select(x => x.Id)));

        Assert.Equal(2, (await repository.ListAsync("claim", 100, default)).Count);
        Assert.Single(await repository.ListAsync("page", 100, default));
        Assert.Empty(await repository.ListAsync("nobody", 100, default));
    }

    /// <summary>The whole immutable grant snapshot round-trips, not just the created_by summary.</summary>
    private static void AssertPrincipal(TriggerPrincipal actual)
    {
        Assert.Equal(Principal.UserId, actual.UserId);
        Assert.Equal(Principal.Role, actual.Role);
        Assert.Equal(Principal.Groups, actual.Groups);
        Assert.Equal(Principal.Capabilities, actual.Capabilities);
    }

    /// <summary>Ordinal key order, which is what <see cref="TriggerInputMapping.Validate"/> produces.</summary>
    private const string Mapping = "{\"message\":\"go\",\"zz\":\"tail\"}";

    private static TriggerCreateInput Input(string name)
        => new(name, "", Target, 1, Mapping, Due, 300);
}
