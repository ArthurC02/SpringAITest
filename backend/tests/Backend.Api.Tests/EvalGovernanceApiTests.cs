using System.Net;
using System.Net.Http.Json;
using System.Text;
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
    // The gate matches on path prefix only, so the write verb must be hidden too -- a POST that
    // still reached the controller while off would create a real run behind a "disabled" feature.
    [InlineData("POST", "/api/admin/operations/eval-runs")]
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
        var ownerRun = await CreateRunAsync(ownerAdmin, SuiteId, 1, "skill", key: "tenant-isolation");
        Assert.Equal(HttpStatusCode.OK, ownerRun.StatusCode);
        var ownerRunId = (await ownerRun.ReadJsonAsync())["id"]!.GetValue<Guid>();

        using var otherAdmin = Client(factory, "eval-tenant-b", "operator-b", manage: true);
        Assert.Equal(HttpStatusCode.NotFound, (await otherAdmin.GetAsync($"/api/admin/operations/eval-suites/{SuiteId}")).StatusCode);
        var otherSuites = await (await otherAdmin.GetAsync("/api/admin/operations/eval-suites")).ReadJsonAsync();
        Assert.Empty(otherSuites.AsArray());
        var otherRuns = await (await otherAdmin.GetAsync("/api/admin/operations/eval-runs")).ReadJsonAsync();
        Assert.Empty(otherRuns.AsArray());
        // GET eval-runs/{id} 的 404 是唯一沒被覆蓋過的一條:跨租戶不得洩漏他人 run 的存在,而且
        // 訊息必須指向 Eval Run —— 這條路由過去誤用了 Eval Suite 的工廠。
        var crossTenantRun = await otherAdmin.GetAsync($"/api/admin/operations/eval-runs/{ownerRunId:D}");
        Assert.Equal(HttpStatusCode.NotFound, crossTenantRun.StatusCode);
        Assert.Equal(
            $"找不到 Eval Run：{ownerRunId:D}",
            (await crossTenantRun.ReadJsonAsync())["message"]!.GetValue<string>());

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

    [Theory]
    // suite_id: blank / control character (the 128-char cap is a boundary, see the length theory).
    [InlineData("""{"suite_id":"   ","revision":1,"candidate":{"kind":"skill","ref":{"name":"c"}}}""", "suite_id is required")]
    [InlineData("""{"suite_id":"eval\u0007suite","revision":1,"candidate":{"kind":"skill","ref":{"name":"c"}}}""", "suite_id is required")]
    // revision: omitted / zero / negative -- the valid class starts at 1.
    [InlineData("""{"suite_id":"eval-test-suite","candidate":{"kind":"skill","ref":{"name":"c"}}}""", "revision is required")]
    [InlineData("""{"suite_id":"eval-test-suite","revision":0,"candidate":{"kind":"skill","ref":{"name":"c"}}}""", "revision is required")]
    [InlineData("""{"suite_id":"eval-test-suite","revision":-1,"candidate":{"kind":"skill","ref":{"name":"c"}}}""", "revision is required")]
    // candidate: omitted entirely / a kind outside {skill, agent}.
    [InlineData("""{"suite_id":"eval-test-suite","revision":1}""", "candidate.kind must be skill or agent")]
    [InlineData("""{"suite_id":"eval-test-suite","revision":1,"candidate":{"kind":"workflow","ref":{"name":"c"}}}""", "candidate.kind must be skill or agent")]
    // candidate.ref: omitted / present but not a JSON object.
    [InlineData("""{"suite_id":"eval-test-suite","revision":1,"candidate":{"kind":"skill"}}""", "candidate.ref must be a JSON object")]
    [InlineData("""{"suite_id":"eval-test-suite","revision":1,"candidate":{"kind":"skill","ref":"not-an-object"}}""", "candidate.ref must be a JSON object")]
    // candidate.pins: optional, but when present it must be an object.
    [InlineData("""{"suite_id":"eval-test-suite","revision":1,"candidate":{"kind":"skill","ref":{"name":"c"},"pins":["a"]}}""", "candidate.pins must be a JSON object")]
    public async Task CreateRun_RejectsEveryInvalidRequestBodyClass_BeforeCallingWorkflow(string body, string expectedMessage)
    {
        using var factory = new EvalEnabledFactory();
        using var admin = Client(factory, "eval-invalid-body", "operator", manage: true);
        var fakeRunner = (FakeEvalRunner)factory.Fake<IEvalRunner>();

        var response = await PostRunAsync(
            admin, new StringContent(body, Encoding.UTF8, "application/json"), "invalid-body-key");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(expectedMessage, await ErrorMessageAsync(response));
        // Validation is pure request shape: an invalid body never reaches (or costs) Workflow.
        Assert.Empty(fakeRunner.Calls);
    }

    [Theory]
    [InlineData(128, HttpStatusCode.NotFound)] // at the cap: passes validation, then fails to resolve
    [InlineData(129, HttpStatusCode.BadRequest)] // one over: rejected before any lookup
    public async Task CreateRun_SuiteId_IsAcceptedAtTheLengthCap_AndRejectedOneCharOver(
        int length, HttpStatusCode expected)
    {
        using var factory = new EvalEnabledFactory();
        using var admin = Client(factory, "eval-suite-length", "operator", manage: true);
        var fakeRunner = (FakeEvalRunner)factory.Fake<IEvalRunner>();

        var response = await PostRunAsync(admin, JsonContent.Create(new
        {
            suite_id = new string('s', length),
            revision = 1,
            candidate = new { kind = "skill", @ref = new { name = "candidate-under-test" } },
        }), "suite-length-key");

        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.BadRequest)
        {
            Assert.Equal("suite_id is required", await ErrorMessageAsync(response));
        }
        // Neither row names a published suite, so neither may reach Workflow.
        Assert.Empty(fakeRunner.Calls);
    }

    [Theory]
    [InlineData(1, HttpStatusCode.OK)]
    [InlineData(300_000, HttpStatusCode.OK)]
    [InlineData(0, HttpStatusCode.BadRequest)]
    [InlineData(300_001, HttpStatusCode.BadRequest)]
    public async Task CreateRun_BudgetMs_AcceptsTheInclusiveRange_AndRejectsEitherSideOfIt(
        int budgetMs, HttpStatusCode expected)
    {
        using var factory = new EvalEnabledFactory();
        var evals = factory.Fake<IEvalRepository>();
        await evals.PublishSuiteRevisionAsync("eval-budget-range", SuiteId, SuiteContent(requiredCaseIds: "case-a"), "system", default);
        using var admin = Client(factory, "eval-budget-range", "operator", manage: true);
        var fakeRunner = (FakeEvalRunner)factory.Fake<IEvalRunner>();

        var response = await PostRunAsync(admin, JsonContent.Create(new
        {
            suite_id = SuiteId,
            revision = 1,
            candidate = new { kind = "skill", @ref = new { name = "candidate-under-test" } },
            budget_ms = budgetMs,
        }), "budget-range-key");

        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.OK)
        {
            // An accepted budget is forwarded verbatim, never clamped.
            Assert.Equal(budgetMs, Assert.Single(fakeRunner.Calls).BudgetMs);
        }
        else
        {
            Assert.Equal("budget_ms must be between 1 and 300000", await ErrorMessageAsync(response));
            Assert.Empty(fakeRunner.Calls);
        }
    }

    [Theory]
    // candidate.ref serializes compactly as {"pad":"<padding>"} -- a 10-byte envelope -- so these
    // padding lengths put the ref exactly at the 16384-byte cap and exactly one byte over it.
    [InlineData(16_374, HttpStatusCode.OK)]
    [InlineData(16_375, HttpStatusCode.BadRequest)]
    public async Task CreateRun_CandidateRef_IsAcceptedAtTheByteCap_AndRejectedOneByteOver(
        int padLength, HttpStatusCode expected)
    {
        using var factory = new EvalEnabledFactory();
        var evals = factory.Fake<IEvalRepository>();
        await evals.PublishSuiteRevisionAsync("eval-ref-bytes", SuiteId, SuiteContent(requiredCaseIds: "case-a"), "system", default);
        using var admin = Client(factory, "eval-ref-bytes", "operator", manage: true);
        var fakeRunner = (FakeEvalRunner)factory.Fake<IEvalRunner>();

        var response = await PostRunAsync(admin, JsonContent.Create(new
        {
            suite_id = SuiteId,
            revision = 1,
            candidate = new { kind = "skill", @ref = new { pad = new string('x', padLength) } },
        }), "ref-bytes-key");

        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.OK)
        {
            Assert.Single(fakeRunner.Calls);
        }
        else
        {
            // Over-cap reuses the shape message rather than a size-specific one.
            Assert.Equal("candidate.ref must be a JSON object", await ErrorMessageAsync(response));
            Assert.Empty(fakeRunner.Calls);
        }
    }

    [Theory]
    [InlineData(null)] // header absent
    [InlineData("")] // present but empty
    [InlineData("   ")] // whitespace only
    public async Task CreateRun_RejectsMissingOrBlankIdempotencyKey(string? key)
    {
        using var factory = new EvalEnabledFactory();
        var evals = factory.Fake<IEvalRepository>();
        await evals.PublishSuiteRevisionAsync("eval-no-key", SuiteId, SuiteContent(requiredCaseIds: "case-a"), "system", default);
        using var admin = Client(factory, "eval-no-key", "operator", manage: true);
        var fakeRunner = (FakeEvalRunner)factory.Fake<IEvalRunner>();

        var response = await PostRunAsync(admin, JsonContent.Create(new
        {
            suite_id = SuiteId,
            revision = 1,
            candidate = new { kind = "skill", @ref = new { name = "candidate-under-test" } },
        }), key);

        // Without a usable key there is no replay identity at all, so the write must never start.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Idempotency-Key is required", await ErrorMessageAsync(response));
        Assert.Empty(fakeRunner.Calls);
        Assert.Empty(await evals.ListRunsAsync("eval-no-key", default));
    }

    [Fact]
    public async Task EvalRoutes_Require_WorkflowManage_EvenWhileTheFlagIsOn()
    {
        // flag=on x capability=missing: the combination the flag-off 404 theory can never observe,
        // because that gate short-circuits before authorization is ever consulted. SYSTEM_ADMIN on
        // its own is deliberately not workflow.manage.
        using var factory = new EvalEnabledFactory();
        var evals = factory.Fake<IEvalRepository>();
        await evals.PublishSuiteRevisionAsync("eval-capability", SuiteId, SuiteContent(requiredCaseIds: "case-a"), "system", default);
        using var noManage = Client(factory, "eval-capability", "operator", manage: false);
        var fakeRunner = (FakeEvalRunner)factory.Fake<IEvalRunner>();

        var readRoutes = new[]
        {
            "/api/admin/operations/eval-suites",
            $"/api/admin/operations/eval-suites/{SuiteId}",
            $"/api/admin/operations/eval-suites/{SuiteId}/revisions/1",
            "/api/admin/operations/eval-runs",
            "/api/admin/operations/eval-runs/00000000-0000-0000-0000-000000000001",
        };
        foreach (var route in readRoutes)
        {
            var response = await noManage.GetAsync(route);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("workflow.manage capability is required", await ErrorMessageAsync(response));
        }

        var created = await CreateRunAsync(noManage, SuiteId, 1, "skill", key: "no-manage-key");
        Assert.Equal(HttpStatusCode.Forbidden, created.StatusCode);
        Assert.Empty(fakeRunner.Calls);
        Assert.Empty(await evals.ListRunsAsync("eval-capability", default));
    }

    [Fact]
    public async Task CreateRun_502sAndPersistsNothing_WhenTheRunnerViolatesTheResponseContract()
    {
        using var factory = new EvalEnabledFactory();
        var evals = factory.Fake<IEvalRepository>();
        await evals.PublishSuiteRevisionAsync("eval-contract", SuiteId, SuiteContent(requiredCaseIds: "case-a"), "system", default);
        using var admin = Client(factory, "eval-contract", "operator", manage: true);
        var fake = (FakeEvalRunner)factory.Fake<IEvalRunner>();
        var now = DateTimeOffset.UtcNow;
        var onePass = new[] { new EvalCaseResultWire("case-a", "id-a", "PASS", null, null) };

        // A runner answering about a *different* suite must never be attributed to this request.
        fake.Setup((suite, revision, cases) => new EvalRunResponseWire(
            "fake-runner-1", "a-different-suite", revision, now, now, onePass));
        var wrongSuite = await CreateRunAsync(admin, SuiteId, 1, "skill", key: "contract-suite");
        Assert.Equal(HttpStatusCode.BadGateway, wrongSuite.StatusCode);
        Assert.Equal("Eval runner 回應違反契約（suite_id/revision 與請求不符）", await ErrorMessageAsync(wrongSuite));

        // Same for a result about a different revision of the right suite.
        fake.Setup((suite, revision, cases) => new EvalRunResponseWire(
            "fake-runner-1", suite, revision + 1, now, now, onePass));
        var wrongRevision = await CreateRunAsync(admin, SuiteId, 1, "skill", key: "contract-revision");
        Assert.Equal(HttpStatusCode.BadGateway, wrongRevision.StatusCode);
        Assert.Equal("Eval runner 回應違反契約（suite_id/revision 與請求不符）", await ErrorMessageAsync(wrongRevision));

        // A case without an identity cannot be gated on later, so it is rejected, not stored blank.
        fake.Setup((suite, revision, cases) => new EvalRunResponseWire(
            "fake-runner-1", suite, revision, now, now,
            new[] { new EvalCaseResultWire("   ", "id-a", "PASS", null, null) }));
        var blankCaseId = await CreateRunAsync(admin, SuiteId, 1, "skill", key: "contract-case-id");
        Assert.Equal(HttpStatusCode.BadGateway, blankCaseId.StatusCode);
        Assert.Equal("Eval runner 回應違反契約（case 結果形狀不合法）", await ErrorMessageAsync(blankCaseId));

        // Verdict is a closed set -- an unknown one would silently count as neither pass nor fail.
        fake.Setup((suite, revision, cases) => new EvalRunResponseWire(
            "fake-runner-1", suite, revision, now, now,
            new[] { new EvalCaseResultWire("case-a", "id-a", "SKIPPED", null, null) }));
        var badVerdict = await CreateRunAsync(admin, SuiteId, 1, "skill", key: "contract-verdict");
        Assert.Equal(HttpStatusCode.BadGateway, badVerdict.StatusCode);
        Assert.Equal("Eval runner 回應違反契約（case 結果形狀不合法）", await ErrorMessageAsync(badVerdict));

        // Every rejection above happens before the write: no half-run is observable.
        Assert.Empty(await evals.ListRunsAsync("eval-contract", default));
    }

    [Fact]
    public async Task CreateRun_AgentCandidate_IsAcceptedAsAKindOnItsOwn()
    {
        // "agent" otherwise only appears as the *conflicting* second candidate of the idempotency
        // test, where its 409 would be identical for any rejected kind.
        using var factory = new EvalEnabledFactory();
        var evals = factory.Fake<IEvalRepository>();
        await evals.PublishSuiteRevisionAsync("eval-agent-kind", SuiteId, SuiteContent(requiredCaseIds: "case-a"), "system", default);
        using var admin = Client(factory, "eval-agent-kind", "operator", manage: true);
        var fakeRunner = (FakeEvalRunner)factory.Fake<IEvalRunner>();

        var response = await CreateRunAsync(admin, SuiteId, 1, "agent", key: "agent-kind-key");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("agent", (await response.ReadJsonAsync())["candidate"]!["kind"]!.GetValue<string>());
        Assert.Equal("agent", Assert.Single(fakeRunner.Calls).CandidateKind);
        Assert.Equal("agent", Assert.Single(await evals.ListRunsAsync("eval-agent-kind", default)).CandidateKind);
    }

    [Fact]
    public async Task OwningTenant_SuiteDetail_RevisionDetail_AndRunDetail_ReturnTheStoredShape()
    {
        // The read side is only ever asserted from the *denied* (cross-tenant 404) direction
        // elsewhere; this is the owning-tenant half of that pair.
        using var factory = new EvalEnabledFactory();
        var evals = factory.Fake<IEvalRepository>();
        await evals.PublishSuiteRevisionAsync("eval-read", SuiteId, SuiteContent(requiredCaseIds: "case-a"), "system", default);
        using var admin = Client(factory, "eval-read", "operator", manage: true);
        var created = await CreateRunAsync(admin, SuiteId, 1, "skill", key: "read-key");
        var runId = (await created.ReadJsonAsync())["id"]!.GetValue<Guid>();

        var suite = await (await admin.GetAsync($"/api/admin/operations/eval-suites/{SuiteId}")).ReadJsonAsync();
        Assert.Equal(SuiteId, suite["suite_id"]!.GetValue<string>());
        Assert.Equal(1, suite["current_revision"]!.GetValue<int>());
        var revisionSummary = Assert.Single(suite["revisions"]!.AsArray())!;
        Assert.Equal(1, revisionSummary["revision"]!.GetValue<int>());
        Assert.Equal(1, revisionSummary["case_count"]!.GetValue<int>());
        Assert.Equal("system", revisionSummary["created_by"]!.GetValue<string>());

        var revision = await (await admin.GetAsync($"/api/admin/operations/eval-suites/{SuiteId}/revisions/1")).ReadJsonAsync();
        Assert.Equal(revisionSummary["cases_sha256"]!.GetValue<string>(), revision["cases_sha256"]!.GetValue<string>());
        // policy/cases come back as real JSON, parsed out of the immutable canonical bytes.
        Assert.Equal("case-a", revision["policy"]!["required_case_ids"]![0]!.GetValue<string>());
        Assert.Equal("case-a", revision["cases"]![0]!["case_id"]!.GetValue<string>());

        var run = await (await admin.GetAsync($"/api/admin/operations/eval-runs/{runId:D}")).ReadJsonAsync();
        Assert.Equal(runId, run["id"]!.GetValue<Guid>());
        Assert.Equal(SuiteId, run["suite_id"]!.GetValue<string>());
        Assert.Equal(1, run["suite_revision"]!.GetValue<int>());
        Assert.Equal("fake-runner-1", run["runner_version"]!.GetValue<string>());
        Assert.Equal(1, run["pass_count"]!.GetValue<int>());
        Assert.Equal(0, run["fail_count"]!.GetValue<int>());
        Assert.Equal(0, run["error_count"]!.GetValue<int>());
        Assert.Equal(64, run["candidate"]!["identity_sha256"]!.GetValue<string>().Length);
        var caseResult = Assert.Single(run["cases"]!.AsArray())!;
        Assert.Equal("case-a", caseResult["case_id"]!.GetValue<string>());
        Assert.Equal("PASS", caseResult["verdict"]!.GetValue<string>());
    }

    [Fact]
    public async Task SameTenant_ExistingSuite_ButMissingRevision_Is404()
    {
        // The other half of the tenant x existence matrix: SuitesAndRuns_AreTenantIsolated covers
        // "wrong tenant, suite absent"; this is "right tenant, suite present, revision absent".
        using var factory = new EvalEnabledFactory();
        var evals = factory.Fake<IEvalRepository>();
        await evals.PublishSuiteRevisionAsync("eval-missing-rev", SuiteId, SuiteContent(requiredCaseIds: "case-a"), "system", default);
        using var admin = Client(factory, "eval-missing-rev", "operator", manage: true);
        var fakeRunner = (FakeEvalRunner)factory.Fake<IEvalRunner>();

        var run = await CreateRunAsync(admin, SuiteId, 2, "skill", key: "missing-revision-key");
        Assert.Equal(HttpStatusCode.NotFound, run.StatusCode);
        Assert.Equal($"找不到 Eval Suite revision：{SuiteId}@2", await ErrorMessageAsync(run));
        Assert.Empty(fakeRunner.Calls);
        Assert.Empty(await evals.ListRunsAsync("eval-missing-rev", default));

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await admin.GetAsync($"/api/admin/operations/eval-suites/{SuiteId}/revisions/2")).StatusCode);
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

    /// <summary>POST eval-runs with a caller-controlled body/header pair (malformed bodies, boundary
    /// values, absent or blank Idempotency-Key) -- <see cref="CreateRunAsync"/> can only send valid ones.</summary>
    private static async Task<HttpResponseMessage> PostRunAsync(HttpClient client, HttpContent content, string? key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/operations/eval-runs")
        {
            Content = content,
        };
        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        }
        return await client.SendAsync(request);
    }

    /// <summary>Asserts the outward ApiError shape of a rejection (status mirrored into the body, a
    /// fieldErrors map always present) and returns its message, so every negative case closes both
    /// halves of the decision -- the status code and what the caller is actually told.</summary>
    private static async Task<string> ErrorMessageAsync(HttpResponseMessage response)
    {
        var body = await response.ReadJsonAsync();
        Assert.Equal((int)response.StatusCode, body["status"]!.GetValue<int>());
        Assert.NotNull(body["timestamp"]);
        Assert.Empty(body["fieldErrors"]!.AsObject());
        return body["message"]!.GetValue<string>();
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
