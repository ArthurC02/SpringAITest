using System.Net;
using System.Net.Http.Json;

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

    // D5 的 dispatch 旗標與 D4 designer 旗標各自 fail-closed,且都在認證之前 —— 帶不帶 token 都看不到端點。
    [Theory]
    [InlineData("GET", "/api/orchestrator-runs/" + RunId)]
    [InlineData("GET", "/api/orchestrator-runs/" + RunId + "/events")]
    [InlineData("POST", "/api/orchestrator-runs/" + RunId + "/cancel")]
    [InlineData("POST", "/api/admin/orchestrators/" + OrchestratorId + "/runs")]
    [InlineData("POST", "/api/admin/orchestrators/" + OrchestratorId + "/runs/")]
    public async Task DispatchFlagOff_HidesOrchestratorRunRoutesBeforeAuthentication(string method,string path)
    {
        using var factory=new TestWebAppFactory(workflowDesignerEnabled:true,multiAgentDispatchEnabled:false);
        var before=FakeOrchestratorRunService.Calls.Count;

        var response=await factory.CreateClient().SendAsync(Request(method,path));

        Assert.Equal(HttpStatusCode.NotFound,response.StatusCode);
        Assert.Equal(before,FakeOrchestratorRunService.Calls.Count);
    }

    // 派生旗標:multiAgentDispatchEnabled = workflowDesignerEnabled && MULTI_AGENT_DISPATCH_ENABLED。
    // 與 D3 的 TestFlagCannotEnableWhenBuilderIsOff 對稱 —— 單獨打開 dispatch 不得繞過 designer 的閘。
    [Fact]
    public async Task DispatchFlagCannotEnableWhenWorkflowDesignerIsOff()
    {
        using var factory=new TestWebAppFactory(workflowDesignerEnabled:false,multiAgentDispatchEnabled:true);
        var admin=factory.CreateClient().WithToken(
            factory.IssueToken("owner","ADMIN","tenant-x",new[]{"workflow.manage"}));

        Assert.Equal(HttpStatusCode.NotFound,
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

    private static TestWebAppFactory EnabledFactory()=>new(workflowDesignerEnabled:true,multiAgentDispatchEnabled:true);
    private static HttpRequestMessage Request(string method,string path)=>new(new HttpMethod(method),path)
    { Content=method=="POST"?JsonContent.Create(new { reason="stop" }):null };
}
