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
        string? bodyText = null, user = null, role = null; var handler = new Handler(async request => { bodyText = await request.Content!.ReadAsStringAsync(); user = request.Headers.GetValues(IdentityHeaders.UserHeader).Single(); role = request.Headers.GetValues(IdentityHeaders.RoleHeader).Single(); return Json(HttpStatusCode.OK, $$$"""{"valid":true,"canonicalDefinition":{"schemaVersion":1,"kind":"orchestrator","nodes":[],"edges":[]},"canonicalUiMetadata":{},"definitionSha256":"x","uiMetadataSha256":"y","compilerContractVersion":"{{{WorkflowCompilerContracts.Current}}}","errors":[]}"""); });
        var compiler = Build(handler); var result = await compiler.ValidateAsync("orchestrator", """{"schemaVersion":1,"kind":"orchestrator","nodes":[],"edges":[]}""", "{}", "demo-a", default);
        Assert.True(result.Valid); Assert.Equal(WorkflowCompilerContracts.Current, result.CompilerContractVersion); Assert.Equal("system-admin", user); Assert.Equal("ADMIN", role);
        using var body = JsonDocument.Parse(bodyText!); Assert.False(body.RootElement.TryGetProperty("kind", out _)); Assert.True(body.RootElement.TryGetProperty("definition", out _)); Assert.True(body.RootElement.TryGetProperty("ui_metadata", out _));
    }
    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "{}")]
    [InlineData(HttpStatusCode.OK, "not-json")]
    public async Task Validate_BadStatusOrJson_FailsClosed(HttpStatusCode status, string body)
    { var compiler = Build(new Handler(_ => Task.FromResult(Json(status, body)))); var ex = await Assert.ThrowsAsync<ApiException>(() => compiler.ValidateAsync("orchestrator", "{}", "{}", "demo-a", default)); Assert.Equal(502, ex.Status); }
    [Fact] public async Task Validate_TransportAndTimeout_FailClosed() { foreach (var error in new Exception[] { new HttpRequestException("down"), new TaskCanceledException("timeout") }) { var compiler = Build(new Handler(_ => Task.FromException<HttpResponseMessage>(error))); var ex = await Assert.ThrowsAsync<ApiException>(() => compiler.ValidateAsync("orchestrator", "{}", "{}", "demo-a", default)); Assert.Equal(502, ex.Status); } }
    [Fact] public async Task Validate_RejectsDriftedCompilerContract() { var compiler = Build(new Handler(_ => Task.FromResult(Json(HttpStatusCode.OK, """{"valid":true,"canonicalDefinition":{},"canonicalUiMetadata":{},"compilerContractVersion":"graph-ir/1","errors":[]}""")))); var ex = await Assert.ThrowsAsync<ApiException>(() => compiler.ValidateAsync("orchestrator", "{}", "{}", "demo-a", default)); Assert.Equal(502, ex.Status); }
    [Fact] public async Task Tools_UsesExactRegistryWireAndRisk() { string? path = null; var compiler = Build(new Handler(request => { path = request.RequestUri!.AbsolutePath; return Task.FromResult(Json(HttpStatusCode.OK, """[{"name":"search_documents","kind":"local","description":"x","risk":"read","returns":"json"},{"name":"delete_document","kind":"http","description":"x","risk":"privileged","returns":"json"}]""")); })); var tools = await compiler.ToolsAsync("demo-a", default); Assert.Equal("/tools", path); Assert.Equal(new[] { "search_documents", "delete_document" }, tools.Select(x => x.Name)); Assert.Equal(new[] { "read", "privileged" }, tools.Select(x => x.Risk)); }
    private static WorkflowDesignerCompiler Build(HttpMessageHandler handler) { var context = new DefaultHttpContext(); context.Request.Headers[IdentityHeaders.UserHeader] = "system-admin"; context.Request.Headers[IdentityHeaders.RoleHeader] = "ADMIN"; return new(new HttpClient(handler), "http://workflow", "token", new HttpContextAccessor { HttpContext = context }); }
    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request); }
}
