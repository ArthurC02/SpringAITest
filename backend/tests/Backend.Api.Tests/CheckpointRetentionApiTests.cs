using System.Net;
using System.Net.Http.Json;
using Backend.Api.CheckpointRetention;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Backend.Api.Tests;

public sealed class CheckpointRetentionApiTests : IClassFixture<CheckpointRetentionApiTests.Factory>
{
    private readonly Factory _factory;

    public CheckpointRetentionApiTests(Factory factory) => _factory = factory;

    [Fact]
    public async Task Candidates_RequiresOnlyInternalToken_AndCapsOpaquePagination()
    {
        var unauthorized = await _factory.CreateClient().GetAsync(CandidatesUri(limit: 1));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        var repository = _factory.Fake<RetentionFake>();
        repository.Rows =
        [
            Row("agent_thread", "d3", 1),
            Row("root_context", "d5-root", 2),
        ];
        using var client = _factory.CreateInternalClient();
        var response = await client.GetAsync(CandidatesUri(limit: 1));

        response.EnsureSuccessStatusCode();
        var body = await response.ReadJsonAsync();
        Assert.True(body["has_more"]!.GetValue<bool>());
        var cursor = body["next_cursor"]!.GetValue<string>();
        Assert.DoesNotContain("agent_thread", cursor, StringComparison.Ordinal);
        Assert.Single(body["items"]!.AsArray());
        Assert.Null(body["items"]![0]!["payload"]);
        Assert.Equal(2, repository.LastTake); // controller reads limit + 1
        Assert.Null(repository.LastCursor);

        repository.Rows = [];
        var next = await client.GetAsync(CandidatesUri(limit: 1) + $"&cursor={Uri.EscapeDataString(cursor)}");
        next.EnsureSuccessStatusCode();
        Assert.NotNull(repository.LastCursor);

        var tampered = cursor[..^1] + (cursor[^1] == 'A' ? 'B' : 'A');
        var rejected = await client.GetAsync(CandidatesUri(limit: 1) + $"&cursor={Uri.EscapeDataString(tampered)}");
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
    }

    [Fact]
    public async Task Ack_RejectsForgedCandidate_AndIsIdempotent()
    {
        var repository = _factory.Fake<RetentionFake>();
        repository.Rows = [Row("agent_thread", "d5-child", 7)];
        using var client = _factory.CreateInternalClient();
        var candidateResponse = await client.GetAsync(CandidatesUri(limit: 100));
        candidateResponse.EnsureSuccessStatusCode();
        var candidate = (await candidateResponse.ReadJsonAsync())["items"]![0]!["candidate_id"]!.GetValue<string>();

        var forged = candidate[..^1] + (candidate[^1] == 'A' ? 'B' : 'A');
        var rejected = await client.PostAsJsonAsync("/api/internal/checkpoint-retention/ack", new
        {
            candidate_id = forged,
            deleted_threads = 1,
            deleted_root_contexts = 0,
            evidence_ref = "evidence:test",
        });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Empty(repository.Acks);

        var request = new
        {
            candidate_id = candidate,
            deleted_threads = 1,
            deleted_root_contexts = 0,
            evidence_ref = "evidence:test",
        };
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/internal/checkpoint-retention/ack", request)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/internal/checkpoint-retention/ack", request)).StatusCode);
        Assert.Single(repository.Acks);
    }

    [Fact]
    public async Task Candidates_RejectsBatchAboveHardLimit()
    {
        using var client = _factory.CreateInternalClient();
        var response = await client.GetAsync(CandidatesUri(limit: 101));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static string CandidatesUri(int limit)
        => "/api/internal/checkpoint-retention/candidates"
           + $"?retentionBefore={Uri.EscapeDataString(DateTime.UtcNow.AddDays(-30).ToString("O"))}"
           + $"&recoveryBefore={Uri.EscapeDataString(DateTime.UtcNow.AddDays(-7).ToString("O"))}"
           + $"&limit={limit}";

    private static CheckpointRetentionRow Row(string kind, string source, int suffix)
        => new(
            kind == "agent_thread" ? 1 : 2,
            kind,
            source,
            $"tenant-{suffix}",
            $"user-{suffix}",
            Guid.Parse($"00000000-0000-0000-0000-{suffix:D12}"),
            new string('a', 64),
            suffix,
            "opaque-checkpoint-reference",
            DateTime.UtcNow.AddDays(-31).AddMinutes(suffix));

    public sealed class Factory : TestWebAppFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ICheckpointRetentionRepository>();
                services.AddSingleton<RetentionFake>();
                services.AddSingleton<ICheckpointRetentionRepository>(sp => sp.GetRequiredService<RetentionFake>());
            });
        }
    }

    public sealed class RetentionFake : ICheckpointRetentionRepository
    {
        private readonly Lock _gate = new();
        private readonly HashSet<(string Kind, Guid RunId)> _acks = [];

        public IReadOnlyList<CheckpointRetentionRow> Rows { get; set; } = [];
        public CheckpointRetentionPosition? LastCursor { get; private set; }
        public int LastTake { get; private set; }
        public IReadOnlyCollection<(string Kind, Guid RunId)> Acks => _acks;

        public Task<IReadOnlyList<CheckpointRetentionRow>> ListAsync(
            DateTime retentionBefore,
            DateTime recoveryBefore,
            CheckpointRetentionPosition? cursor,
            int take,
            CancellationToken ct)
        {
            LastCursor = cursor;
            LastTake = take;
            return Task.FromResult(Rows);
        }

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
}
