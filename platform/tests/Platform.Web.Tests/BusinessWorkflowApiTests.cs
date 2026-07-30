using System.Net;
using System.Net.Http.Json;

namespace Platform.Web.Tests;

public sealed class BusinessWorkflowApiTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _factory;

    public BusinessWorkflowApiTests(TestWebAppFactory factory) => _factory = factory;

    [Fact]
    public async Task AuthenticatedRoutes_ProxyCrudExportAndValidate()
    {
        var client = _factory.AdminClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/business-workflows")).StatusCode);
        var created = await client.PostAsJsonAsync(
            "/api/business-workflows",
            new
            {
                definition = "name: quarterly-flow",
                simpleForm = new { templateId = "template-stats" },
            });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(
            "/api/business-workflows/quarterly-flow",
            created.Headers.Location!.ToString());
        var createdBody = await created.ReadJsonAsync();
        Assert.Equal("flow", createdBody["kind"]!.GetValue<string>());
        Assert.Equal("template-stats", createdBody["simpleForm"]!["templateId"]!.GetValue<string>());

        Assert.Equal(
            HttpStatusCode.OK,
            (await client.GetAsync("/api/business-workflows/quarterly-flow")).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PutAsJsonAsync(
                "/api/business-workflows/quarterly-flow",
                new { definition = "name: quarterly-flow" })).StatusCode);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await client.DeleteAsync("/api/business-workflows/quarterly-flow")).StatusCode);

        var export = await client.GetAsync("/api/business-workflows/quarterly-flow/export");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal("application/zip", export.Content.Headers.ContentType!.MediaType);

        var validate = await client.PostAsJsonAsync(
            "/api/business-workflows/validate",
            new { definition = "name: quarterly-flow" });
        Assert.Equal(HttpStatusCode.OK, validate.StatusCode);
        Assert.Contains("business-workflow-validate", FakeWorkflowEngineClient.EngineCalls);
    }

    [Fact]
    public async Task AnonymousRequest_Returns401()
        => Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await _factory.CreateClient().GetAsync("/api/business-workflows")).StatusCode);

    [Fact]
    public async Task User_CanRead_ButWriteRoutesReturn403()
    {
        var client = _factory.UserClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/business-workflows")).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.GetAsync("/api/business-workflows/quarterly-flow")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync(
                "/api/business-workflows", new { definition = "name: quarterly-flow" })).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.PutAsJsonAsync(
                "/api/business-workflows/quarterly-flow",
                new { definition = "name: quarterly-flow" })).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.DeleteAsync("/api/business-workflows/quarterly-flow")).StatusCode);
    }
}
