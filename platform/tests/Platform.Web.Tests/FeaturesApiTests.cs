using System.Net;

namespace Platform.Web.Tests;

/// <summary>
/// GET /api/features(D1):AllowAnonymous、只暴露 agentBuilderEnabled 布林旗標(camelCase),供前端決定入口顯示。
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
        // 只暴露這一個欄位,不揭露其他組態。
        Assert.Single(body.AsObject());
    }

    [Fact]
    public async Task Features_FlagOn_Anonymous_Returns200_TrueBool()
    {
        using var factory = new TestWebAppFactory(agentBuilderEnabled: true);

        var resp = await factory.CreateClient().GetAsync("/api/features");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True((await resp.ReadJsonAsync())["agentBuilderEnabled"]!.GetValue<bool>());
    }
}
