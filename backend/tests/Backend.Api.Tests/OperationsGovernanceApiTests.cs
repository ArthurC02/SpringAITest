using System.Net;
using System.Net.Http.Json;
using Backend.Api.OperationsGovernance;

namespace Backend.Api.Tests;

public sealed class OperationsGovernanceApiTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _factory;
    public OperationsGovernanceApiTests(TestWebAppFactory factory) => _factory = factory;

    [Fact]
    public async Task InternalTelemetry_IsTenantFencedAndIdempotentlyRecorded()
    {
        using var client = _factory.CreateInternalClient().WithTenant("ops-meter").WithUser("workflow").WithRole("SYSTEM");
        var run = Guid.NewGuid(); var eventId = Guid.NewGuid();
        var first = await client.PostAsJsonAsync("/api/operations/telemetry", new { run_id = run, event_id = eventId, kind = "model", node_id = "model_step", usage_units = 42, latency_ms = 9 });
        var replay = await client.PostAsJsonAsync("/api/operations/telemetry", new { run_id = run, event_id = eventId, kind = "model", node_id = "model_step", usage_units = 42, latency_ms = 9 });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var store = Assert.IsType<InMemoryOperationsGovernanceRepository>(_factory.Fake<IOperationsGovernanceRepository>());
        // 依租戶過濾:store 是本 class 共用的 singleton,不篩選的話任何新增的 telemetry 測試都會互相打到。
        var item = Assert.Single(store.Telemetry, x => x.Tenant == "ops-meter");
        Assert.Equal(42, item.Value.UsageUnits); Assert.Equal(9, item.Value.LatencyMs);
    }

    private const string EmptyGuid = "00000000-0000-0000-0000-000000000000";

    public static TheoryData<string, object?> RejectedTelemetryFields => new()
    {
        { "run_id", EmptyGuid },                    // 全零 GUID 不是可歸屬的 run
        { "event_id", EmptyGuid },                  // idempotency 的另一半,同樣不得為全零
        { "kind", "prompt" },                       // 只收 model|tool|node
        { "usage_units", -1 },
        { "usage_units", 10_000_001L },             // 上限 +1
        { "cost_units", -0.01m },                   // 下限 0 的 off-point
        { "cost_units", 1_000_001m },               // 上限 +1
        { "latency_ms", -1L },                      // 下限 0 的 off-point
        { "latency_ms", 86_400_001L },              // 一天 +1 毫秒
        { "agent_revision", 0 },                    // revision 從 1 起算
        { "agent_revision", 1_000_001 },
        { "skill_revision", 0 },                    // 與 agent_revision 是同一道檢查,不能只守一半
        { "skill_revision", 1_000_001 },
        { "node_id", "modelstep" },           // 控制字元
        { "node_id", new string('n', 201) },        // 長度上限 +1
    };

    // 這支是 Workflow 內部計量端點,輸入完全來自另一個服務:上限沒守住等於讓一次錯誤回報污染整份聚合指標。
    [Theory]
    [MemberData(nameof(RejectedTelemetryFields))]
    public async Task Telemetry_RejectsOutOfRangeOrUnsafeFields(string field, object? value)
    {
        var body = ValidTelemetry();
        body[field] = value;

        using var client = _factory.CreateInternalClient().WithTenant("ops-telemetry-invalid").WithUser("workflow");
        var response = await client.PostAsJsonAsync("/api/operations/telemetry", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // On-point 對照:每個上限剛好等於上界都必須被接受,否則邊界值就是抄錯的。
    [Fact]
    public async Task Telemetry_AcceptsInclusiveUpperBounds()
    {
        var body = ValidTelemetry();
        body["usage_units"] = 10_000_000L;
        body["cost_units"] = 1_000_000m;
        body["latency_ms"] = 86_400_000L;
        body["agent_revision"] = 1_000_000;
        body["skill_revision"] = 1_000_000;
        body["node_id"] = new string('n', 200);

        using var client = _factory.CreateInternalClient().WithTenant("ops-telemetry-bounds").WithUser("workflow");
        var response = await client.PostAsJsonAsync("/api/operations/telemetry", body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // 下限 0 的 on-point:快取命中/免費呼叫的 0 成本、0 延遲是合法計量,不能跟負值一起被丟掉。
    [Fact]
    public async Task Telemetry_AcceptsZeroLowerBounds()
    {
        var body = ValidTelemetry();
        body["usage_units"] = 0L;
        body["cost_units"] = 0m;
        body["latency_ms"] = 0L;

        using var client = _factory.CreateInternalClient().WithTenant("ops-telemetry-zero").WithUser("workflow");
        var response = await client.PostAsJsonAsync("/api/operations/telemetry", body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // kind 白名單的另外兩個合法值:tool/node 只在 repository 層被直接呼叫過,從沒真的走完 HTTP 驗證。
    [Theory]
    [InlineData("tool")]
    [InlineData("node")]
    public async Task Telemetry_AcceptsEveryDeclaredKind(string kind)
    {
        var body = ValidTelemetry();
        body["kind"] = kind;
        body["tool_name"] = "local.calculator";

        using var client = _factory.CreateInternalClient().WithTenant("ops-telemetry-kind").WithUser("workflow");
        var response = await client.PostAsJsonAsync("/api/operations/telemetry", body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // override 是 break-glass:必須有具體理由、必須有東西可以 override,而且 gate 已經通過時不得留下多餘的稽核紀錄。
    [Fact]
    public async Task Override_RequiresMeaningfulReasonAnAvailableGate_AndNotAPassingOne()
    {
        using var admin = Client("ops-override", "operator", manage: true);

        // reason 7 字元(下限 8 的 off-point);這一關在查 gate 之前。
        Assert.Equal(HttpStatusCode.BadRequest, (await OverrideAsync(admin, "1234567", "short-reason")).StatusCode);
        // 尚未有任何 regression 紀錄可供 override。
        Assert.Equal(HttpStatusCode.Conflict, (await OverrideAsync(admin, "12345678", "no-gate")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync(
            "/api/admin/operations/regressions",
            new { suite = "d7-passing", passed = true, evidence_ref = "evidence/pass" })).StatusCode);
        // gate 已通過 → override 沒有必要,必須 409 而不是照單全收。
        Assert.Equal(HttpStatusCode.Conflict, (await OverrideAsync(admin, "documented break-glass", "passing-gate")).StatusCode);
    }

    // reason 的上界 1000 與「整個欄位沒送」:on-point 必須通過長度檢查(因此才會走到「沒有 gate 可
    // override」的 409),off-point 與缺席都必須在查 gate 之前就 400。
    [Fact]
    public async Task Override_RejectsOversizedOrAbsentReason()
    {
        using var admin = Client("ops-override-bounds", "operator", manage: true);

        using var absent = new HttpRequestMessage(HttpMethod.Post, "/api/admin/operations/regression-overrides")
        {
            Content = JsonContent.Create(new { }),
        };
        absent.Headers.Add("Idempotency-Key", "absent-reason");
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(absent)).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await OverrideAsync(admin, new string('r', 1001), "reason-over-limit")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await OverrideAsync(admin, new string('r', 1000), "reason-at-limit")).StatusCode);
    }

    // W2-02(e) window_days 的值域決策表:省略 → 預設 90 並帶回應;界內 → 照用並帶回應;
    // 界外 → 400(刻意不夾到邊界,見 OperationsMetricsWindow 的註解)。on-point(1 / 365)與
    // off-point(0 / 366)兩側都測,只用「安全內部值」試不出打錯的邊界數字。
    // 窗外資料真的不計入聚合的部分由 Postgres 測試證明(lite 回填不了 200 天前的時間戳)。
    [Theory]
    [InlineData(null, OperationsMetricsWindow.DefaultDays, HttpStatusCode.OK)]
    [InlineData(OperationsMetricsWindow.MinDays, OperationsMetricsWindow.MinDays, HttpStatusCode.OK)]
    [InlineData(OperationsMetricsWindow.MaxDays, OperationsMetricsWindow.MaxDays, HttpStatusCode.OK)]
    [InlineData(OperationsMetricsWindow.MinDays - 1, 0, HttpStatusCode.BadRequest)]
    [InlineData(OperationsMetricsWindow.MaxDays + 1, 0, HttpStatusCode.BadRequest)]
    [InlineData(-1, 0, HttpStatusCode.BadRequest)]
    public async Task MetricsAndComparison_HonourWindowBounds_AndEchoTheAppliedWindow(
        int? windowDays, int expectedWindow, HttpStatusCode expected)
    {
        using var admin = Client("ops-window-" + (windowDays?.ToString() ?? "default"), "operator", manage: true);
        var query = windowDays is int days ? "?window_days=" + days : "";

        foreach (var route in new[] { "metrics", "version-comparison" })
        {
            var response = await admin.GetAsync("/api/admin/operations/" + route + query);

            Assert.Equal(expected, response.StatusCode);
            var body = await response.ReadJsonAsync();
            if (expected == HttpStatusCode.OK)
                Assert.Equal(expectedWindow, body["window_days"]!.GetValue<int>());
            else
                body.AssertApiError(400, "validation_failed");
        }
    }

    private static async Task<HttpResponseMessage> OverrideAsync(HttpClient client, string reason, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/operations/regression-overrides")
        {
            Content = JsonContent.Create(new { reason }),
        };
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }

    private static Dictionary<string, object?> ValidTelemetry() => new()
    {
        ["run_id"] = Guid.NewGuid(),
        ["event_id"] = Guid.NewGuid(),
        ["kind"] = "model",
        ["node_id"] = "model_step",
        ["agent_id"] = "agent-a",
        ["agent_revision"] = 1,
        ["usage_units"] = 1,
        ["cost_units"] = 0.5m,
        ["latency_ms"] = 1,
    };

    [Fact]
    public async Task InMemoryTelemetry_AggregatesAgentSkillToolNodeAndRevisionWithoutCrossTenantLeakage()
    {
        var store = new InMemoryOperationsGovernanceRepository();
        var run = Guid.NewGuid();
        await store.RecordTelemetryAsync("metric-a", new(run, Guid.NewGuid(), "model", "model_step", null, "skill-a", 3, "agent-a", 7, 12, 1.25m, 20), default);
        await store.RecordTelemetryAsync("metric-a", new(run, Guid.NewGuid(), "tool", "invoke_tool", "local.calculator", "skill-a", 3, "agent-a", 7, null, null, 10), default);
        await store.RecordTelemetryAsync("metric-b", new(Guid.NewGuid(), Guid.NewGuid(), "model", "other", null, "other", 1, "other", 1, 99, 9m, 99), default);

        var metrics = await store.GetMetricsAsync("metric-a", OperationsMetricsWindow.DefaultDays, default);
        var agent = Assert.Single(metrics.Agents);
        Assert.Equal(("agent-a", 7, 1), (agent.AgentId, agent.Revision, agent.Runs));
        Assert.Equal(12, agent.ObservedUsageUnits); Assert.Equal(1.25m, agent.ObservedCostUnits);
        var skill = Assert.Single(metrics.Skills);
        Assert.Equal(("skill-a", 3, 12L), (skill.Name, skill.Revision, skill.ObservedUsageUnits));
        var tool = Assert.Single(metrics.Tools);
        Assert.Equal("local.calculator", tool.Kind); Assert.Equal(10, tool.ObservedLatencyMs);
        Assert.Equal(2, metrics.Nodes.Count);

        var comparison = await store.GetVersionComparisonAsync("metric-a", 7, OperationsMetricsWindow.DefaultDays, default);
        // Telemetry carries Agent revision, never Orchestrator revision.
        // Lite mode has no durable Root ledger, so it must not fabricate an
        // Orchestrator version series from these events.
        Assert.Empty(comparison.Revisions);
        Assert.Null(comparison.SelectedVsPrevious);
    }

    [Fact]
    public async Task InMemory_ConcurrentCallsAcrossMethods_CompleteWithoutDeadlock()
    {
        // SemaphoreSlim is not reentrant like the System.Threading.Lock this repository used to
        // use. This proves plain concurrent access to several *different* gate-acquiring methods
        // at once still completes -- i.e. nothing here accidentally re-enters _gate from inside
        // itself (ApplyRolloutAsync's await into bindings.PutAsync included).
        var store = new InMemoryOperationsGovernanceRepository();
        var binding = new Backend.Api.RuntimeDiscovery.TenantRuntimeBinding(false, null, null, []);
        var all = Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            var tenant = $"concurrency-gate-{i}";
            for (var round = 0; round < 25; round++)
            {
                await store.RecordTelemetryAsync(tenant, new(Guid.NewGuid(), Guid.NewGuid(), "model", "model_step", null, null, null, null, null, null, null, 1), default);
                var gate = await store.RecordRegressionAsync(tenant, "suite", true, "evidence", "actor", default);
                await store.GetCurrentGateAsync(tenant, default);
                await store.GetMetricsAsync(tenant, OperationsMetricsWindow.DefaultDays, default);
                await store.ApplyRolloutAsync(tenant, binding, "actor", default);
                await store.CreateOverrideAsync(tenant, gate.Id, $"key-{i}-{round}", "reason", "actor", default);
            }
        }));
        var winner = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(ReferenceEquals(winner, all), "concurrent calls across InMemoryOperationsGovernanceRepository methods did not complete within 5s -- suspected deadlock");
    }

    [Fact]
    public async Task FailedRegression_BlocksRollout_UntilDurableAuditedOverride_AndIsTenantScoped()
    {
        using var admin = Client("ops-a", "operator-a", manage: true);
        var failed = await admin.PostAsJsonAsync("/api/admin/operations/regressions", new { suite = "d7-release", passed = false, evidence_ref = "evidence/d7-a" });
        Assert.Equal(HttpStatusCode.OK, failed.StatusCode);

        var blocked = await admin.PutAsJsonAsync("/api/admin/operations/rollout", new { enabled = true, orchestrator_id = Guid.NewGuid(), revision = 2, canary_user_ids = new[] { "user-a" } });
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);

        using var missingKey = new HttpRequestMessage(HttpMethod.Post, "/api/admin/operations/regression-overrides") { Content = JsonContent.Create(new { reason = "documented break-glass rollout" }) };
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(missingKey)).StatusCode);

        using var overrideRequest = new HttpRequestMessage(HttpMethod.Post, "/api/admin/operations/regression-overrides") { Content = JsonContent.Create(new { reason = "documented break-glass rollout" }) };
        overrideRequest.Headers.Add("Idempotency-Key", "ops-override-a");
        Assert.Equal(HttpStatusCode.OK, (await admin.SendAsync(overrideRequest)).StatusCode);
        using var replay = new HttpRequestMessage(HttpMethod.Post, "/api/admin/operations/regression-overrides") { Content = JsonContent.Create(new { reason = "documented break-glass rollout" }) };
        replay.Headers.Add("Idempotency-Key", "ops-override-a");
        Assert.Equal(HttpStatusCode.OK, (await admin.SendAsync(replay)).StatusCode);
        using var payloadConflict = new HttpRequestMessage(HttpMethod.Post, "/api/admin/operations/regression-overrides") { Content = JsonContent.Create(new { reason = "different text must not reuse an accepted decision" }) };
        payloadConflict.Headers.Add("Idempotency-Key", "ops-override-a");
        Assert.Equal(HttpStatusCode.Conflict, (await admin.SendAsync(payloadConflict)).StatusCode);

        var enabled = await admin.PutAsJsonAsync("/api/admin/operations/rollout", new { enabled = true, orchestrator_id = Guid.NewGuid(), revision = 2, canary_user_ids = new[] { "user-a" } });
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        var body = await enabled.ReadJsonAsync();
        Assert.True(body["new_roots_only"]!.GetValue<bool>());

        var metrics = await admin.GetAsync("/api/admin/operations/metrics");
        var metricText = await metrics.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, metrics.StatusCode);
        Assert.DoesNotContain("evidence/d7-a", metricText, StringComparison.Ordinal);
        Assert.DoesNotContain("break-glass", metricText, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync(
            "/api/admin/operations/regressions",
            new { suite = "d7-release-2", passed = false, evidence_ref = "evidence/d7-b" })).StatusCode);
        using var staleKey = new HttpRequestMessage(HttpMethod.Post, "/api/admin/operations/regression-overrides")
        {
            Content = JsonContent.Create(new { reason = "documented break-glass rollout" }),
        };
        staleKey.Headers.Add("Idempotency-Key", "ops-override-a");
        Assert.Equal(HttpStatusCode.Conflict, (await admin.SendAsync(staleKey)).StatusCode);

        using var otherTenant = Client("ops-b", "operator-b", manage: true);
        var otherMetrics = await (await otherTenant.GetAsync("/api/admin/operations/metrics")).ReadJsonAsync();
        Assert.True(otherMetrics["release_gate"]!["regression_passed"]!.GetValue<bool>());
        Assert.False(otherMetrics["release_gate"]!["override_active"]!.GetValue<bool>());
    }

    // suite / evidence_ref 是 release gate 唯一的稽核索引:空白或超長會讓通過與否查不回來源。
    public static TheoryData<string?, string?, HttpStatusCode> RegressionAuditFields => new()
    {
        { null, "evidence/pass", HttpStatusCode.BadRequest },                   // suite 未填
        { "   ", "evidence/pass", HttpStatusCode.BadRequest },                  // 只有空白,Trim 後等同未填
        { new string('s', 129), "evidence/pass", HttpStatusCode.BadRequest },   // suite 上限 128 +1
        { "d7\nrelease", "evidence/pass", HttpStatusCode.BadRequest },          // 控制字元
        { "d7-release", null, HttpStatusCode.BadRequest },                      // evidence_ref 未填
        { "d7-release", new string('e', 257), HttpStatusCode.BadRequest },      // evidence_ref 上限 256 +1
        { new string('s', 128), new string('e', 256), HttpStatusCode.OK },      // on-point:剛好等於上限必須收下
    };

    [Theory]
    [MemberData(nameof(RegressionAuditFields))]
    public async Task Regression_ValidatesSuiteAndEvidenceRefBounds(string? suite, string? evidenceRef, HttpStatusCode expected)
    {
        using var admin = Client("ops-regression-fields", "operator", manage: true);
        var response = await admin.PostAsJsonAsync(
            "/api/admin/operations/regressions",
            new { suite, passed = true, evidence_ref = evidenceRef });

        Assert.Equal(expected, response.StatusCode);
    }

    // enabled 的 binding 必須 pin 住 Orchestrator 修訂號,否則 canary 會跟著 default 浮動;
    // revision 從 1 起算,停用中送非法值一樣不收。
    [Theory]
    [InlineData(true, false, null)]     // 開啟卻完全沒指定 Orchestrator
    [InlineData(true, true, null)]      // 只給 id、沒給 revision
    [InlineData(true, true, 0)]         // revision 下限 1 的 off-point
    [InlineData(false, true, 0)]        // 停用中也不接受非法 revision
    public async Task Rollout_RejectsUnpinnedOrNonPositiveBinding(bool enabled, bool withOrchestrator, int? revision)
    {
        using var admin = Client("ops-rollout-binding", "operator", manage: true);
        var response = await admin.PutAsJsonAsync("/api/admin/operations/rollout", new
        {
            enabled,
            orchestrator_id = withOrchestrator ? Guid.NewGuid() : (Guid?)null,
            revision,
            canary_user_ids = new[] { "user-a" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // RequireManage() 是每個 handler 自己呼叫的,不是共用 filter:漏掉一行就整條路徑對沒有
    // workflow.manage 的呼叫者敞開。legacy-inventory 由下面那支釘住,這裡補完其餘路由。
    public static TheoryData<string, string> ManageProtectedRoutes => new()
    {
        { "POST", "/api/admin/operations/regressions" },
        { "POST", "/api/admin/operations/regression-overrides" },
        { "PUT", "/api/admin/operations/rollout" },
        { "GET", "/api/admin/operations/metrics" },
        { "GET", "/api/admin/operations/version-comparison" },
        { "GET", "/api/admin/operations/evidence-reconcile" },
    };

    [Theory]
    [MemberData(nameof(ManageProtectedRoutes))]
    public async Task AdminOperations_RejectCallerWithoutWorkflowManage(string method, string path)
    {
        using var denied = Client("ops-denied", "ordinary", manage: false);
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        // 連合法 body、Idempotency-Key 都不帶:403 必須發生在任何輸入驗證之前。
        if (method != "GET") request.Content = JsonContent.Create(new { });

        Assert.Equal(HttpStatusCode.Forbidden, (await denied.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task VersionAndInventory_AreCapabilityProtected_AndRedacted()
    {
        using var denied = Client("ops-c", "ordinary", manage: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await denied.GetAsync("/api/admin/operations/legacy-inventory")).StatusCode);

        using var admin = Client("ops-c", "operator", manage: true);
        var compare = await (await admin.GetAsync("/api/admin/operations/version-comparison")).ReadJsonAsync();
        Assert.False(compare["new_roots_only"]!.GetValue<bool>());
        Assert.True(compare["active_runs_keep_immutable_snapshot"]!.GetValue<bool>());
        var inventory = await admin.GetAsync("/api/admin/operations/legacy-inventory");
        Assert.Equal(HttpStatusCode.OK, inventory.StatusCode);
        Assert.DoesNotContain("token", await inventory.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    private HttpClient Client(string tenant, string user, bool manage)
    {
        var client = _factory.CreateInternalClient().WithTenant(tenant).WithUser(user).WithRole("SYSTEM_ADMIN");
        if (manage) client.DefaultRequestHeaders.Add("X-User-Capabilities", "workflow.manage");
        return client;
    }
}
