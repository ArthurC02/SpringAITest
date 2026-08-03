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
        Assert.False(body["contextEnrichmentEnabled"]!.GetValue<bool>());
        Assert.False(body["agentChatEnabled"]!.GetValue<bool>());
        Assert.False(body["agentWriteToolsEnabled"]!.GetValue<bool>());
        Assert.Equal(7, body.AsObject().Count);
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

    // AND 閘的另一半:子旗標自己開、父旗標維持預設關 → 仍必須關(fail-closed)。
    // 只測「兩者皆開」的話,把 && 誤改成獨立旗標不會有任何測試失敗。
    [Fact]
    public async Task Features_TestRun_TrueWithoutBuilderEnabled_StaysFalse()
    {
        using var factory = new TestWebAppFactory(agentTestRunEnabled: true);

        var body = await (await factory.CreateClient().GetAsync("/api/features")).ReadJsonAsync();

        Assert.False(body["agentBuilderEnabled"]!.GetValue<bool>());
        Assert.False(body["agentTestRunEnabled"]!.GetValue<bool>());
    }

    // 02-spec §8:WORKFLOW_DESIGNER_ENABLED 只是管理 gate,已從 runtime readiness 依賴移除 ——
    // 關掉設計器不得順帶關掉 runtime kill switch,dispatch 只看自己的 env。
    [Fact]
    public async Task Features_MultiAgentDispatch_TrueWithoutWorkflowDesigner_StaysTrue()
    {
        using var factory = new TestWebAppFactory(multiAgentDispatchEnabled: true);

        var body = await (await factory.CreateClient().GetAsync("/api/features")).ReadJsonAsync();

        Assert.False(body["workflowDesignerEnabled"]!.GetValue<bool>());
        Assert.True(body["multiAgentDispatchEnabled"]!.GetValue<bool>());
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
    public async Task Features_ContextEnrichmentRequiresDispatch()
    {
        using var factory = new TestWebAppFactory(contextEnrichmentEnabled: true);

        var body = await (await factory.CreateClient().GetAsync("/api/features")).ReadJsonAsync();

        Assert.False(body["multiAgentDispatchEnabled"]!.GetValue<bool>());
        Assert.False(body["contextEnrichmentEnabled"]!.GetValue<bool>());
    }

    // AND 閘的另一半:父旗標(dispatch 鏈)全開、CONTEXT_ENRICHMENT_ENABLED 維持預設關 → 仍必須關。
    // 少了這格,把 `contextEnrichmentEnabled = multiAgentDispatchEnabled`(掉了自己那半個條件)
    // 的變異不會有任何測試失敗——現有測試裡 dispatch 開著時剛好都同時開了 CONTEXT_ENRICHMENT_ENABLED。
    [Fact]
    public async Task Features_ContextEnrichment_DispatchOnWithoutOwnFlag_StaysFalse()
    {
        using var factory = new TestWebAppFactory(multiAgentDispatchEnabled: true);

        var body = await (await factory.CreateClient().GetAsync("/api/features")).ReadJsonAsync();

        Assert.True(body["multiAgentDispatchEnabled"]!.GetValue<bool>());
        Assert.False(body["contextEnrichmentEnabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Features_ContextEnrichmentEnabledWithDispatch_IsExposed()
    {
        using var factory = new TestWebAppFactory(
            multiAgentDispatchEnabled: true,
            contextEnrichmentEnabled: true);

        var body = await (await factory.CreateClient().GetAsync("/api/features")).ReadJsonAsync();

        Assert.True(body["multiAgentDispatchEnabled"]!.GetValue<bool>());
        Assert.True(body["contextEnrichmentEnabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Features_AgentChatFlagIsIndependent()
    {
        using var factory = new TestWebAppFactory(
            agentChatEnabled: true,
            agentChatTenantAllowlist: "tenant-x");
        var body = await (await factory.CreateClient().GetAsync("/api/features")).ReadJsonAsync();
        Assert.True(body["agentChatEnabled"]!.GetValue<bool>());
        Assert.False(body["workflowDesignerEnabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Features_WriteToolsFlagIsIndependent()
    {
        using var factory = new TestWebAppFactory(agentWriteToolsEnabled: true);
        var body = await (await factory.CreateClient().GetAsync("/api/features")).ReadJsonAsync();
        Assert.True(body["agentWriteToolsEnabled"]!.GetValue<bool>());
        Assert.False(body["agentTestRunEnabled"]!.GetValue<bool>());
    }

    // 每個旗標的「獨立」測試都只在其他旗標全關的基準上開一個;全開這格補上另一端:
    // 兩條 AND 鏈(builder→testRun、dispatch→context)與三個獨立旗標同時開時,
    // 七個值必須全 true——任何把不相干旗標互相耦合成條件的改動會在這裡爆。
    [Fact]
    public async Task Features_AllFlagsOn_Anonymous_Returns200_AllTrue()
    {
        using var factory = new TestWebAppFactory(
            agentBuilderEnabled: true,
            agentTestRunEnabled: true,
            workflowDesignerEnabled: true,
            multiAgentDispatchEnabled: true,
            contextEnrichmentEnabled: true,
            agentWriteToolsEnabled: true,
            agentChatEnabled: true,
            agentChatTenantAllowlist: "tenant-x");

        var resp = await factory.CreateClient().GetAsync("/api/features");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.True(body["agentBuilderEnabled"]!.GetValue<bool>());
        Assert.True(body["agentTestRunEnabled"]!.GetValue<bool>());
        Assert.True(body["workflowDesignerEnabled"]!.GetValue<bool>());
        Assert.True(body["multiAgentDispatchEnabled"]!.GetValue<bool>());
        Assert.True(body["contextEnrichmentEnabled"]!.GetValue<bool>());
        Assert.True(body["agentChatEnabled"]!.GetValue<bool>());
        Assert.True(body["agentWriteToolsEnabled"]!.GetValue<bool>());
    }
}
