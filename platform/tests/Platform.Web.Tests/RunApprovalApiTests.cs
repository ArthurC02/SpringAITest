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
public sealed class RunApprovalApiTests
{
    private const string RunId = FakeAgentRunService.RunIdText;
    private const string ApprovalId = "66666666-6666-4666-8666-666666666666";

    // D7 的旗標獨立於 D3:即使 Agent 測試台(builder + testRun)整組打開,
    // AGENT_WRITE_TOOLS_ENABLED 關閉仍必須在認證之前 404。
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FeatureOff_HidesApprovalRoutesBeforeAuthentication(bool agentTestRunEnabled)
    {
        using var factory = new TestWebAppFactory(
            agentBuilderEnabled: agentTestRunEnabled,
            agentTestRunEnabled: agentTestRunEnabled,
            agentWriteToolsEnabled: false);
        var before = FakeAgentRunService.Calls.Count;

        var response = await factory.CreateClient().GetAsync($"/api/runs/{RunId}/approvals");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(before, FakeAgentRunService.Calls.Count);
    }

    /// <summary>
    /// 這個 factory 刻意只打開 agentWriteToolsEnabled,<c>agentTestRunEnabled</c> 維持關閉 ——
    /// 因此本測試同時證明了 D3 gate(Program.cs 的 <c>!agentTestRunEnabled</c> 中介軟體)對 approval 路徑的
    /// <c>isApprovalRoute</c> 例外真的生效:商務審批者不會因為 ADMIN 專用的 D3 測試台被關掉而看不到待審項目。
    /// 若有人「簡化」這裡的 factory 參數(例如順手把 agentTestRunEnabled 也打開),這個覆蓋會靜默消失。
    /// </summary>
    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("USER", HttpStatusCode.OK)]
    [InlineData("ADMIN", HttpStatusCode.OK)]
    public async Task EnabledApprovalList_RequiresAuthenticationButNotAdmin(string? role, HttpStatusCode expected)
    {
        using var factory = new TestWebAppFactory(
            agentWriteToolsEnabled: true, agentTestRunEnabled: false);
        var client = factory.CreateClient();
        if (role is not null)
        {
            client = client.WithToken(factory.IssueToken("business-approver", role, "tenant-x"));
        }

        var response = await client.GetAsync($"/api/runs/{RunId}/approvals");

        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.OK)
        {
            Assert.Equal("business-approver", FakeAgentRunService.LastContext!.UserId);
            Assert.Equal("tenant-x", FakeAgentRunService.LastContext.TenantCode);
        }
    }

    [Fact]
    public async Task Decision_ForwardsOnlySignedIdentityReasonAndIdempotencyKey()
    {
        using var factory = new TestWebAppFactory(agentWriteToolsEnabled: true);
        var client = factory.CreateClient().WithToken(factory.IssueToken("approver", "USER", "tenant-x"));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/runs/{RunId}/approvals/{ApprovalId}/approve")
        {
            Content = JsonContent.Create(new { reason = "reviewed" }),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", "approval-attempt-1");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Contains($"approval:{RunId}:{ApprovalId}:True:reviewed:approval-attempt-1:approver", FakeAgentRunService.Calls);
    }

    // reject 是 approve 的鏡像路由,在 platform 之前完全沒被執行過:必須確認 /reject 對應 approve=false
    // (是它決定了 service 層不去 kick 已核准的寫入,見 AgentRunServiceTests)。
    // 同時覆蓋「未帶 Idempotency-Key」的 off-point:轉發 null 而不是憑空生一把 key。
    [Fact]
    public async Task Reject_Returns202_WithApproveFalse_AndNoIdempotencyKey()
    {
        using var factory = new TestWebAppFactory(agentWriteToolsEnabled: true);
        var client = factory.CreateClient().WithToken(factory.IssueToken("approver", "USER", "tenant-x"));

        var response = await client.PostAsJsonAsync(
            $"/api/runs/{RunId}/approvals/{ApprovalId}/reject", new { reason = "不符政策" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Contains($"approval:{RunId}:{ApprovalId}:False:不符政策::approver", FakeAgentRunService.Calls);
    }

    // 決策權在 Backend:SoD 衝突(403)、已過期/不存在(404)、非 waiting 或 fingerprint 不符(409)
    // 必須原樣穿透,platform 不得改寫狀態碼或 ApiError body。
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
    }

    /// <summary>共用的 FakeAgentRunService 一律回 202;決策失敗族群需要可控狀態碼,故在本檔自備。</summary>
    private sealed class RejectingRunService(int status) : IAgentRunService
    {
        private AgentProxyResponse Rejection() => new(
            status,
            $"{{\"timestamp\":\"2026-07-25T00:00:00Z\",\"status\":{status},\"message\":\"下游決策訊息\",\"fieldErrors\":{{}}}}",
            null);

        public Task<AgentProxyResponse> DecideApprovalAsync(
            Guid runId, Guid approvalId, bool approve, string? reason, string? idempotencyKey,
            UserContext ctx, CancellationToken ct = default)
            => Task.FromResult(Rejection());

        public Task<AgentProxyResponse> ApprovalsAsync(Guid runId, UserContext ctx, CancellationToken ct = default)
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
        : TestWebAppFactory(agentWriteToolsEnabled: true)
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
}
