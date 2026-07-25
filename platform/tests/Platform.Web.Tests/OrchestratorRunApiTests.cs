using System.Net;
using System.Net.Http.Json;

namespace Platform.Web.Tests;

[Collection("EngineCalls")]
public sealed class OrchestratorRunApiTests
{
    private const string OrchestratorId = "66666666-6666-6666-6666-666666666666";
    private const string RunId = FakeOrchestratorRunService.RunIdText;

    [Theory]
    [InlineData("GET", "/api/orchestrator-runs/" + RunId)]
    [InlineData("GET", "/api/orchestrator-runs/" + RunId + "/events")]
    [InlineData("POST", "/api/orchestrator-runs/" + RunId + "/cancel")]
    public async Task LifecycleRoutes_AreAuthenticatedButDoNotRequireWorkflowManage(string method,string path)
    {
        using var factory=EnabledFactory();
        var anonymous=await factory.CreateClient().SendAsync(Request(method,path));
        Assert.Equal(HttpStatusCode.Unauthorized,anonymous.StatusCode);

        var caller=factory.CreateClient().WithToken(factory.IssueToken("owner","USER","tenant-x"));
        var response=await caller.SendAsync(Request(method,path));
        Assert.NotEqual(HttpStatusCode.Forbidden,response.StatusCode);
        Assert.Contains(FakeOrchestratorRunService.Calls,x=>x.EndsWith(":owner",StringComparison.Ordinal));
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
