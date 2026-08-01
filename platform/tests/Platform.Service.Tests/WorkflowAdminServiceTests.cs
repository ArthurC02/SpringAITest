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

    // id 與 suffix 是兩個各自獨立的路徑分支,實際 controller 也各有只帶其中一個的動作:
    // GET {id}(Get/Disable)只有 id,GET catalog/nodes(Catalog)只有 suffix。
    [Theory]
    [InlineData(true, null, "http://backend/api/admin/workflows/11111111-1111-1111-1111-111111111111")]
    [InlineData(false, "catalog/nodes", "http://backend/api/admin/workflows/catalog/nodes")]
    public async Task IdAndSuffixPathSegmentsAreAppendedIndependently(
        bool withId, string? suffix, string expectedUri)
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.OK, "{}"));

        await Build(stub).SendAsync(
            HttpMethod.Get,
            "workflows",
            withId ? Id : (Guid?)null,
            suffix,
            Context);

        Assert.Equal(expectedUri, stub.LastRequest!.RequestUri!.ToString());
    }

    // If-Match 與 body 也是兩個獨立分支:draft/validate/publish 允許空 body(EmptyBodyBehavior.Allow)
    // 卻仍要帶 If-Match;Create 反過來只有 body、沒有 If-Match。
    [Fact]
    public async Task IfMatchAndBodyAreForwardedIndependently()
    {
        var withoutBody = new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.OK, "{}"));

        await Build(withoutBody).SendAsync(
            HttpMethod.Put,
            "workflows",
            Id,
            "draft",
            Context,
            "\"7\"",
            null);

        Assert.Equal("\"7\"", withoutBody.Header("If-Match"));
        Assert.Null(withoutBody.LastRequest!.Content);

        var withoutIfMatch = new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.Created, "{}"));

        await Build(withoutIfMatch).SendAsync(
            HttpMethod.Post,
            "workflows",
            null,
            null,
            Context,
            null,
            JsonDocument.Parse("""{"definition":{"schemaVersion":2}}""").RootElement.Clone());

        Assert.False(withoutIfMatch.HasHeader("If-Match"));
        Assert.Contains("\"schemaVersion\":2", withoutIfMatch.LastBody);
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
