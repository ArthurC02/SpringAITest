using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.Service;

namespace Platform.Web.Tests;

// G4:ProxyFixture 與 DownstreamStatusFixture 各自共用一份 host。兩者分開的理由是 CapturingBackendHandler
// 只記錄「最近一次」請求/回應狀態(見該類別 XML doc)——DownstreamStatus_* 會呼叫 Handler.Reset 把回應
// 狀態碼改成非 200 且從不還原,若與其餘只斷言「自己這次請求」且預期固定 200/"{}" 回應的測試共用同一顆
// Handler,會依測試執行順序讓那些測試讀到殘留的非 200 狀態(order-dependent 壞法),故獨立成另一組。
public sealed class OperationsGovernanceApiTests
    : IClassFixture<OperationsGovernanceApiTests.ProxyFixture>,
        IClassFixture<OperationsGovernanceApiTests.DownstreamStatusFixture>
{
    private readonly ProxyFixture _proxy;
    private readonly DownstreamStatusFixture _downstreamStatus;

    public OperationsGovernanceApiTests(ProxyFixture proxy, DownstreamStatusFixture downstreamStatus)
    {
        _proxy = proxy;
        _downstreamStatus = downstreamStatus;
    }

    [Fact]
    public async Task FeatureOff_HidesOperationsBeforeAuthentication()
    {
        using var factory = new TestWebAppFactory();
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().GetAsync("/api/admin/operations/metrics")).StatusCode);
    }

    [Fact]
    public async Task FeatureOff_UsesSameNotFoundContractAsUnknownRoute()
    {
        using var factory = new TestWebAppFactory();
        using var client = factory.CreateClient();
        using var gated = await client.GetAsync("/api/admin/operations/metrics");
        using var unknown = await client.GetAsync("/api/not-a-route");

        Assert.Equal(HttpStatusCode.NotFound, gated.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        var gatedBody = await gated.ReadJsonAsync();
        var unknownBody = await unknown.ReadJsonAsync();
        gatedBody.AssertApiError(404, "not_found");
        unknownBody.AssertApiError(404, "not_found");
        Assert.Equal(gatedBody["message"]!.GetValue<string>(), unknownBody["message"]!.GetValue<string>());
        Assert.Equal(gated.Content.Headers.ContentType?.MediaType, unknown.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task FlagOn_AnonymousReturns401()
    {
        using var factory = TestWebAppFactory.WithFlags("AGENT_WRITE_TOOLS_ENABLED");
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await factory.CreateClient().GetAsync("/api/admin/operations/metrics")).StatusCode);
    }

    // 每條 route 的動詞/路徑必須正確組出 backend 路徑;Idempotency-Key 只在 regression-overrides 與
    // eval-runs 這兩條轉發(OperationsGovernanceController 的 key:true 只套在這兩個),其餘都不得夾帶
    // (那兩條的 Idempotency-Key 轉發另見 KeyRoutes_ForwardIdempotencyKeyHeader)。
    [Theory]
    [InlineData("GET", "metrics")]
    [InlineData("GET", "version-comparison")]
    [InlineData("GET", "legacy-inventory")]
    [InlineData("GET", "evidence-reconcile")]
    [InlineData("POST", "regressions")]
    [InlineData("PUT", "rollout")]
    [InlineData("GET", "eval-suites")]
    [InlineData("GET", "eval-suites/csr-eval-001")]
    [InlineData("GET", "eval-runs")]
    [InlineData("GET", "eval-runs/00000000-0000-0000-0000-000000000001")]
    public async Task RedactedRoutes_ForwardVerbAndPath_WithoutIdempotencyKey(string method, string suffix)
    {
        using var client = _proxy.CreateClient().WithToken(
            _proxy.IssueToken(capabilities: ["workflow.manage"]));
        using var request = new HttpRequestMessage(new HttpMethod(method), "/api/admin/operations/" + suffix);
        if (method is "POST" or "PUT")
        {
            request.Content = new StringContent("{\"k\":1}", Encoding.UTF8, "application/json");
        }

        request.Headers.Add("Idempotency-Key", "must-not-be-forwarded");
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(method, _proxy.Handler.Method);
        Assert.Equal("/api/admin/operations/" + suffix, _proxy.Handler.Path);
        Assert.Null(_proxy.Handler.Header("Idempotency-Key"));
    }

    // W2-02(e):window_days 在 platform 是純透傳,值域與拒絕行為留給 Backend authority。決策表兩半:
    // 帶了就必須出現在轉發的 query 上(否則 backend 永遠只看得到預設 90 天),沒帶就不得憑空補一個
    // (那會讓 platform 悄悄變成第二個定義預設值的地方)。界外值也照樣透傳 —— platform 不搶著驗。
    [Theory]
    [InlineData("metrics")]
    [InlineData("version-comparison")]
    public async Task WindowDays_IsForwardedVerbatim_AndAbsentWhenOmitted(string suffix)
    {
        using var client = _proxy.CreateClient().WithToken(
            _proxy.IssueToken(capabilities: ["workflow.manage"]));

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/admin/operations/{suffix}?window_days=30")).StatusCode);
        Assert.Equal("/api/admin/operations/" + suffix, _proxy.Handler.Path);
        Assert.Equal("?window_days=30", _proxy.Handler.Query);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/admin/operations/{suffix}?window_days=9999")).StatusCode);
        Assert.Equal("?window_days=9999", _proxy.Handler.Query);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/admin/operations/{suffix}")).StatusCode);
        Assert.Equal("", _proxy.Handler.Query);
    }

    // Regression 端點的 body 是 [FromBody] object(無 typed DTO),原樣序列化轉發;eval_run_id 這個
    // E2/E3 才新增的欄位不需要 platform 端額外補型別就能穿透 —— 這裡釘住這個事實。
    [Fact]
    public async Task Regressions_ForwardsEvalRunIdFieldVerbatim()
    {
        using var client = _proxy.CreateClient().WithToken(
            _proxy.IssueToken(capabilities: ["workflow.manage"]));
        var evalRunId = Guid.NewGuid();

        var response = await client.PostAsJsonAsync(
            "/api/admin/operations/regressions",
            new { suite = "gate-suite", passed = true, evidence_ref = "e", eval_run_id = evalRunId });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var forwardedBody = System.Text.Encoding.UTF8.GetString(_proxy.Handler.Body!);
        Assert.Contains(evalRunId.ToString(), forwardedBody, StringComparison.OrdinalIgnoreCase);
    }

    // backend 4xx(例如迴歸未通過 → 阻擋 rollout)是治理決策,原樣穿透;>= 500 收斂成受控 502 且不外洩 body。
    // 獨立於 ProxyFixture:每格都改寫 Handler 的回應狀態碼且從不還原(見本檔頂部 XML doc)。
    [Theory]
    [InlineData(409, 409, "regression gate blocked")]
    [InlineData(500, 502, null)]
    [InlineData(503, 502, null)]
    public async Task DownstreamStatus_PassesThrough4xx_AndCollapses5xxWithoutLeakingBody(
        int downstream, int expected, string? expectedMessage)
    {
        _downstreamStatus.Handler.Reset(
            (HttpStatusCode)downstream,
            "{\"status\":" + downstream + ",\"message\":\"regression gate blocked\",\"detail\":\"internal-secret\"}");
        using var client = _downstreamStatus.CreateClient().WithToken(
            _downstreamStatus.IssueToken(capabilities: ["workflow.manage"]));

        var response = await client.PutAsync(
            "/api/admin/operations/rollout",
            new StringContent("{\"k\":1}", Encoding.UTF8, "application/json"));

        Assert.Equal(expected, (int)response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        if (expectedMessage is null)
        {
            // 5xx:換成固定訊息,backend body 一個位元組都不得外流。
            Assert.DoesNotContain("internal-secret", body, StringComparison.Ordinal);
            Assert.DoesNotContain("regression gate blocked", body, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains(expectedMessage, body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task CapabilityAndFlagGate_ForwardOnlyAuthenticatedIdentityAndOverrideKey()
    {
        using var denied = _proxy.CreateClient().WithToken(_proxy.IssueToken(role: "ADMIN"));
        Assert.Equal(HttpStatusCode.Forbidden, (await denied.GetAsync("/api/admin/operations/metrics")).StatusCode);

        using var client = _proxy.CreateClient().WithToken(_proxy.IssueToken(username: "operator-a", tenantCode: "tenant-a", capabilities: ["workflow.manage"]));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/operations/regression-overrides") { Content = new StringContent("{\"reason\":\"break glass\"}", Encoding.UTF8, "application/json") };
        request.Headers.Add("Idempotency-Key", "override-logical-attempt");
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/api/admin/operations/regression-overrides", _proxy.Handler.Path);
        Assert.Equal("override-logical-attempt", _proxy.Handler.Header("Idempotency-Key"));
        Assert.Equal("tenant-a", _proxy.Handler.Header("X-Tenant-Id"));
        Assert.Equal("operator-a", _proxy.Handler.Header("X-User-Id"));
        Assert.Equal("workflow.manage", _proxy.Handler.Header("X-User-Capabilities"));
    }

    // regression-overrides 與 eval-runs 是 OperationsGovernanceController 裡僅有的兩條 key:true
    // 路由;其餘所有路由都由上面的 RedactedRoutes_ForwardVerbAndPath_WithoutIdempotencyKey 釘住不得夾帶。
    [Theory]
    [InlineData("regression-overrides")]
    [InlineData("eval-runs")]
    public async Task KeyRoutes_ForwardIdempotencyKeyHeader(string suffix)
    {
        using var client = _proxy.CreateClient().WithToken(
            _proxy.IssueToken(capabilities: ["workflow.manage"]));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/operations/" + suffix)
        {
            Content = new StringContent("{\"k\":1}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", "key-forward-check");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("key-forward-check", _proxy.Handler.Header("Idempotency-Key"));
    }

    // 決策表的另一半:單一 key 原樣轉發(上一條)vs. 重複 header → 400,一個位元組都不往下送。
    // 先前 ProxyControllerBase 用 StringValues.ToString() 把多值逗號拼接後轉發,合成出一把
    // 兩個邏輯嘗試都沒發過的 key,悄悄繞過 backend 的「恰一個值」檢查(Document/Chat 兩條路徑
    // 早就是 400,只有代理層漏掉)。
    [Fact]
    public async Task KeyRoutes_DuplicateIdempotencyKeyHeader_Returns400_AndForwardsNothing()
    {
        using var client = _proxy.CreateClient().WithToken(
            _proxy.IssueToken(capabilities: ["workflow.manage"]));
        using var request = new HttpRequestMessage(
            HttpMethod.Post, "/api/admin/operations/regression-overrides")
        {
            Content = new StringContent("{\"reason\":\"break glass\"}", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", new[] { "attempt-1", "attempt-2" });

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.ReadJsonAsync();
        body.AssertApiError(400, "validation_failed");
        Assert.Equal(
            "Idempotency-Key must contain exactly one value", body["message"]!.GetValue<string>());
    }

    /// <summary>保留真 BackendClient,只把最下游換成可斷言的攔截 handler(預設固定回應 200/"{}")。</summary>
    public class ProxyFixture : TestWebAppFactory
    {
        public CapturingBackendHandler Handler { get; } = new();
        public ProxyFixture() : base(new() { ["AGENT_WRITE_TOOLS_ENABLED"] = "true" }) { }
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<BackendClient>();
                services.AddHttpClient<BackendClient>().ConfigurePrimaryHttpMessageHandler(() => Handler);
            });
        }
    }

    /// <summary>與 <see cref="ProxyFixture"/> 同型但自己一份 Handler:DownstreamStatus_* 逐格改寫回應
    /// 狀態碼,不得與預期固定 200 回應的測試共用同一顆 CapturingBackendHandler 實例。</summary>
    public sealed class DownstreamStatusFixture : ProxyFixture
    {
    }
}
