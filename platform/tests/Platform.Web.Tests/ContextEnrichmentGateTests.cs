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
    // 邊界:gate 用的是 StartsWithSegments,子資源(往下一層)才是日後 proxy 真正會長成的形狀,
    // 也是這道 gate 存在的理由;裸前綴過關不代表 /api/contexts/{id} 也被擋。
    [InlineData("/api/contexts/abc-123")]
    public async Task ContextRoutes_FlagOff_AreHiddenBeforeAuthentication(string path)
    {
        using var factory = new TestWebAppFactory(
            multiAgentDispatchEnabled: true,
            contextEnrichmentEnabled: false);

        var response = await factory.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        (await response.ReadJsonAsync()).AssertApiError(404, "not_found");
    }

    /// <summary>
    /// 等價類的另一半:兩個旗標全開時 gate 中介軟體根本不註冊,回應不得再帶 gate 自寫的 ApiError。
    /// Platform 目前確實沒有 Context proxy,所以現況仍是 404 —— 但是路由未命中的空 body 404,
    /// 而非 gate 的 JSON。日後真的接上 proxy 時本測試應改為斷言被代理的回應。
    /// </summary>
    [Fact]
    public async Task ContextRoutes_FlagOn_ProduceNoGateApiError()
    {
        using var factory = new TestWebAppFactory(
            multiAgentDispatchEnabled: true,
            contextEnrichmentEnabled: true);

        var response = await factory.CreateClient().GetAsync("/api/contexts");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
    }
}
