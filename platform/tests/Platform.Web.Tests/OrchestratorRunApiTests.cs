using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.Service;
using Platform.Service.Abstractions;

namespace Platform.Web.Tests;

[Collection("EngineCalls")]
public sealed class OrchestratorRunApiTests
{
    private const string OrchestratorId = "66666666-6666-6666-6666-666666666666";
    private const string RunId = FakeOrchestratorRunService.RunIdText;

    // get/events/cancel 只要求「已認證的 owner」(USER 亦可,無 workflow.manage);start 才要 capability。
    // 期望值寫死成明確的狀態碼與完整的呼叫字串:NotEqual(Forbidden) 會被 500 蒙混過關,
    // EndsWith(":owner") 則會被同一組 theory 其他案例留在靜態清單裡的紀錄成全。
    [Theory]
    [InlineData("GET", "/api/orchestrator-runs/" + RunId, HttpStatusCode.OK, "get:" + RunId + ":owner")]
    [InlineData("GET", "/api/orchestrator-runs/" + RunId + "/events", HttpStatusCode.OK, "events:" + RunId + ":0:100:owner")]
    [InlineData("POST", "/api/orchestrator-runs/" + RunId + "/cancel", HttpStatusCode.Accepted, "cancel:" + RunId + ":stop::owner")]
    public async Task LifecycleRoutes_AreAuthenticatedButDoNotRequireWorkflowManage(
        string method,string path,HttpStatusCode expected,string expectedCall)
    {
        using var factory=EnabledFactory();
        var anonymous=await factory.CreateClient().SendAsync(Request(method,path));
        Assert.Equal(HttpStatusCode.Unauthorized,anonymous.StatusCode);

        var caller=factory.CreateClient().WithToken(factory.IssueToken("owner","USER","tenant-x"));
        var response=await caller.SendAsync(Request(method,path));
        Assert.Equal(expected,response.StatusCode);
        Assert.Contains(expectedCall,FakeOrchestratorRunService.Calls);
    }

    // cancel 的 body 是 CancelRequest?(EmptyBodyBehavior 之外的 nullable 綁定):完全不帶 body 仍須成立,
    // reason 為 null 而非 400 —— 前端「直接取消」不帶理由是既有用法。
    [Fact]
    public async Task Cancel_WithoutBody_IsAccepted_AndForwardsNullReason()
    {
        using var factory=EnabledFactory();
        var caller=factory.CreateClient().WithToken(factory.IssueToken("owner","USER","tenant-x"));

        var response=await caller.PostAsync($"/api/orchestrator-runs/{RunId}/cancel",content:null);

        Assert.Equal(HttpStatusCode.Accepted,response.StatusCode);
        Assert.Contains($"cancel:{RunId}:::owner",FakeOrchestratorRunService.Calls);
    }

    // dispatch 關閉時,runtime 與 admin 兩組路由都在認證之前 fail-closed —— 帶不帶 token 都看不到端點。
    [Theory]
    [InlineData("GET", "/api/orchestrator-runs/" + RunId)]
    [InlineData("GET", "/api/orchestrator-runs/" + RunId + "/events")]
    [InlineData("POST", "/api/orchestrator-runs/" + RunId + "/cancel")]
    [InlineData("POST", "/api/admin/orchestrators/" + OrchestratorId + "/runs")]
    [InlineData("POST", "/api/admin/orchestrators/" + OrchestratorId + "/runs/")]
    public async Task DispatchFlagOff_HidesOrchestratorRunRoutesBeforeAuthentication(string method,string path)
    {
        using var factory=new TestWebAppFactory(multiAgentDispatchEnabled:false);
        var before=FakeOrchestratorRunService.Calls.Count;

        var response=await factory.CreateClient().SendAsync(Request(method,path));

        Assert.Equal(HttpStatusCode.NotFound,response.StatusCode);
        Assert.Equal(before,FakeOrchestratorRunService.Calls.Count);
    }

    // 02-spec §8:dispatch 只看 MULTI_AGENT_DISPATCH_ENABLED,designer 已從 runtime readiness 移除。
    // 兩個 gate 的作用面因此不同,必須同時釘住兩邊:
    //   runtime 路由 /api/orchestrator-runs/* → designer 關著也照常可達(匿名時是 401,不再是 404);
    //   管理路由 /api/admin/orchestrators/* → 仍受 designer 這個「管理 gate」保護,維持 404。
    [Fact]
    public async Task DispatchIsIndependentOfWorkflowDesigner_ButAdminSurfaceStaysGated()
    {
        using var factory=new TestWebAppFactory(workflowDesignerEnabled:false,multiAgentDispatchEnabled:true);
        var admin=factory.CreateClient().WithToken(
            factory.IssueToken("owner","ADMIN","tenant-x",new[]{"workflow.manage"}));

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await factory.CreateClient().GetAsync($"/api/orchestrator-runs/{RunId}")).StatusCode);
        using var start=new HttpRequestMessage(HttpMethod.Post,"/api/admin/orchestrators/"+OrchestratorId+"/runs")
        { Content=JsonContent.Create(new { message="m",conversationId="c" }) };
        Assert.Equal(HttpStatusCode.NotFound,(await admin.SendAsync(start)).StatusCode);
    }

    [Fact]
    public async Task Start_RequiresWorkflowManage_AndForwardsIdempotency()
    {
        using var factory=EnabledFactory();
        var user=factory.CreateClient().WithToken(factory.IssueToken("owner","ADMIN","tenant-x"));
        using var denied=new HttpRequestMessage(HttpMethod.Post,"/api/admin/orchestrators/"+OrchestratorId+"/runs") { Content=JsonContent.Create(new { message="m",conversationId="c" }) };
        Assert.Equal(HttpStatusCode.Forbidden,(await user.SendAsync(denied)).StatusCode);
        var admin=factory.CreateClient().WithToken(factory.IssueToken("owner","ADMIN","tenant-x",new[]{"workflow.manage"}));
        using var allowed=new HttpRequestMessage(HttpMethod.Post,"/api/admin/orchestrators/"+OrchestratorId+"/runs") { Content=JsonContent.Create(new { message="m",conversationId="c" }) };
        allowed.Headers.TryAddWithoutValidation("Idempotency-Key","root-key");
        Assert.Equal(HttpStatusCode.Accepted,(await admin.SendAsync(allowed)).StatusCode);
        Assert.Contains(FakeOrchestratorRunService.Calls,x=>x.Contains(":root-key:owner",StringComparison.Ordinal));
    }

    // 公開 start body 沒有 context 欄位:呼叫端硬塞的 context 是不可回收的授權,必須在 Web 邊界就被丟掉,
    // 既不得進 Backend 的 root_input,也不得混進 Workflow 的 dispatch body(那裡只送 command_id + 空 context)。
    // 走真 OrchestratorRunService(其餘測試用 fake),兩個下游各自攔截後逐字檢查。
    [Fact]
    public async Task Start_CallerSuppliedContextNeverReachesBackendOrWorkflow()
    {
        using var factory=new RealServiceFactory();
        factory.Backend.Reset(HttpStatusCode.Accepted,
            $$"""{"id":"{{RunId}}","status":"queued","state_version":1,"command_id":"77777777-7777-7777-7777-777777777777"}""");
        factory.Workflow.Reset(HttpStatusCode.Accepted,"{}");
        var admin=factory.CreateClient().WithToken(
            factory.IssueToken("owner","ADMIN","tenant-x",new[]{"workflow.manage"}));
        using var start=new HttpRequestMessage(HttpMethod.Post,"/api/admin/orchestrators/"+OrchestratorId+"/runs")
        {
            Content=JsonContent.Create(new
            {
                message="m",
                conversationId="c",
                context=new { granted_tools=new[]{"runtime.write_evidence"},tenant="other-tenant" },
            }),
        };

        var response=await admin.SendAsync(start);

        Assert.Equal(HttpStatusCode.Accepted,response.StatusCode);
        foreach(var forwarded in new[]
        {
            Encoding.UTF8.GetString(factory.Backend.Body!),
            Encoding.UTF8.GetString(factory.Workflow.Body!),
        })
        {
            Assert.DoesNotContain("granted_tools",forwarded,StringComparison.Ordinal);
            Assert.DoesNotContain("other-tenant",forwarded,StringComparison.Ordinal);
        }
    }

    // start 路由住在 /api/admin/orchestrators 之下,仍受 designer 這個管理 gate 保護,
    // 所以完整流程的 fixture 兩個旗標都要開(runtime 路由本身已不需要 designer)。
    private static TestWebAppFactory EnabledFactory()=>new(workflowDesignerEnabled:true,multiAgentDispatchEnabled:true);
    private static HttpRequestMessage Request(string method,string path)=>new(new HttpMethod(method),path)
    { Content=method=="POST"?JsonContent.Create(new { reason="stop" }):null };

    /// <summary>保留真 OrchestratorRunService,只把它的兩個下游(backend、workflow)換成攔截 handler。</summary>
    private sealed class RealServiceFactory : TestWebAppFactory
    {
        public CapturingBackendHandler Backend { get; }=new();
        public CapturingBackendHandler Workflow { get; }=new();

        public RealServiceFactory()
            : base(workflowDesignerEnabled:true,multiAgentDispatchEnabled:true)
        {
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<BackendClient>();
                services.AddHttpClient<BackendClient>().ConfigurePrimaryHttpMessageHandler(()=>Backend);
                services.RemoveAll<IOrchestratorRunService>();
                services.AddHttpClient<IOrchestratorRunService,OrchestratorRunService>()
                    .ConfigurePrimaryHttpMessageHandler(()=>Workflow);
            });
        }
    }
}
