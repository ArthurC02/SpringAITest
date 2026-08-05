using Backend.Api.Conversations;
using Dapper;

namespace Backend.Api.Tests;

[Collection("Postgres")]
public sealed class ConversationRepositoryTests(PostgresFixture fixture)
{
    [SkippableFact]
    public async Task Page_Query_HasInMemoryParityForTiesAndConcurrentInsert()
    {
        fixture.SkipIfUnavailable();
        var tenant = "conversation-page-" + Guid.NewGuid().ToString("N");
        const string user = "user-a";
        var at = new DateTime(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc);
        await using var connection = await fixture.DataSource!.OpenConnectionAsync();
        try
        {
            var ids = new List<long>();
            for (var i = 0; i < 4; i++)
            {
                ids.Add(await connection.QuerySingleAsync<long>(
                    "INSERT INTO conversations (tenant_id,user_id,prompt,reply,created_at)"
                    + " VALUES (@tenant,@user,@prompt,@reply,@at) RETURNING id",
                    new { tenant, user, prompt = $"p{i}", reply = $"r{i}", at }));
            }

            var repo = new ConversationRepository(fixture.DataSource);
            var first = await repo.ListPageDescAsync(tenant, user, null, 3, default);
            Assert.Equal(ids.AsEnumerable().Reverse().Take(3), first.Select(item => item.Id));

            var inserted = await connection.QuerySingleAsync<long>(
                "INSERT INTO conversations (tenant_id,user_id,prompt,reply,created_at)"
                + " VALUES (@tenant,@user,'new','new',@at) RETURNING id",
                new { tenant, user, at = at.AddMinutes(1) });
            var next = await repo.ListPageDescAsync(
                tenant, user, new ConversationPosition(first[1].CreatedAt, first[1].Id), 3, default);
            Assert.Equal(ids.Take(2).Reverse(), next.Select(item => item.Id));
            Assert.DoesNotContain(inserted, next.Select(item => item.Id));
        }
        finally
        {
            await connection.ExecuteAsync(
                "DELETE FROM conversations WHERE tenant_id = @tenant", new { tenant });
        }
    }

    [SkippableFact]
    public async Task Bootstrap_PageIndex_MatchesKeysetWithoutIncludingReply()
    {
        fixture.SkipIfUnavailable();
        await using var connection = await fixture.DataSource!.OpenConnectionAsync();
        var definition = await connection.QuerySingleAsync<string>(
            "SELECT indexdef FROM pg_indexes WHERE schemaname='public'"
            + " AND indexname='conversations_history_page_idx'");

        Assert.Contains("tenant_id, user_id, created_at DESC, id DESC", definition);
        Assert.DoesNotContain("INCLUDE", definition, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reply", definition, StringComparison.OrdinalIgnoreCase);
    }
}
