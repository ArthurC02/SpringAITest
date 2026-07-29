using System.Net;
using System.Net.Http.Json;
using Backend.Api.OperationsGovernance;
using Microsoft.AspNetCore.Hosting;

namespace Backend.Api.Tests;

/// <summary>
/// E2/E3 durable eval suite/result authority against the InMemory repository + FakeEvalRunner (no
/// live DB/workflow required). Postgres-specific invariants (real unique constraints, the
/// CSR-EVAL-001 seed itself) are covered by <see cref="EvalGovernancePostgresApiTests"/>.
/// </summary>
public sealed class EvalGovernanceApiTests : IClassFixture<TestWebAppFactory>
{
    private const string SuiteId = "eval-test-suite";

    private readonly TestWebAppFactory _factory;
    public EvalGovernanceApiTests(TestWebAppFactory factory) => _factory = factory;

    [Theory]
    [InlineData("GET", "/api/admin/operations/eval-suites")]
    [InlineData("GET", "/api/admin/operations/eval-suites/csr-eval-001")]
    [InlineData("GET", "/api/admin/operations/eval-suites/csr-eval-001/revisions/1")]
    [InlineData("GET", "/api/admin/operations/eval-runs")]
    [InlineData("GET", "/api/admin/operations/eval-runs/00000000-0000-0000-0000-000000000001")]
    public async Task FlagOff_HidesEveryEvalRouteBeforeAuth(string method, string path)
    {
        // RUN_EVAL_ENABLED is unset on the shared factory (defaults false), while
        // AGENT_WRITE_TOOLS_ENABLED stays true -- isolates that *this* flag, not the pre-existing
        // D7 gate on the shared /api/admin/operations prefix, is what hides these routes. The gate
        // sits after InternalTokenMiddleware (same placement as the D4/D5/context flags), so a
        // valid internal token is still required to observe the 404 -- capability/tenant auth is
        // what stays bypassed.
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        var response = await _factory.CreateInternalClient().SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Run_IsIdempotentOnReplay_ConflictsOnMismatch_And502sWithNoHalfWriteWhenUnreachable()
    {
        using var factory = new EvalEnabledFactory();
        var evals = factory.Fake<IEvalRepository>();
        await evals.PublishSuiteRevisionAsync("eval-a", SuiteId, SuiteContent(requiredCaseIds: "case-a"), "system", default);
        using var admin = Client(factory, "eval-a", "operator", manage: true);
        var fakeRunner = (FakeEvalRunner)factory.Fake<IEvalRunner>();

        // Workflow unreachable -> 502, and nothing at all gets written (no pending/half run row).
        fakeRunner.SetupUnreachable();
        var unreachable = await CreateRunAsync(admin, SuiteId, 1, "skill", key: "run-key-1");
        Assert.Equal(HttpStatusCode.BadGateway, unreachable.StatusCode);
        Assert.Empty(await evals.ListRunsAsync("eval-a", default));

        // Workflow recovers -> the same idempotency key now succeeds and creates exactly one run.
        fakeRunner.ClearUnreachable();
        var first = await CreateRunAsync(admin, SuiteId, 1, "skill", key: "run-key-1");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstBody = await first.ReadJsonAsync();
        var runId = firstBody["id"]!.GetValue<Guid>();
        // candidate.ref round-trips as a real JSON object (RawJsonConverter), not an escaped string.
        Assert.Equal("candidate-under-test", firstBody["candidate"]!["ref"]!["name"]!.GetValue<string>());
        Assert.Equal("skill", firstBody["candidate"]!["kind"]!.GetValue<string>());
        Assert.Single(await evals.ListRunsAsync("eval-a", default));

        // Replay with the exact same request body/key -> same run, no duplicate row.
        var replay = await CreateRunAsync(admin, SuiteId, 1, "skill", key: "run-key-1");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(runId, (await replay.ReadJsonAsync())["id"]!.GetValue<Guid>());
        Assert.Single(await evals.ListRunsAsync("eval-a", default));

        // Same key, different candidate -> rejected as a conflict, still no second row.
        var conflict = await CreateRunAsync(admin, SuiteId, 1, "agent", key: "run-key-1");
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Single(await evals.ListRunsAsync("eval-a", default));
    }

    [Fact]
    public async Task Replay_ShortCircuitsBeforeInvokingTheRunnerASecondTime()
    {
        using var factory = new EvalEnabledFactory();
        var evals = factory.Fake<IEvalRepository>();
        await evals.PublishSuiteRevisionAsync("eval-replay-runner", SuiteId, SuiteContent(requiredCaseIds: "case-a"), "system", default);
        using var admin = Client(factory, "eval-replay-runner", "operator", manage: true);
        var fakeRunner = (FakeEvalRunner)factory.Fake<IEvalRunner>();

        var first = await CreateRunAsync(admin, SuiteId, 1, "skill", key: "replay-runner-key");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Single(fakeRunner.Calls);

        // Idempotency is resolved before the (expensive) runner call: a replay must not invoke it
        // again just to discard the duplicate result.
        var replay = await CreateRunAsync(admin, SuiteId, 1, "skill", key: "replay-runner-key");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Single(fakeRunner.Calls);
    }

    [Fact]
    public async Task CreateRun_OmittedBudgetMs_DefaultsToTheEvalRunnerTimeoutCap_NotUnbounded()
    {
        // Omitting budget_ms must not mean "no limit" -- a long-running (e.g. 200-case) suite would
        // then always hit the eval-runner HttpClient's own 300s timeout and get discarded wholesale.
        using var factory = new EvalEnabledFactory();
        var evals = factory.Fake<IEvalRepository>();
        await evals.PublishSuiteRevisionAsync("eval-budget-default", SuiteId, SuiteContent(requiredCaseIds: "case-a"), "system", default);
        using var admin = Client(factory, "eval-budget-default", "operator", manage: true);
        var fakeRunner = (FakeEvalRunner)factory.Fake<IEvalRunner>();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/operations/eval-runs")
        {
            Content = JsonContent.Create(new
            {
                suite_id = SuiteId,
                revision = 1,
                candidate = new { kind = "skill", @ref = new { name = "candidate-under-test" } },
                // budget_ms deliberately omitted
            }),
        };
        request.Headers.Add("Idempotency-Key", "budget-default-key");
        var response = await admin.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(300_000, Assert.Single(fakeRunner.Calls).BudgetMs);
    }

    [Fact]
    public async Task SuitesAndRuns_AreTenantIsolated()
    {
        using var factory = new EvalEnabledFactory();
        var evals = factory.Fake<IEvalRepository>();
        await evals.PublishSuiteRevisionAsync("eval-tenant-a", SuiteId, SuiteContent(requiredCaseIds: "case-a"), "system", default);
        using var ownerAdmin = Client(factory, "eval-tenant-a", "operator", manage: true);
        Assert.Equal(HttpStatusCode.OK, (await CreateRunAsync(ownerAdmin, SuiteId, 1, "skill", key: "tenant-isolation")).StatusCode);

        using var otherAdmin = Client(factory, "eval-tenant-b", "operator-b", manage: true);
        Assert.Equal(HttpStatusCode.NotFound, (await otherAdmin.GetAsync($"/api/admin/operations/eval-suites/{SuiteId}")).StatusCode);
        var otherSuites = await (await otherAdmin.GetAsync("/api/admin/operations/eval-suites")).ReadJsonAsync();
        Assert.Empty(otherSuites.AsArray());
        var otherRuns = await (await otherAdmin.GetAsync("/api/admin/operations/eval-runs")).ReadJsonAsync();
        Assert.Empty(otherRuns.AsArray());
        // Cross-tenant candidate resolution must fail closed too: the run cannot even be created,
        // because tenant B never published this suite_id.
        Assert.Equal(HttpStatusCode.NotFound, (await CreateRunAsync(otherAdmin, SuiteId, 1, "skill", key: "tenant-isolation-b")).StatusCode);
    }

    [Fact]
    public async Task Gate_RequiredCaseFail_And_StaleFreshness_BothFailClosed_WhileFreshAllPassSucceeds()
    {
        using var factory = new EvalEnabledFactory();
        var evals = factory.Fake<IEvalRepository>();
        // freshness_seconds=60s keeps the stale/fresh cases comfortably separated (30s vs 70s
        // margins) so real wall-clock jitter between setup and assertion can never flip the result.
        await evals.PublishSuiteRevisionAsync("eval-gate", SuiteId, SuiteContent(requiredCaseIds: "case-a", freshnessSeconds: 60), "system", default);
        using var admin = Client(factory, "eval-gate", "operator", manage: true);
        var fake = (FakeEvalRunner)factory.Fake<IEvalRunner>();

        // Regression `suite` below is always the real eval suite_id (SuiteId) unless a test is
        // specifically exercising the candidate/suite-identity mismatch axis, so each assertion
        // below isolates exactly one equivalence class (required-case, freshness, identity).

        // Required case fails -> gate must fail even though the run otherwise "completed".
        fake.Setup((suite, revision, cases) => new EvalRunResponseWire(
            "fake-runner-1", suite, revision, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            new[] { new EvalCaseResultWire("case-a", "id-a", "FAIL", null, "wrong skill routed") }));
        var failingRun = await CreateRunAsync(admin, SuiteId, 1, "skill", key: "gate-fail-required");
        var failingRunId = (await failingRun.ReadJsonAsync())["id"]!.GetValue<Guid>();
        Assert.False((await RecordRegressionAsync(admin, SuiteId, failingRunId)).regressionPassed);

        // All-required-PASS but stale (completed well beyond the 60s freshness window) -> fail.
        fake.Setup((suite, revision, cases) => new EvalRunResponseWire(
            "fake-runner-1", suite, revision,
            DateTimeOffset.UtcNow.AddSeconds(-70), DateTimeOffset.UtcNow.AddSeconds(-70),
            new[] { new EvalCaseResultWire("case-a", "id-a", "PASS", null, null) }));
        var staleRun = await CreateRunAsync(admin, SuiteId, 1, "skill", key: "gate-fail-stale");
        var staleRunId = (await staleRun.ReadJsonAsync())["id"]!.GetValue<Guid>();
        Assert.False((await RecordRegressionAsync(admin, SuiteId, staleRunId)).regressionPassed);

        // All-required-PASS and comfortably fresh -> gate passes.
        fake.Setup((suite, revision, cases) => new EvalRunResponseWire(
            "fake-runner-1", suite, revision,
            DateTimeOffset.UtcNow.AddSeconds(-30), DateTimeOffset.UtcNow.AddSeconds(-30),
            new[] { new EvalCaseResultWire("case-a", "id-a", "PASS", null, null) }));
        var passingRun = await CreateRunAsync(admin, SuiteId, 1, "skill", key: "gate-pass");
        var passingRunId = (await passingRun.ReadJsonAsync())["id"]!.GetValue<Guid>();
        Assert.True((await RecordRegressionAsync(admin, SuiteId, passingRunId)).regressionPassed);

        // Same passing/fresh run, but the regression's own `suite` label names a different suite ->
        // the candidate/suite-identity check must fail the gate even though cases and freshness are fine.
        Assert.False((await RecordRegressionAsync(admin, "a-different-suite-id", passingRunId)).regressionPassed);

        // Unknown eval_run_id is a caller error (400), not a silently-failed gate.
        using var unknown = new HttpRequestMessage(HttpMethod.Post, "/api/admin/operations/regressions")
        {
            Content = JsonContent.Create(new { suite = "gate-suite-missing", passed = true, evidence_ref = "e", eval_run_id = Guid.NewGuid() }),
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(unknown)).StatusCode);
    }

    [Fact]
    public async Task Gate_NonRequiredCaseFails_FailsClosed_EvenWhenEveryRequiredCasePasses()
    {
        using var factory = new EvalEnabledFactory();
        var evals = factory.Fake<IEvalRepository>();
        // "case-a" is the only required case; "case-b" exists in the suite but is not required.
        await evals.PublishSuiteRevisionAsync(
            "eval-gate-nonreq", SuiteId, SuiteContentWithCases(new[] { "case-a" }, new[] { "case-a", "case-b" }), "system", default);
        using var admin = Client(factory, "eval-gate-nonreq", "operator", manage: true);
        var fake = (FakeEvalRunner)factory.Fake<IEvalRunner>();
        fake.Setup((suite, revision, cases) => new EvalRunResponseWire(
            "fake-runner-1", suite, revision, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            new[]
            {
                new EvalCaseResultWire("case-a", "id-a", "PASS", null, null),
                new EvalCaseResultWire("case-b", "id-b", "FAIL", null, "unrelated regression"),
            }));

        var run = await CreateRunAsync(admin, SuiteId, 1, "skill", key: "gate-nonreq-fail");
        var runId = (await run.ReadJsonAsync())["id"]!.GetValue<Guid>();
        Assert.False((await RecordRegressionAsync(admin, SuiteId, runId)).regressionPassed);
    }

    [Fact]
    public async Task Gate_EmptyRequiredCaseIds_FailsClosed_EvenWhenEveryCasePasses()
    {
        using var factory = new EvalEnabledFactory();
        var evals = factory.Fake<IEvalRepository>();
        await evals.PublishSuiteRevisionAsync(
            "eval-gate-norequired", SuiteId, SuiteContentWithCases(Array.Empty<string>(), new[] { "case-a" }), "system", default);
        using var admin = Client(factory, "eval-gate-norequired", "operator", manage: true);
        // FakeEvalRunner's default (no .Setup) script returns every case PASS, fresh "now" timestamps.

        var run = await CreateRunAsync(admin, SuiteId, 1, "skill", key: "gate-empty-required");
        var runId = (await run.ReadJsonAsync())["id"]!.GetValue<Guid>();
        Assert.False((await RecordRegressionAsync(admin, SuiteId, runId)).regressionPassed);
    }

    [Fact]
    public async Task CallerSuppliedPassed_StaysUnchanged_WhenEvalRunIdIsOmitted()
    {
        using var factory = new EvalEnabledFactory();
        using var admin = Client(factory, "eval-legacy", "operator", manage: true);

        var truthy = await admin.PostAsJsonAsync("/api/admin/operations/regressions", new { suite = "legacy-suite", passed = true, evidence_ref = "e1" });
        Assert.True((await truthy.ReadJsonAsync())["regression_passed"]!.GetValue<bool>());

        var falsy = await admin.PostAsJsonAsync("/api/admin/operations/regressions", new { suite = "legacy-suite-2", passed = false, evidence_ref = "e2" });
        Assert.False((await falsy.ReadJsonAsync())["regression_passed"]!.GetValue<bool>());
    }

    private static async Task<HttpResponseMessage> CreateRunAsync(
        HttpClient client, string suiteId, int revision, string candidateKind, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/operations/eval-runs")
        {
            Content = JsonContent.Create(new
            {
                suite_id = suiteId,
                revision,
                candidate = new { kind = candidateKind, @ref = new { name = "candidate-under-test" } },
            }),
        };
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }

    private static async Task<(bool regressionPassed, int auditEntries)> RecordRegressionAsync(
        HttpClient client, string suite, Guid evalRunId)
    {
        var response = await client.PostAsJsonAsync(
            "/api/admin/operations/regressions",
            new { suite, passed = true, evidence_ref = "eval-gate-evidence", eval_run_id = evalRunId });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.ReadJsonAsync();
        return (body["regression_passed"]!.GetValue<bool>(), body["audit_entries"]!.GetValue<int>());
    }

    private static string SuiteContent(string requiredCaseIds, int freshnessSeconds = 86_400)
        => "{\"policy\":{\"required_case_ids\":[\"" + requiredCaseIds + "\"],\"freshness_seconds\":" + freshnessSeconds + "},"
           + "\"cases\":[{\"case_id\":\"" + requiredCaseIds + "\",\"mode\":\"deterministic\","
           + "\"input\":{\"message\":\"m\"},\"expected\":{\"category\":\"retrieval\"}}]}";

    private static string SuiteContentWithCases(
        IReadOnlyList<string> requiredCaseIds, IReadOnlyList<string> allCaseIds, int freshnessSeconds = 86_400)
    {
        var required = string.Join(',', requiredCaseIds.Select(id => "\"" + id + "\""));
        var cases = string.Join(',', allCaseIds.Select(id =>
            "{\"case_id\":\"" + id + "\",\"mode\":\"deterministic\",\"input\":{\"message\":\"m\"},\"expected\":{\"category\":\"retrieval\"}}"));
        return "{\"policy\":{\"required_case_ids\":[" + required + "],\"freshness_seconds\":" + freshnessSeconds + "},"
               + "\"cases\":[" + cases + "]}";
    }

    private static HttpClient Client(TestWebAppFactory factory, string tenant, string user, bool manage)
    {
        var client = factory.CreateInternalClient().WithTenant(tenant).WithUser(user).WithRole("SYSTEM_ADMIN");
        if (manage)
        {
            client.DefaultRequestHeaders.Add("X-User-Capabilities", "workflow.manage");
        }
        return client;
    }

    private sealed class EvalEnabledFactory : TestWebAppFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("RUN_EVAL_ENABLED", "true");
        }
    }
}
