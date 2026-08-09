using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.Service.Abstractions;
using Platform.Service.Dtos;

namespace Platform.Web.Tests;

[Collection("EngineCalls")]
public sealed class RunApprovalApiTests : IClassFixture<RunApprovalApiTests.EnabledApprovalFixture>
{
    private const string RunId = FakeAgentRunService.RunIdText;
    private const string ApprovalId = "66666666-6666-4666-8666-666666666666";

    private readonly EnabledApprovalFixture _factory;

    public RunApprovalApiTests(EnabledApprovalFixture factory) => _factory = factory;

    // D7 的旗標獨立於 D3:即使 Agent 測試台(builder + testRun)整組打開,
    // AGENT_WRITE_TOOLS_ENABLED 關閉仍必須在認證之前 404。
    // 兩個資料點的旗標組合彼此不同(false/false vs true/true),無法併入共用 fixture,維持各自 factory。
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FeatureOff_HidesApprovalRoutesBeforeAuthentication(bool agentTestRunEnabled)
    {
        var testRun = agentTestRunEnabled ? "true" : "false";
        using var factory = new TestWebAppFactory(new()
        {
            ["AGENT_BUILDER_ENABLED"] = testRun,
            ["AGENT_TEST_RUN_ENABLED"] = testRun,
            ["AGENT_WRITE_TOOLS_ENABLED"] = "false",
        });
        var before = FakeAgentRunService.Calls.Count;

        var response = await factory.CreateClient().GetAsync($"/api/runs/{RunId}/approvals");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(before, FakeAgentRunService.Calls.Count);
    }

    // O3 discoverable approval queue shares the exact same 404 fail-closed gate as the existing
    // per-run list route — same flag, same pre-auth posture.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FeatureOff_HidesQueueRouteBeforeAuthentication(bool agentTestRunEnabled)
    {
        var testRun = agentTestRunEnabled ? "true" : "false";
        using var factory = new TestWebAppFactory(new()
        {
            ["AGENT_BUILDER_ENABLED"] = testRun,
            ["AGENT_TEST_RUN_ENABLED"] = testRun,
            ["AGENT_WRITE_TOOLS_ENABLED"] = "false",
        });
        var before = FakeAgentRunService.Calls.Count;

        var response = await factory.CreateClient().GetAsync("/api/runs/approvals");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(before, FakeAgentRunService.Calls.Count);
    }

    // 下面共用 EnabledApprovalFixture 的測試都只斷言「自己這次呼叫」(Contains 特定字串,或緊接在
    // 自己請求後讀 LastContext/delta count),不依賴 FakeAgentRunService.Calls 只含自己那筆或固定順序,
    // 故共用同一份 host 是安全的(旗標組合的風險注記見 EnabledApprovalFixture)。

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("USER", HttpStatusCode.OK)]
    [InlineData("ADMIN", HttpStatusCode.OK)]
    public async Task EnabledApprovalList_RequiresAuthenticationButNotAdmin(string? role, HttpStatusCode expected)
    {
        var client = _factory.CreateClient();
        if (role is not null)
        {
            client = client.WithToken(_factory.IssueToken("business-approver", role, "tenant-x"));
        }

        var response = await client.GetAsync($"/api/runs/{RunId}/approvals");

        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.OK)
        {
            Assert.Equal("business-approver", FakeAgentRunService.LastContext!.UserId);
            Assert.Equal("tenant-x", FakeAgentRunService.LastContext.TenantCode);
        }
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("USER", HttpStatusCode.OK)]
    [InlineData("ADMIN", HttpStatusCode.OK)]
    public async Task EnabledQueue_RequiresAuthenticationButNotAdmin(string? role, HttpStatusCode expected)
    {
        var client = _factory.CreateClient();
        if (role is not null)
        {
            client = client.WithToken(_factory.IssueToken("business-approver", role, "tenant-x"));
        }

        var response = await client.GetAsync("/api/runs/approvals");

        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.OK)
        {
            Assert.Equal("business-approver", FakeAgentRunService.LastContext!.UserId);
            Assert.Equal("tenant-x", FakeAgentRunService.LastContext.TenantCode);
        }
    }

    // O3 §4 defines two predicates (scope=visible/actionable) plus a keyset cursor; Platform's
    // whole job here is to forward the query string and identity verbatim — Backend owns both
    // predicates and the cursor codec, so this only proves nothing is dropped or defaulted away.
    [Fact]
    public async Task Queue_ForwardsScopeCursorAndLimitVerbatim()
    {
        var client = _factory.CreateClient().WithToken(_factory.IssueToken("approver", "USER", "tenant-x"));

        var response = await client.GetAsync("/api/runs/approvals?scope=actionable&cursor=abc123&limit=7");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("queue:actionable:abc123:7:approver", FakeAgentRunService.Calls);
    }

    // Off-point for the cursor parameter specifically: omitted entirely (not an empty string),
    // and the controller's own default scope/limit apply — Platform must not invent a cursor.
    [Fact]
    public async Task Queue_WithoutCursorOrExplicitScopeAndLimit_ForwardsControllerDefaults()
    {
        var client = _factory.CreateClient().WithToken(_factory.IssueToken("approver", "USER", "tenant-x"));

        var response = await client.GetAsync("/api/runs/approvals");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("queue:visible::20:approver", FakeAgentRunService.Calls);
    }

    // 決策權在 Backend,查詢佇列同理:SoD/expiry/角色都是 Backend 算好放進 DTO 的,平台只透傳。
    [Theory]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(409)]
    public async Task Queue_DownstreamRejection_PassesThroughVerbatim(int status)
    {
        using var factory = new RejectingFactory(status);
        var client = factory.CreateClient().WithToken(factory.IssueToken("approver", "USER", "tenant-x"));

        var response = await client.GetAsync("/api/runs/approvals");

        Assert.Equal(status, (int)response.StatusCode);
        var body = await response.ReadJsonAsync();
        Assert.Equal(status, body["status"]!.GetValue<int>());
        Assert.Equal("下游決策訊息", body["message"]!.GetValue<string>());
        Assert.Equal("backend_decision_code", body["code"]!.GetValue<string>());
        Assert.Equal("backend-trace-2", body["correlationId"]!.GetValue<string>());
    }

    // List 的三格(匿名/USER/ADMIN)已覆蓋,決策路由卻只被 USER 打過:approve/reject 共用同一支
    // [Authorize],匿名必須 401(證明授權真的套用在這兩個 action 上),而 ADMIN 這一格必須照樣 202 ——
    // D7 的決策權在 Backend,平台不得把「非 ADMIN 不能決策」偷渡成閘門。
    [Theory]
    [InlineData("approve", "True")]
    [InlineData("reject", "False")]
    public async Task Decision_RequiresAuthenticationButNotAdmin(string action, string approve)
    {
        var path = $"/api/runs/{RunId}/approvals/{ApprovalId}/{action}";

        var anonymous = await _factory.CreateClient().PostAsJsonAsync(path, new { reason = "r" });
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var admin = _factory.CreateClient().WithToken(_factory.IssueToken("boss", "ADMIN", "tenant-x"));
        var response = await admin.PostAsJsonAsync(path, new { reason = "r" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Contains($"approval:{RunId}:{ApprovalId}:{approve}:r::boss", FakeAgentRunService.Calls);
    }

    [Fact]
    public async Task Decision_ForwardsOnlySignedIdentityReasonAndIdempotencyKey()
    {
        var client = _factory.CreateClient().WithToken(_factory.IssueToken("approver", "USER", "tenant-x"));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/runs/{RunId}/approvals/{ApprovalId}/approve")
        {
            Content = JsonContent.Create(new { reason = "reviewed" }),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", "approval-attempt-1");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Contains($"approval:{RunId}:{ApprovalId}:True:reviewed:approval-attempt-1:approver", FakeAgentRunService.Calls);
    }

    // Idempotency-Key 的邊界:「帶了 header 但值為空」與「完全沒帶 header」是兩個相鄰卻不同的等價類 ——
    // ProxyControllerBase 用 TryGetValue,空值 header 會拿到 ""(不是 null)。平台對空值既不 400、
    // 也不自行補一把 key,原樣往下轉發(AgentRunService 的 IsNullOrWhiteSpace 讓兩者最終殊途同歸)。
    [Fact]
    public async Task Decision_EmptyIdempotencyKeyHeader_IsAcceptedAndForwardsNoKey()
    {
        var client = _factory.CreateClient().WithToken(_factory.IssueToken("approver", "USER", "tenant-x"));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/runs/{RunId}/approvals/{ApprovalId}/approve")
        {
            Content = JsonContent.Create(new { reason = "reviewed" }),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", string.Empty);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Contains($"approval:{RunId}:{ApprovalId}:True:reviewed::approver", FakeAgentRunService.Calls);
    }

    // reject 是 approve 的鏡像路由,在 platform 之前完全沒被執行過:必須確認 /reject 對應 approve=false
    // (是它決定了 service 層不去 kick 已核准的寫入,見 AgentRunServiceTests)。
    // 同時覆蓋「未帶 Idempotency-Key」的 off-point:轉發 null 而不是憑空生一把 key。
    [Fact]
    public async Task Reject_Returns202_WithApproveFalse_AndNoIdempotencyKey()
    {
        var client = _factory.CreateClient().WithToken(_factory.IssueToken("approver", "USER", "tenant-x"));

        var response = await client.PostAsJsonAsync(
            $"/api/runs/{RunId}/approvals/{ApprovalId}/reject", new { reason = "不符政策" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Contains($"approval:{RunId}:{ApprovalId}:False:不符政策::approver", FakeAgentRunService.Calls);
    }

    // 「完全不給理由」是獨立的等價類:其餘決策測試都帶了 reason,而 ApprovalDecisionRequest? 是可為 null 的
    // [FromBody] —— 連 body 都不帶(審批者只按核准鍵)必須是 202 並轉發 reason=null,不是 400。
    [Fact]
    public async Task Approve_WithoutBody_IsAccepted_AndForwardsNullReason()
    {
        var client = _factory.CreateClient().WithToken(_factory.IssueToken("approver", "USER", "tenant-x"));

        var response = await client.PostAsync(
            $"/api/runs/{RunId}/approvals/{ApprovalId}/approve", content: null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Contains($"approval:{RunId}:{ApprovalId}:True:::approver", FakeAgentRunService.Calls);
    }

    // 決策權在 Backend:SoD 衝突(403)、已過期/不存在(404)、非 waiting 或 fingerprint 不符(409)
    // 必須原樣穿透,platform 不得改寫狀態碼或 ApiError body。
    // RejectingFactory 換掉 IAgentRunService 的實作(見下),與 EnabledApprovalFixture 共用的
    // FakeAgentRunService.Calls 無關,不能併入共用 fixture;三個狀態碼各自需要獨立實例。
    [Theory]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(409)]
    public async Task Decision_DownstreamRejection_PassesThroughVerbatim(int status)
    {
        using var factory = new RejectingFactory(status);
        var client = factory.CreateClient().WithToken(factory.IssueToken("approver", "USER", "tenant-x"));

        var response = await client.PostAsJsonAsync(
            $"/api/runs/{RunId}/approvals/{ApprovalId}/approve", new { reason = "r" });

        Assert.Equal(status, (int)response.StatusCode);
        var body = await response.ReadJsonAsync();
        Assert.Equal(status, body["status"]!.GetValue<int>());
        Assert.Equal("下游決策訊息", body["message"]!.GetValue<string>());
        // 透明代理:backend 自己的 code/correlationId 不得被 platform 改寫成本地推導值。
        Assert.Equal("backend_decision_code", body["code"]!.GetValue<string>());
        Assert.Equal("backend-trace-2", body["correlationId"]!.GetValue<string>());
    }

    /// <summary>共用的 FakeAgentRunService 一律回 202;決策失敗族群需要可控狀態碼,故在本檔自備。</summary>
    private sealed class RejectingRunService(int status) : IAgentRunService
    {
        private AgentProxyResponse Rejection() => new(
            status,
            $"{{\"timestamp\":\"2026-07-25T00:00:00Z\",\"status\":{status},\"code\":\"backend_decision_code\",\"message\":\"下游決策訊息\",\"correlationId\":\"backend-trace-2\",\"fieldErrors\":{{}}}}",
            null);

        public Task<AgentProxyResponse> DecideApprovalAsync(
            Guid runId, Guid approvalId, bool approve, string? reason, string? idempotencyKey,
            UserContext ctx, CancellationToken ct = default)
            => Task.FromResult(Rejection());

        public Task<AgentProxyResponse> ApprovalsAsync(Guid runId, UserContext ctx, CancellationToken ct = default)
            => Task.FromResult(Rejection());

        public Task<AgentProxyResponse> QueueAsync(string scope, string? cursor, int limit, UserContext ctx, CancellationToken ct = default)
            => Task.FromResult(Rejection());

        public Task<AgentProxyResponse> StartAsync(
            Guid agentId, string? message, string? idempotencyKey, UserContext ctx, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<AgentProxyResponse> GetAsync(Guid runId, UserContext ctx, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<AgentProxyResponse> EventsAsync(
            Guid runId, long afterSequence, int limit, UserContext ctx, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<AgentProxyResponse> ResumeAsync(
            Guid runId, string? message, long? expectedCheckpointVersion, string? idempotencyKey,
            UserContext ctx, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<AgentProxyResponse> CancelAsync(
            Guid runId, string? reason, string? idempotencyKey, UserContext ctx, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class RejectingFactory(int status)
        : TestWebAppFactory(new() { ["AGENT_WRITE_TOOLS_ENABLED"] = "true" })
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAgentRunService>();
                services.AddScoped<IAgentRunService>(_ => new RejectingRunService(status));
            });
        }
    }

    /// <summary>
    /// G4:List/decide 各案共用同一份 host。刻意只打開 agentWriteToolsEnabled,agentTestRunEnabled
    /// 維持關閉——這同時證明了 D3 gate(Program.cs 的 <c>!agentTestRunEnabled</c> 中介軟體)對 approval 路徑的
    /// <c>isApprovalRoute</c> 例外真的生效:商務審批者不會因為 ADMIN 專用的 D3 測試台被關掉而看不到待審項目。
    /// 若有人「簡化」這裡的設定(例如順手把 AGENT_TEST_RUN_ENABLED 也打開,或省略這個本來就是預設值的顯式鍵),
    /// 這個覆蓋語意會靜默消失,故 AGENT_TEST_RUN_ENABLED = "false" 即使等於預設值也保留顯式寫法。
    /// </summary>
    public sealed class EnabledApprovalFixture : TestWebAppFactory
    {
        public EnabledApprovalFixture()
            : base(new()
            {
                ["AGENT_WRITE_TOOLS_ENABLED"] = "true",
                ["AGENT_TEST_RUN_ENABLED"] = "false",
            })
        {
        }
    }
}
