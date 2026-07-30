using System.Net;
using System.Text.Json;
using Platform.Service.Dtos;
using Platform.Service.Exceptions;

namespace Platform.Service.Tests;

public sealed class BusinessWorkflowServiceTests
{
    private static readonly UserContext Admin = new("admin-a", "demo-a", "ADMIN");

    private static BusinessWorkflowService Build(StubHttpMessageHandler stub)
        => new(TestBackend.Client(stub));

    [Fact]
    public async Task Create_PreservesLocationKindAndSimpleForm()
    {
        var stub = new StubHttpMessageHandler(_ =>
        {
            var response = TestHttp.Json(
                HttpStatusCode.Created,
                """{"name":"quarterly-flow","description":"flow","definition":"name: quarterly-flow","required_role":"USER","enabled":true,"current_revision":1,"created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-01T00:00:00Z","kind":"flow","simpleForm":{"templateId":"template-stats","form":{"topK":"5"}}}""");
            response.Headers.Location = new Uri("/api/business-workflows/quarterly-flow", UriKind.Relative);
            return response;
        });
        var requestForm = JsonSerializer.SerializeToElement(
            new { templateId = "template-stats", form = new { topK = "5" } });

        var result = await Build(stub).CreateAsync(
            new SkillUpsert("name: quarterly-flow", requestForm), Admin);

        Assert.Equal("http://backend/api/business-workflows", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal("/api/business-workflows/quarterly-flow", result.Location);
        Assert.Equal("flow", result.Workflow.Kind);
        Assert.Equal(
            "template-stats",
            result.Workflow.SimpleForm!.Value.GetProperty("templateId").GetString());
        using var sent = JsonDocument.Parse(stub.LastBody!);
        Assert.Equal(
            "template-stats",
            sent.RootElement.GetProperty("simpleForm").GetProperty("templateId").GetString());
    }

    [Fact]
    public async Task Update_PreservesKindAndSimpleForm()
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json(
            HttpStatusCode.OK,
            """{"name":"quarterly-flow","description":"flow","definition":"name: quarterly-flow","required_role":"USER","enabled":true,"current_revision":2,"created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-02T00:00:00Z","kind":"flow","simpleForm":{"templateId":"template-compare"}}"""));

        var result = await Build(stub).UpdateAsync(
            "quarterly-flow", new SkillUpsert("name: quarterly-flow"), Admin);

        Assert.Equal("flow", result.Kind);
        Assert.Equal(
            "template-compare",
            result.SimpleForm!.Value.GetProperty("templateId").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData(",\"kind\":\"agentic\"")]
    [InlineData(",\"kind\":\"unknown\"")]
    public async Task Create_MissingOrNonFlowKind_ThrowsControlled502(string kindJson)
    {
        var body =
            """{"name":"quarterly-flow","description":"flow","definition":"name: quarterly-flow","required_role":"USER","enabled":true,"current_revision":1,"created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-01T00:00:00Z""" +
            kindJson + "}";
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.Created, body));

        await Assert.ThrowsAsync<WorkflowInvocationException>(() => Build(stub).CreateAsync(
            new SkillUpsert("name: quarterly-flow"), Admin));
    }
}
