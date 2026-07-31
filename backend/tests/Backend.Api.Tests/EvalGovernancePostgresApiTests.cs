using System.Net;
using System.Net.Http.Json;
using Backend.Api.OperationsGovernance;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Backend.Api.Tests;

/// <summary>
/// E2/E3 durable eval suite/result authority against the **real Dapper + Postgres** repository:
/// revision immutability/SHA idempotency under the real unique constraints, the CSR-EVAL-001
/// bootstrap seed actually landing in appdb, and tenant isolation. Business-logic coverage
/// (gate computation, controller validation, idempotent-write conflict) lives in the faster
/// InMemory-backed <see cref="EvalGovernanceApiTests"/>; isolation here is tenant prefixing
/// (shared springaitest db), matching <see cref="OperationsGovernancePostgresApiTests"/>.
/// </summary>
[Collection("Postgres")]
public sealed class EvalGovernancePostgresApiTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string TenantPrefix = "eval-pg-";

    public Task InitializeAsync() => CleanupAsync();

    public Task DisposeAsync() => CleanupAsync();

    [SkippableFact]
    public async Task SuiteRevision_IsImmutable_AndPublishIsShaIdempotent()
    {
        fixture.SkipIfUnavailable();
        var tenant = TenantPrefix + Guid.NewGuid().ToString("N");
        var repo = new EvalRepository(fixture.DataSource!);
        const string suiteId = "immutability-suite";
        const string contentA = "{\"policy\":{\"required_case_ids\":[]},\"cases\":[{\"case_id\":\"a\",\"mode\":\"deterministic\",\"input\":{},\"expected\":{}}]}";
        const string contentB = "{\"policy\":{\"required_case_ids\":[]},\"cases\":[{\"case_id\":\"a\",\"mode\":\"deterministic\",\"input\":{},\"expected\":{}},{\"case_id\":\"b\",\"mode\":\"deterministic\",\"input\":{},\"expected\":{}}]}";

        var first = await repo.PublishSuiteRevisionAsync(tenant, suiteId, contentA, "tester", default);
        var replay = await repo.PublishSuiteRevisionAsync(tenant, suiteId, contentA, "tester", default);
        Assert.Equal(1, first.Revision);
        Assert.Equal(1, replay.Revision);
        Assert.Equal(first.CasesSha256, replay.CasesSha256);

        await using (var connection = await fixture.DataSource!.OpenConnectionAsync())
        {
            var revisionCount = await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM eval_suite_revision r JOIN eval_suite s ON s.id = r.suite_id"
                + " WHERE s.tenant_id = @tenant AND s.suite_id = @suiteId",
                new { tenant, suiteId });
            Assert.Equal(1, revisionCount);
        }

        var next = await repo.PublishSuiteRevisionAsync(tenant, suiteId, contentB, "tester", default);
        Assert.Equal(2, next.Revision);
        Assert.NotEqual(first.CasesSha256, next.CasesSha256);

        // Immutability: revision 1's stored bytes never change after revision 2 is published.
        var rev1 = await repo.GetSuiteRevisionAsync(tenant, suiteId, 1, default);
        Assert.Equal(contentA, rev1!.CasesCanonical);
        Assert.Equal(first.CasesSha256, rev1.CasesSha256);
    }

    /// <summary>SeedEvalSuiteAsync 對 `tenants` 表的**每一列**發一次 PublishSuiteRevisionAsync,
    /// 所以兩個真實種子租戶都要各驗一次 —— 只驗 demo-a 的話,「只種到迴圈第一個租戶」的回歸會漏掉。</summary>
    [SkippableTheory]
    [InlineData("demo-a")]
    [InlineData("demo-b")]
    public async Task CsrEval001Seed_LandsInAppdb_ForEachRealTenant(string tenantCode)
    {
        fixture.SkipIfUnavailable();
        await using var connection = await fixture.DataSource!.OpenConnectionAsync();
        var suite = await connection.QuerySingleOrDefaultAsync<(string TenantId, int CurrentRevision)>(
            "SELECT tenant_id AS TenantId, current_revision AS CurrentRevision FROM eval_suite"
            + " WHERE tenant_id = @tenantCode AND suite_id = @suiteId",
            new { tenantCode, suiteId = CsrEval001Suite.SuiteId });
        Assert.Equal(tenantCode, suite.TenantId);
        Assert.Equal(1, suite.CurrentRevision);

        var caseCount = await connection.ExecuteScalarAsync<int>(
            "SELECT r.case_count FROM eval_suite_revision r JOIN eval_suite s ON s.id = r.suite_id"
            + " WHERE s.tenant_id = @tenantCode AND s.suite_id = @suiteId AND r.revision = 1",
            new { tenantCode, suiteId = CsrEval001Suite.SuiteId });
        Assert.True(caseCount > 0);
    }

    [SkippableFact]
    public async Task Run_IsIdempotentUnderTheRealUniqueConstraint_AndTenantIsolated()
    {
        fixture.SkipIfUnavailable();
        var tenant = TenantPrefix + Guid.NewGuid().ToString("N");
        var otherTenant = tenant + "-other";
        var repo = new EvalRepository(fixture.DataSource!);
        const string suiteId = "run-write-suite";
        const string content = "{\"policy\":{\"required_case_ids\":[\"a\"],\"freshness_seconds\":86400},\"cases\":[{\"case_id\":\"a\",\"mode\":\"deterministic\",\"input\":{},\"expected\":{}}]}";
        await repo.PublishSuiteRevisionAsync(tenant, suiteId, content, "tester", default);

        using var factory = new DapperEvalFactory();
        using var admin = factory.CreateInternalClient().WithTenant(tenant).WithUser("operator").WithRole("SYSTEM_ADMIN");
        admin.DefaultRequestHeaders.Add("X-User-Capabilities", "workflow.manage");

        using var request1 = NewRunRequest(suiteId, "run-write-key");
        using var request2 = NewRunRequest(suiteId, "run-write-key");
        var first = await admin.SendAsync(request1);
        var replay = await admin.SendAsync(request2);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(
            (await first.ReadJsonAsync())["id"]!.GetValue<Guid>(),
            (await replay.ReadJsonAsync())["id"]!.GetValue<Guid>());

        await using (var connection = await fixture.DataSource!.OpenConnectionAsync())
        {
            var runCount = await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM eval_run WHERE tenant_id = @tenant", new { tenant });
            Assert.Equal(1, runCount);
        }

        using var otherAdmin = factory.CreateInternalClient().WithTenant(otherTenant).WithUser("operator-b").WithRole("SYSTEM_ADMIN");
        otherAdmin.DefaultRequestHeaders.Add("X-User-Capabilities", "workflow.manage");
        Assert.Equal(HttpStatusCode.NotFound, (await otherAdmin.GetAsync($"/api/admin/operations/eval-suites/{suiteId}")).StatusCode);
        var otherRuns = await (await otherAdmin.GetAsync("/api/admin/operations/eval-runs")).ReadJsonAsync();
        Assert.Empty(otherRuns.AsArray());
    }

    /// <summary>Real Dapper/Postgres coverage of <see cref="EvalRepository.EvaluateGateAsync"/>
    /// (previously untested against a real DB): a required case failing must keep the regression
    /// gate closed even though the run itself "completed".</summary>
    [SkippableFact]
    public async Task EvaluateGateAsync_RequiredCaseFails_RegressionRecordsAsNotPassed()
    {
        fixture.SkipIfUnavailable();
        var tenant = TenantPrefix + Guid.NewGuid().ToString("N");
        var repo = new EvalRepository(fixture.DataSource!);
        const string suiteId = "gate-fail-suite";
        const string content = "{\"policy\":{\"required_case_ids\":[\"a\"],\"freshness_seconds\":86400},\"cases\":[{\"case_id\":\"a\",\"mode\":\"deterministic\",\"input\":{},\"expected\":{}}]}";
        await repo.PublishSuiteRevisionAsync(tenant, suiteId, content, "tester", default);

        using var factory = new DapperEvalFactory();
        using var admin = factory.CreateInternalClient().WithTenant(tenant).WithUser("operator").WithRole("SYSTEM_ADMIN");
        admin.DefaultRequestHeaders.Add("X-User-Capabilities", "workflow.manage");
        var fakeRunner = (FakeEvalRunner)factory.Fake<IEvalRunner>();
        fakeRunner.Setup((suite, revision, cases) => new EvalRunResponseWire(
            "fake-runner-1", suite, revision, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            new[] { new EvalCaseResultWire("a", "id-a", "FAIL", null, "regressed") }));

        using var runRequest = NewRunRequest(suiteId, "gate-fail-key");
        var runResponse = await admin.SendAsync(runRequest);
        Assert.Equal(HttpStatusCode.OK, runResponse.StatusCode);
        var runId = (await runResponse.ReadJsonAsync())["id"]!.GetValue<Guid>();

        var regressionResponse = await admin.PostAsJsonAsync(
            "/api/admin/operations/regressions",
            new { suite = suiteId, passed = true, evidence_ref = "e", eval_run_id = runId });
        Assert.Equal(HttpStatusCode.OK, regressionResponse.StatusCode);
        Assert.False((await regressionResponse.ReadJsonAsync())["regression_passed"]!.GetValue<bool>());
    }

    /// <summary>Mirror of the above with a fresh, all-PASS run -- the real gate must pass.</summary>
    [SkippableFact]
    public async Task EvaluateGateAsync_FreshAllPass_RegressionRecordsAsPassed()
    {
        fixture.SkipIfUnavailable();
        var tenant = TenantPrefix + Guid.NewGuid().ToString("N");
        var repo = new EvalRepository(fixture.DataSource!);
        const string suiteId = "gate-pass-suite";
        const string content = "{\"policy\":{\"required_case_ids\":[\"a\"],\"freshness_seconds\":86400},\"cases\":[{\"case_id\":\"a\",\"mode\":\"deterministic\",\"input\":{},\"expected\":{}}]}";
        await repo.PublishSuiteRevisionAsync(tenant, suiteId, content, "tester", default);

        using var factory = new DapperEvalFactory();
        using var admin = factory.CreateInternalClient().WithTenant(tenant).WithUser("operator").WithRole("SYSTEM_ADMIN");
        admin.DefaultRequestHeaders.Add("X-User-Capabilities", "workflow.manage");
        // FakeEvalRunner's default (no .Setup) script returns every case PASS with "now" timestamps.

        using var runRequest = NewRunRequest(suiteId, "gate-pass-key");
        var runResponse = await admin.SendAsync(runRequest);
        Assert.Equal(HttpStatusCode.OK, runResponse.StatusCode);
        var runId = (await runResponse.ReadJsonAsync())["id"]!.GetValue<Guid>();

        var regressionResponse = await admin.PostAsJsonAsync(
            "/api/admin/operations/regressions",
            new { suite = suiteId, passed = true, evidence_ref = "e", eval_run_id = runId });
        Assert.Equal(HttpStatusCode.OK, regressionResponse.StatusCode);
        Assert.True((await regressionResponse.ReadJsonAsync())["regression_passed"]!.GetValue<bool>());
    }

    private static HttpRequestMessage NewRunRequest(string suiteId, string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/operations/eval-runs")
        {
            Content = JsonContent.Create(new
            {
                suite_id = suiteId,
                revision = 1,
                candidate = new { kind = "skill", @ref = new { name = "candidate-under-test" } },
            }),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return request;
    }

    /// <summary>租戶前綴清理:eval_suite_revision/eval_case_result 沒有自己的 tenant_id,經父表 join 刪除;
    /// operations_regression_result/operations_release_audit 是 EvaluateGateAsync 測試經
    /// POST /regressions 寫入的,同一個 tenant 前綴一併清掉。</summary>
    private async Task CleanupAsync()
    {
        if (!fixture.Available) return;
        await using var connection = await fixture.DataSource!.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            DELETE FROM operations_release_audit WHERE tenant_id LIKE @prefix;
            DELETE FROM operations_regression_result WHERE tenant_id LIKE @prefix;
            DELETE FROM eval_case_result WHERE run_id IN (SELECT id FROM eval_run WHERE tenant_id LIKE @prefix);
            DELETE FROM eval_run WHERE tenant_id LIKE @prefix;
            DELETE FROM eval_suite_revision WHERE suite_id IN (SELECT id FROM eval_suite WHERE tenant_id LIKE @prefix);
            DELETE FROM eval_suite WHERE tenant_id LIKE @prefix;
            """,
            new { prefix = TenantPrefix + "%" });
    }

    private sealed class DapperEvalFactory : TestWebAppFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("RUN_EVAL_ENABLED", "true");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IEvalRepository>();
                services.AddScoped<IEvalRepository, EvalRepository>();
            });
        }
    }
}
