using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Platform.Service.Dtos;
using Platform.Service.Exceptions;

namespace Platform.Service.Tests;

public sealed class WorkflowAdminServiceTests
{
    private static readonly Guid Id = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly UserContext Context = new(
        "system-admin",
        "demo-a",
        "USER",
        new[] { "workflow.manage" });

    private static WorkflowAdminService Build(StubHttpMessageHandler stub) =>
        new(TestBackend.Client(stub));

    [Fact]
    public async Task WorkflowDraft_ForwardsIdentityCapabilityBodyAndIfMatch()
    {
        var response = TestHttp.Json(HttpStatusCode.Conflict, """{"status":409}""");
        response.Headers.ETag = new EntityTagHeaderValue("\"8\"");
        var stub = new StubHttpMessageHandler(_ => response);
        var body = JsonDocument.Parse("""{"definition":{"schemaVersion":1}}""").RootElement.Clone();

        var result = await Build(stub).SendAsync(
            HttpMethod.Put,
            "workflows",
            Id,
            "draft",
            Context,
            "\"7\"",
            body);

        Assert.Equal($"http://backend/api/admin/workflows/{Id:D}/draft", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal("\"7\"", stub.Header("If-Match"));
        Assert.Equal("workflow.manage", stub.Header("X-User-Capabilities"));
        Assert.Equal("demo-a", stub.Header("X-Tenant-Id"));
        Assert.Contains("\"schemaVersion\":1", stub.LastBody);
        Assert.Equal(409, result.Status);
        Assert.Equal("\"8\"", result.ETag);
    }

    [Fact]
    public async Task OrchestratorRevision_UsesBoundedCanonicalPath()
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.OK, "[]"));

        await Build(stub).SendAsync(
            HttpMethod.Get,
            "orchestrators",
            Id,
            "revisions",
            Context);

        Assert.Equal(
            $"http://backend/api/admin/orchestrators/{Id:D}/revisions",
            stub.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task UnknownResourceIsRejectedLocally()
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.OK, "{}"));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Build(stub).SendAsync(HttpMethod.Get, "agents", null, null, Context));

        Assert.Null(stub.LastRequest);
    }

    [Fact]
    public async Task Downstream5xxIsMappedToControlledGatewayFailure()
    {
        var stub = new StubHttpMessageHandler(_ =>
            TestHttp.Json(HttpStatusCode.InternalServerError, """{"detail":"secret"}"""));

        await Assert.ThrowsAsync<WorkflowInvocationException>(() =>
            Build(stub).SendAsync(HttpMethod.Get, "workflows", null, null, Context));
    }
}
