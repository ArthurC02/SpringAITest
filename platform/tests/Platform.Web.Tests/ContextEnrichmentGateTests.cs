using System.Net;

namespace Platform.Web.Tests;

/// <summary>
/// Platform 尚未代理 Context API；仍須在認證前封鎖其保留路徑，避免日後接入時在
/// rollout 關閉狀態意外暴露。此處驗證的是新 middleware 的邊界，而非不存在的 controller。
/// </summary>
public sealed class ContextEnrichmentGateTests
{
    [Theory]
    [InlineData("/api/contexts")]
    [InlineData("/api/context-views")]
    [InlineData("/api/context-policies")]
    public async Task ContextRoutes_FlagOff_AreHiddenBeforeAuthentication(string path)
    {
        using var factory = new TestWebAppFactory(
            workflowDesignerEnabled: true,
            multiAgentDispatchEnabled: true,
            contextEnrichmentEnabled: false);

        var response = await factory.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.ReadJsonAsync();
        Assert.Equal(404, body["status"]!.GetValue<int>());
        Assert.False(string.IsNullOrWhiteSpace(body["message"]!.GetValue<string>()));
    }
}
