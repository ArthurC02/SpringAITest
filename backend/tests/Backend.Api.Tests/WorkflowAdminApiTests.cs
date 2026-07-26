using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Backend.Api.Common;
using Backend.Api.Data.InMemory;
using Backend.Api.Orchestrators;
using Backend.Api.Workflows;

namespace Backend.Api.Tests;

public sealed class WorkflowAdminApiTests(TestWebAppFactory factory) : IClassFixture<TestWebAppFactory>
{
    private HttpClient Admin() { var c = factory.CreateInternalClient().WithTenant("demo-a").WithUser("system-admin").WithRole("ADMIN"); c.DefaultRequestHeaders.Add(IdentityHeaders.CapabilitiesHeader, "workflow.manage"); return c; }
    private static object Draft(string name = "Harness") => new { name, kind = "orchestrator", definition = new { schemaVersion = 1, kind = "orchestrator", nodes = Array.Empty<object>(), edges = Array.Empty<object>() }, ui_metadata = new { viewport = new { x = 0, y = 0, zoom = 1 } } };
    [Fact]
    public async Task Workflow_Lifecycle_UsesEtagAndImmutableRevision()
    { var client = Admin(); var create = await client.PostAsJsonAsync("/api/admin/workflows", Draft()); Assert.Equal(HttpStatusCode.Created, create.StatusCode); var json = await create.ReadJsonAsync(); var id = json["id"]!.GetValue<string>(); var etag = create.Headers.ETag!.ToString(); var validateReq = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/workflows/{id}/validate"); validateReq.Headers.TryAddWithoutValidation("If-Match", etag); var validate = await client.SendAsync(validateReq); Assert.Equal(HttpStatusCode.OK, validate.StatusCode); var publishReq = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/workflows/{id}/publish") { Content = JsonContent.Create(new { expected_draft_version = 1 }) }; publishReq.Headers.TryAddWithoutValidation("If-Match", etag); var publish = await client.SendAsync(publishReq); Assert.Equal(HttpStatusCode.OK, publish.StatusCode); var revisions = await client.GetAsync($"/api/admin/workflows/{id}/revisions"); Assert.Contains(WorkflowCompilerContracts.Current, await revisions.Content.ReadAsStringAsync()); }
    [Fact] public async Task Workflow_MissingCapability_IsForbidden() { var c = factory.CreateInternalClient().WithTenant("demo-a").WithUser("admin").WithRole("ADMIN"); Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/admin/workflows")).StatusCode); }
    [Fact] public async Task Workflow_DraftMutationWithoutEtag_IsPreconditionRequired() { var c = Admin(); var created = await c.PostAsJsonAsync("/api/admin/workflows", Draft("NoEtag")); var id = (await created.ReadJsonAsync())["id"]!.GetValue<string>(); Assert.Equal((HttpStatusCode)428, (await c.PutAsJsonAsync($"/api/admin/workflows/{id}/draft", Draft("Changed"))).StatusCode); }
    // 樂觀鎖的另一半:缺 If-Match 是 428(上一條),用「已被別人推進過的」ETag 是 409。
    // 只測 428 的話,把版本比對整段刪掉仍然全綠。
    [Fact]
    public async Task Workflow_DraftMutationWithStaleEtag_IsConflict()
    {
        var c = Admin();
        var created = await c.PostAsJsonAsync("/api/admin/workflows", Draft("StaleEtag"));
        var id = (await created.ReadJsonAsync())["id"]!.GetValue<string>();
        var stale = created.Headers.ETag!.ToString();

        using var first = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/workflows/{id}/draft") { Content = JsonContent.Create(Draft("Changed")) };
        first.Headers.TryAddWithoutValidation("If-Match", stale);
        var accepted = await c.SendAsync(first);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.NotEqual(stale, accepted.Headers.ETag!.ToString());

        using var replay = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/workflows/{id}/draft") { Content = JsonContent.Create(Draft("Again")) };
        replay.Headers.TryAddWithoutValidation("If-Match", stale);
        Assert.Equal(HttpStatusCode.Conflict, (await c.SendAsync(replay)).StatusCode);
    }

    // capability 是 ordinal 精確比對:近似字串一律 403。角色本身無關(controller 沒有 [AdminOnly]),
    // 所以「USER + 正確 capability」必須通行 —— 這一格把「ADMIN 不隱含 workflow.manage」的反面也釘住。
    [Theory]
    [InlineData("ADMIN", "workflow.manage.all", HttpStatusCode.Forbidden)]
    [InlineData("ADMIN", "WORKFLOW.MANAGE", HttpStatusCode.Forbidden)]
    [InlineData("ADMIN", "workflow.manag", HttpStatusCode.Forbidden)]
    [InlineData("ADMIN", "workflow.manage,extra", HttpStatusCode.Forbidden)]
    [InlineData("USER", "workflow.manage", HttpStatusCode.OK)]
    [InlineData("USER", "reporting.read workflow.manage", HttpStatusCode.OK)]
    public async Task Workflow_Manage_CapabilityMustMatchExactly(string role, string capabilities, HttpStatusCode expected)
    {
        var c = factory.CreateInternalClient().WithTenant("demo-a").WithUser("someone").WithRole(role);
        c.DefaultRequestHeaders.TryAddWithoutValidation(IdentityHeaders.CapabilitiesHeader, capabilities);

        Assert.Equal(expected, (await c.GetAsync("/api/admin/workflows")).StatusCode);
    }

    // D4 的門在 InternalTokenMiddleware **之後**(Program.cs:192),與 D7 的門(在之前)相反:
    // 帶了內部憑證才會看到 404,沒帶憑證仍是 401。這個差異沒測過,誤搬門的位置不會有任何測試變紅。
    [Theory]
    [InlineData("/api/admin/workflows")]
    [InlineData("/api/admin/orchestrators")]
    public async Task DesignerFeatureOff_Hides404AfterInternalToken_But401WithoutIt(string path)
    {
        using var disabled = new DesignerDisabledFactory();

        Assert.Equal(HttpStatusCode.Unauthorized, (await disabled.CreateClient().GetAsync(path)).StatusCode);

        var authorized = disabled.CreateClient();
        authorized.DefaultRequestHeaders.Add(InternalTokenMiddleware.HeaderName, TestWebAppFactory.InternalToken);
        Assert.Equal(HttpStatusCode.NotFound, (await authorized.GetAsync(path)).StatusCode);
    }

    // 旗標是 AND 串鏈(Program.cs:38-41):只開下游而沒開上游必須仍然 404。
    // 這是最容易誤設的部署組合,而且誤設的方向是「以為開了其實沒開」的相反面 —— 靜默放行。
    [Theory]
    [InlineData(false, false, "/api/orchestrator-runs/" + Placeholder)]
    [InlineData(true, false, "/api/orchestrator-runs/" + Placeholder)]
    [InlineData(false, true, "/api/chat-runs")]
    [InlineData(true, true, "/api/chat-runs")]
    public async Task DownstreamFlagsAlone_StayHidden_WithoutTheDesignerFlag(
        bool multiAgentDispatch, bool agentChat, string path)
    {
        using var factoryWithoutDesigner = new DesignerDisabledFactory(multiAgentDispatch, agentChat);
        var client = factoryWithoutDesigner.CreateClient();
        client.DefaultRequestHeaders.Add(InternalTokenMiddleware.HeaderName, TestWebAppFactory.InternalToken);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
    }

    private const string Placeholder = "11111111-1111-4111-8111-111111111111";

    /// <summary>WORKFLOW_DESIGNER_ENABLED 顯式關閉;下游旗標可獨立打開以驗證 AND 串鏈。</summary>
    private sealed class DesignerDisabledFactory(bool multiAgentDispatch = false, bool agentChat = false)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("WORKFLOW_DESIGNER_ENABLED", "false");
            builder.UseSetting("MULTI_AGENT_DISPATCH_ENABLED", multiAgentDispatch ? "true" : "false");
            builder.UseSetting("AGENT_CHAT_ENABLED", agentChat ? "true" : "false");
        }
    }

    [Fact] public async Task Orchestrator_InvalidTypedDefinition_IsRejected() { var c = Admin(); var response = await c.PostAsJsonAsync("/api/admin/orchestrators", new { name = "Root", description = "x", definition = new { workflow = new { id = Guid.NewGuid(), revision = 1 } } }); Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode); }
    [Fact] public void Orchestrator_VerifierCannotAlsoBeWorker() { var id = Guid.NewGuid(); var json = ValidDefinition(id, id); Assert.Contains(OrchestratorCanonicalizer.Validate(json), x => x.Contains("must not appear")); }
    [Fact] public void Orchestrator_UnknownPolicyFieldIsRejected() { var json = ValidDefinition(Guid.NewGuid(), Guid.NewGuid()).Replace("\"denialPolicy\":\"fail-closed\"", "\"denialPolicy\":\"fail-closed\",\"python\":\"bad\""); Assert.Contains(OrchestratorCanonicalizer.Validate(json), x => x.Contains("not allowed")); }
    [Fact] public void Orchestrator_NestedUnknownFieldIsRejected() { var json = ValidDefinition(Guid.NewGuid(), Guid.NewGuid()).Replace("\"allowedTools\":[]", "\"allowedTools\":[],\"escape\":true"); Assert.Contains(OrchestratorCanonicalizer.Validate(json), x => x.Contains("escape is not allowed")); }
    [Fact] public void Orchestrator_ContextToolsUseRegistryRisk_NotNameHeuristics() { var definition = ValidDefinition(Guid.NewGuid(), Guid.NewGuid()).Replace("\"allowedTools\":[]", "\"allowedTools\":[\"write_named_but_safe\",\"delete_document\",\"missing\"]"); var errors = OrchestratorCanonicalizer.ValidateContextTools(definition, [new("write_named_but_safe", "read"), new("delete_document", "privileged")]); Assert.DoesNotContain(errors, x => x.Contains("write_named_but_safe")); Assert.Contains(errors, x => x.Contains("delete_document") && x.Contains("risk")); Assert.Contains(errors, x => x.Contains("missing") && x.Contains("unknown")); }
    [Fact] public void Orchestrator_WriteCapableVerifierRevisionIsRejected() { var definition = """{"execution_roles":["verifier"],"allowed_tools":["write.document"],"output_contract":{"type":"verification-report"}}"""; Assert.Contains(OrchestratorReferencePolicy.ValidateAgentDefinition(definition, new(Guid.NewGuid(), 1, true)), x => x.Contains("read-only")); }
    [Fact]
    public async Task Orchestrator_InMemoryPublishAtomicallyRechecksReferencesAndWorkflowKind()
    {
        var skills = new InMemorySkillRepository(); var agents = new InMemoryAgentRepository(skills); var workflows = new InMemoryWorkflowRepository(); var repo = new InMemoryOrchestratorRepository(workflows, agents);
        var workflow = await workflows.CreateAsync("t", "wrong-kind", "agent-runtime", "{}", "{}", "u", default); var workflowId = workflow.Workflow!.Id;
        Assert.True(await workflows.MarkValidatedAsync("t", workflowId, 1, "{}", "{}", default));
        Assert.Equal(WorkflowWriteStatus.Success, (await workflows.PublishAsync("t", workflowId, 1, "{}", "{}", WorkflowCompilerContracts.Current, "u", default)).Status);
        var template = ValidDefinition(Guid.NewGuid(), Guid.NewGuid()); var definition = template.Replace(ExtractWorkflowId(template), workflowId.ToString("D"));
        var errors = await repo.ValidateReferencesAsync("t", definition, default); Assert.Contains(errors, x => x.Contains("orchestrator kind"));
        var created = await repo.CreateAsync("t", "root", "", definition, "u", default); Assert.True(await repo.MarkValidatedAsync("t", created.Orchestrator!.Id, 1, definition, default));
        Assert.Equal(OrchestratorWriteStatus.VersionConflict, (await repo.PublishAsync("t", created.Orchestrator.Id, 1, definition, "u", default)).Status);
    }
    private static string ExtractWorkflowId(string definition) => JsonNode.Parse(definition)!["workflow"]!["id"]!.GetValue<string>();
    [Fact]
    public void OrchestratorBudget_ReservesFirstVerifierChild()
    {
        var invalid = ValidDefinition(Guid.NewGuid(), Guid.NewGuid()).Replace("\"maxChildRuns\":2", "\"maxChildRuns\":1", StringComparison.Ordinal);
        Assert.Contains(OrchestratorCanonicalizer.Validate(invalid), x => x.Contains("maxTasks + 1", StringComparison.Ordinal));
    }
    private static string ValidDefinition(Guid verifier, Guid worker) => $$$"""{"instructions":"root","policy":{"dispatchMode":"bounded-parallel","joinPolicy":"repair","repairPolicy":"redispatch","aggregationPolicy":"verified-only","denialPolicy":"fail-closed"},"workflow":{"id":"{{{Guid.NewGuid():D}}}","revision":1},"verifier":{"agentId":"{{{verifier:D}}}","revision":1,"variant":"read-only","independent":true,"outputContract":{"type":"verification-report"}},"workerPool":[{"agentId":"{{{worker:D}}}","revision":1}],"workerPolicy":{"requiredAudience":[],"requiredCapabilities":[],"selection":"pinned-only"},"context":{"readOnly":true,"allowedTools":[],"knowledgeSources":[]},"audience":[],"capabilities":[],"budgets":{"maxContextRounds":1,"maxTasks":1,"maxChildRuns":2,"maxConcurrency":1,"maxRepairRounds":1,"tokenBudget":1,"timeoutSeconds":1}}""";
}
