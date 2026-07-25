using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Backend.Api.Skills;

namespace Backend.Api.Tests;

public sealed class SkillExecutionArtifactTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _factory;

    public SkillExecutionArtifactTests(TestWebAppFactory factory) => _factory = factory;

    private HttpClient Admin(string tenant = "demo-a")
        => _factory.CreateInternalClient().WithTenant(tenant).WithRole("ADMIN").WithUser("admin-a");

    [Fact]
    public async Task ExactRevisionArtifact_DoesNotDriftWhenCurrentUpdates()
    {
        var client = Admin();
        var name = $"artifact-{Guid.NewGuid():N}";
        var rev1 = $"name: {name}\ndescription: 第一版\nrequired_role: USER\nflow:\n  - node: query_intake\n";
        var rev2 = $"name: {name}\ndescription: 第二版\nrequired_role: USER\nflow:\n  - node: query_intake\n";
        Assert.Equal(HttpStatusCode.Created,
            (await client.PostAsJsonAsync("/api/skills", new { definition = rev1 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await client.PutAsJsonAsync($"/api/skills/{name}", new { definition = rev2 })).StatusCode);

        var response = await client.GetAsync(
            $"/api/skills/{name}/revisions/1/execution-artifact");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var artifact = await response.ReadJsonAsync();
        Assert.Equal(1, artifact["revision"]!.GetValue<int>());
        Assert.Equal(rev1, artifact["definition"]!.GetValue<string>());
        Assert.Equal(
            SkillHash.Sha256(rev1),
            artifact["definition_sha256"]!.GetValue<string>());
        Assert.Null(artifact["package_base64"]);
    }

    [Fact]
    public async Task ExactRevisionArtifact_IsTenantScoped_AndAdminOnly()
    {
        var owner = Admin();
        var name = $"artifact-private-{Guid.NewGuid():N}";
        var yaml = $"name: {name}\ndescription: private\nrequired_role: USER\nflow:\n  - node: query_intake\n";
        _ = await owner.PostAsJsonAsync("/api/skills", new { definition = yaml });

        Assert.Equal(HttpStatusCode.NotFound, (await Admin("demo-b").GetAsync(
            $"/api/skills/{name}/revisions/1/execution-artifact")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.CreateInternalClient()
            .WithTenant("demo-a").WithRole("USER").WithUser("user-a").GetAsync(
                $"/api/skills/{name}/revisions/1/execution-artifact")).StatusCode);
    }
}
