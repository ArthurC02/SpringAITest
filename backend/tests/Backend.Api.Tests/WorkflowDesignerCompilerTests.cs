using System.Net;
using System.Text;
using System.Text.Json;
using Backend.Api.Common;
using Backend.Api.Workflows;
using Microsoft.AspNetCore.Http;

namespace Backend.Api.Tests;

public sealed class WorkflowDesignerCompilerTests
{
    [Fact]
    public async Task Validate_UsesFrozenWireContractAndIdentityHeaders()
    {
        string? bodyText = null, user = null, role = null, internalToken = null, tenant = null; var handler = new Handler(async request => { bodyText = await request.Content!.ReadAsStringAsync(); user = request.Headers.GetValues(IdentityHeaders.UserHeader).Single(); role = request.Headers.GetValues(IdentityHeaders.RoleHeader).Single(); internalToken = request.Headers.GetValues("X-Internal-Token").Single(); tenant = request.Headers.GetValues(IdentityHeaders.TenantHeader).Single(); return Json(HttpStatusCode.OK, $$$"""{"valid":true,"canonicalDefinition":{"schemaVersion":1,"kind":"orchestrator","nodes":[],"edges":[]},"canonicalUiMetadata":{},"definitionSha256":"x","uiMetadataSha256":"y","compilerContractVersion":"{{{WorkflowCompilerContracts.Current}}}","errors":[]}"""); });
        var compiler = Build(handler); var result = await compiler.ValidateAsync("orchestrator", """{"schemaVersion":1,"kind":"orchestrator","nodes":[],"edges":[]}""", "{}", "demo-a", default);
        Assert.True(result.Valid); Assert.Equal(WorkflowCompilerContracts.Current, result.CompilerContractVersion); Assert.Equal("system-admin", user); Assert.Equal("ADMIN", role);
        // 內部信任邊界:Workflow 的 /workflow-designer/* 由 X-Internal-Token 守門,漏送這個 header 整條路徑都會 401。
        Assert.Equal("token", internalToken); Assert.Equal("demo-a", tenant);
        using var body = JsonDocument.Parse(bodyText!); Assert.False(body.RootElement.TryGetProperty("kind", out _)); Assert.True(body.RootElement.TryGetProperty("definition", out _)); Assert.True(body.RootElement.TryGetProperty("ui_metadata", out _));
    }

    // valid:false 是**正常回傳**(使用者看到驗證錯誤的唯一路徑),不得 fail-closed 成 502;
    // 而且必須原封不動回傳原始 definition/ui_metadata —— 無效圖沒有 canonical 形式可用。
    [Fact]
    public async Task Validate_InvalidGraph_ReturnsErrorsWithoutCanonicalisation()
    {
        const string definition = """{"schemaVersion":1,"kind":"orchestrator","nodes":[],"edges":[]}""";
        var compiler = Build(new Handler(_ => Task.FromResult(Json(HttpStatusCode.OK, $$$"""
            {"valid":false,"canonicalDefinition":null,"canonicalUiMetadata":null,"compilerContractVersion":"{{{WorkflowCompilerContracts.Current}}}","errors":[{"path":"nodes[0]","code":"unreachable","message":"node is unreachable","nodeId":"n1","edgeId":null}]}
            """))));

        var result = await compiler.ValidateAsync("orchestrator", definition, "{}", "demo-a", default);

        Assert.False(result.Valid);
        Assert.Equal(definition, result.CanonicalDefinition);
        Assert.Equal("{}", result.CanonicalUiMetadata);
        var error = Assert.Single(result.Errors);
        Assert.Equal(("nodes[0]", "node is unreachable", "n1"), (error.Field, error.Message, error.NodeId));
    }

    // valid:true 卻沒有 canonical 產物 = 契約被違反,只能 fail closed(絕不能拿原始位元當 canonical 存進 revision)。
    [Theory]
    [InlineData("""{"valid":true,"canonicalUiMetadata":{},"compilerContractVersion":"PLACEHOLDER","errors":[]}""")]
    [InlineData("""{"valid":true,"canonicalDefinition":{},"compilerContractVersion":"PLACEHOLDER","errors":[]}""")]
    public async Task Validate_ValidWithoutCanonicalArtifacts_FailsClosed(string template)
    {
        var compiler = Build(new Handler(_ => Task.FromResult(Json(
            HttpStatusCode.OK, template.Replace("PLACEHOLDER", WorkflowCompilerContracts.Current, StringComparison.Ordinal)))));

        var ex = await Assert.ThrowsAsync<ApiException>(
            () => compiler.ValidateAsync("orchestrator", "{}", "{}", "demo-a", default));
        Assert.Equal(502, ex.Status);
    }

    // 呼叫者身分缺失時必須在**送出請求之前**失敗:Workflow 依這兩個 header 做授權決定,
    // 送出空身分等於讓 designer 端以匿名身分編譯。
    [Theory]
    [InlineData(null, "ADMIN")]
    [InlineData("system-admin", null)]
    [InlineData("   ", "ADMIN")]
    public async Task Compiler_BlankCallerIdentity_FailsClosedWithoutSendingRequest(string? user, string? role)
    {
        var sent = 0;
        var compiler = Build(new Handler(_ => { sent++; return Task.FromResult(Json(HttpStatusCode.OK, "{}")); }), user, role);

        var ex = await Assert.ThrowsAsync<ApiException>(
            () => compiler.ValidateAsync("orchestrator", "{}", "{}", "demo-a", default));
        Assert.Equal(502, ex.Status);
        Assert.Equal(0, sent);
    }

    // catalog / simulate 的 path 字串是跨服務契約的一部分(Workflow 端沒有別名路由)。
    [Fact]
    public async Task CatalogAndSimulate_UseExactPaths()
    {
        string? catalogPath = null, simulatePath = null;
        var catalog = Build(new Handler(request => { catalogPath = request.RequestUri!.AbsolutePath; return Task.FromResult(Json(HttpStatusCode.OK, "{}")); }));
        await catalog.CatalogAsync("demo-a", default);
        var simulate = Build(new Handler(request => { simulatePath = request.RequestUri!.AbsolutePath; return Task.FromResult(Json(HttpStatusCode.OK, "{}")); }));
        await simulate.SimulateAsync("orchestrator", "{}", "{}", "demo-a", default);

        Assert.Equal("/workflow-designer/catalog/nodes", catalogPath);
        Assert.Equal("/workflow-designer/simulate", simulatePath);
    }
    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "{}")]
    [InlineData(HttpStatusCode.OK, "not-json")]
    public async Task Validate_BadStatusOrJson_FailsClosed(HttpStatusCode status, string body)
    { var compiler = Build(new Handler(_ => Task.FromResult(Json(status, body)))); var ex = await Assert.ThrowsAsync<ApiException>(() => compiler.ValidateAsync("orchestrator", "{}", "{}", "demo-a", default)); Assert.Equal(502, ex.Status); }
    [Fact] public async Task Validate_TransportAndTimeout_FailClosed() { foreach (var error in new Exception[] { new HttpRequestException("down"), new TaskCanceledException("timeout") }) { var compiler = Build(new Handler(_ => Task.FromException<HttpResponseMessage>(error))); var ex = await Assert.ThrowsAsync<ApiException>(() => compiler.ValidateAsync("orchestrator", "{}", "{}", "demo-a", default)); Assert.Equal(502, ex.Status); } }
    [Fact] public async Task Validate_RejectsDriftedCompilerContract() { var compiler = Build(new Handler(_ => Task.FromResult(Json(HttpStatusCode.OK, """{"valid":true,"canonicalDefinition":{},"canonicalUiMetadata":{},"compilerContractVersion":"graph-ir/1","errors":[]}""")))); var ex = await Assert.ThrowsAsync<ApiException>(() => compiler.ValidateAsync("orchestrator", "{}", "{}", "demo-a", default)); Assert.Equal(502, ex.Status); }
    [Fact] public async Task Tools_UsesExactRegistryWireAndRisk() { string? path = null; var compiler = Build(new Handler(request => { path = request.RequestUri!.AbsolutePath; return Task.FromResult(Json(HttpStatusCode.OK, """[{"name":"search_documents","kind":"local","description":"x","risk":"read","returns":"json"},{"name":"delete_document","kind":"http","description":"x","risk":"privileged","returns":"json"}]""")); })); var tools = await compiler.ToolsAsync("demo-a", default); Assert.Equal("/tools", path); Assert.Equal(new[] { "search_documents", "delete_document" }, tools.Select(x => x.Name)); Assert.Equal(new[] { "read", "privileged" }, tools.Select(x => x.Risk)); }
    private static WorkflowDesignerCompiler Build(HttpMessageHandler handler, string? user = "system-admin", string? role = "ADMIN") { var context = new DefaultHttpContext(); if (user is not null) context.Request.Headers[IdentityHeaders.UserHeader] = user; if (role is not null) context.Request.Headers[IdentityHeaders.RoleHeader] = role; return new(new HttpClient(handler), "http://workflow", "token", new HttpContextAccessor { HttpContext = context }); }
    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request); }
}
