using System.Net;

namespace Platform.Web.Tests;

public sealed class HealthApiTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _factory;

    public HealthApiTests(TestWebAppFactory factory) => _factory = factory;

    // 容器探針:免 token、AllowAnonymous → 200。OTel filter 過濾的正是這條路徑。
    [Fact]
    public async Task Health_Anonymous_Returns200()
    {
        var client = _factory.CreateClient();

        var resp = await client.GetAsync("/actuator/health");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }
}
