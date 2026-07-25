using System.Net;

namespace Platform.Web.Tests;

/// <summary>
/// GET /api/features:AllowAnonymous、只暴露 rollout 布林旗標(camelCase),供前端決定入口顯示。
/// </summary>
public sealed class FeaturesApiTests
{
    [Fact]
    public async Task Features_FlagOff_Anonymous_Returns200_FalseBool()
    {
        using var factory = new TestWebAppFactory(agentBuilderEnabled: false);

        var resp = await factory.CreateClient().GetAsync("/api/features");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.False(body["agentBuilderEnabled"]!.GetValue<bool>());
        Assert.False(body["agentTestRunEnabled"]!.GetValue<bool>());
        Assert.False(body["workflowDesignerEnabled"]!.GetValue<bool>());
        Assert.False(body["multiAgentDispatchEnabled"]!.GetValue<bool>());
        Assert.Equal(4, body.AsObject().Count);
    }

    [Fact]
    public async Task Features_FlagOn_Anonymous_Returns200_TrueBool()
    {
        using var factory = new TestWebAppFactory(agentBuilderEnabled: true);

        var resp = await factory.CreateClient().GetAsync("/api/features");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.True(body["agentBuilderEnabled"]!.GetValue<bool>());
        Assert.False(body["agentTestRunEnabled"]!.GetValue<bool>());
        Assert.False(body["workflowDesignerEnabled"]!.GetValue<bool>());
        Assert.False(body["multiAgentDispatchEnabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Features_TestRunRequiresBothFlags()
    {
        using var factory = new TestWebAppFactory(
            agentBuilderEnabled: true,
            agentTestRunEnabled: true);

        var body = await (await factory.CreateClient().GetAsync("/api/features")).ReadJsonAsync();

        Assert.True(body["agentBuilderEnabled"]!.GetValue<bool>());
        Assert.True(body["agentTestRunEnabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Features_WorkflowDesignerFlagIsIndependent()
    {
        using var factory = new TestWebAppFactory(workflowDesignerEnabled: true);

        var body = await (await factory.CreateClient().GetAsync("/api/features")).ReadJsonAsync();

        Assert.False(body["agentBuilderEnabled"]!.GetValue<bool>());
        Assert.False(body["agentTestRunEnabled"]!.GetValue<bool>());
        Assert.True(body["workflowDesignerEnabled"]!.GetValue<bool>());
        Assert.False(body["multiAgentDispatchEnabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Features_MultiAgentDispatchRequiresWorkflowDesigner()
    {
        using var factory = new TestWebAppFactory(
            workflowDesignerEnabled: true,
            multiAgentDispatchEnabled: true);
        var body = await (await factory.CreateClient().GetAsync("/api/features")).ReadJsonAsync();
        Assert.True(body["multiAgentDispatchEnabled"]!.GetValue<bool>());
    }
}
