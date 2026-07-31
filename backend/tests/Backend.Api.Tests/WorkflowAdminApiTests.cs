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

    // Resource-existence × If-Match 的交叉格:兩個 sibling controller 對「id 不存在 + 沒帶 If-Match」
    // 給的答案不一樣 —— WorkflowController.Update 先 `GetAsync ?? throw Missing(id)` 才求值 Version(),所以 404;
    // OrchestratorController.Update 把 Version() 當參數直接餵給 repo、沒有前置存在性檢查,所以先撞 428。
    // 帶了 If-Match 之後兩邊才一致回 404 —— 證明 428 純粹是檢查先後順序造成的,不是「不存在的資源也要求前置條件」。
    [Fact]
    public async Task DraftMutation_OnMissingId_WithoutEtag_DivergesBetweenWorkflowAndOrchestrator()
    {
        var c = Admin();
        var missing = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await c.PutAsJsonAsync($"/api/admin/workflows/{missing}/draft", Draft("Ghost"))).StatusCode);
        Assert.Equal((HttpStatusCode)428, (await c.PutAsync($"/api/admin/orchestrators/{missing}/draft", OrchestratorBody("Ghost", ValidDefinition(Guid.NewGuid(), Guid.NewGuid())))).StatusCode);

        using var withEtag = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/orchestrators/{missing}/draft") { Content = OrchestratorBody("Ghost", ValidDefinition(Guid.NewGuid(), Guid.NewGuid())) };
        withEtag.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(withEtag)).StatusCode);
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
    // Orchestrator 的 10 條路由過去只有 Create(invalid) 真的走過 HTTP;ETag 樂觀鎖與狀態碼這半邊
    // 全靠 Workflow 的對應測試「推論」。這條把 create → draft → validate → publish 的實際狀態碼
    // 釘在 controller 邊界上,順帶釘住「形狀合法但引用解不開 → validate 是 200 + valid:false,不是 4xx」。
    [Fact]
    public async Task Orchestrator_Lifecycle_UsesEtagAndReportsUnresolvableReferences()
    {
        var c = Admin();
        var definition = ValidDefinition(Guid.NewGuid(), Guid.NewGuid());
        var create = await c.PostAsync("/api/admin/orchestrators", OrchestratorBody("Root Lifecycle", definition));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var id = (await create.ReadJsonAsync())["id"]!.GetValue<string>();
        var stale = create.Headers.ETag!.ToString();

        Assert.Equal((HttpStatusCode)428, (await c.PutAsync($"/api/admin/orchestrators/{id}/draft", OrchestratorBody("Root Lifecycle", definition))).StatusCode);

        using var update = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/orchestrators/{id}/draft") { Content = OrchestratorBody("Root Renamed", definition) };
        update.Headers.TryAddWithoutValidation("If-Match", stale);
        var updated = await c.SendAsync(update);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var current = updated.Headers.ETag!.ToString();
        Assert.NotEqual(stale, current);

        using var replay = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/orchestrators/{id}/draft") { Content = OrchestratorBody("Root Again", definition) };
        replay.Headers.TryAddWithoutValidation("If-Match", stale);
        Assert.Equal(HttpStatusCode.Conflict, (await c.SendAsync(replay)).StatusCode);

        using var validateReq = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/orchestrators/{id}/validate");
        validateReq.Headers.TryAddWithoutValidation("If-Match", current);
        var validate = await c.SendAsync(validateReq);
        Assert.Equal(HttpStatusCode.OK, validate.StatusCode);
        var body = await validate.ReadJsonAsync();
        Assert.False(body["valid"]!.GetValue<bool>());
        Assert.Contains("workflow must pin active same-tenant published orchestrator kind", body["errors"]!.ToJsonString());

        // 沒通過 validate 的草稿不得 publish:draft_validated_version 仍是 null → 409(不是 200 也不是 422)。
        using var publish = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/orchestrators/{id}/publish") { Content = JsonContent.Create(new { expected_draft_version = 2 }) };
        publish.Headers.TryAddWithoutValidation("If-Match", current);
        Assert.Equal(HttpStatusCode.Conflict, (await c.SendAsync(publish)).StatusCode);
    }
    private static HttpContent OrchestratorBody(string name, string definition) => JsonContent.Create(new { name, description = "", definition = JsonNode.Parse(definition) });
    [Fact] public void Orchestrator_VerifierCannotAlsoBeWorker() { var id = Guid.NewGuid(); var json = ValidDefinition(id, id); Assert.Contains(OrchestratorCanonicalizer.Validate(json), x => x.Contains("must not appear")); }
    [Fact] public void Orchestrator_UnknownPolicyFieldIsRejected() { var json = ValidDefinition(Guid.NewGuid(), Guid.NewGuid()).Replace("\"denialPolicy\":\"fail-closed\"", "\"denialPolicy\":\"fail-closed\",\"python\":\"bad\""); Assert.Contains(OrchestratorCanonicalizer.Validate(json), x => x.Contains("not allowed")); }
    [Fact] public void Orchestrator_NestedUnknownFieldIsRejected() { var json = ValidDefinition(Guid.NewGuid(), Guid.NewGuid()).Replace("\"allowedTools\":[]", "\"allowedTools\":[],\"escape\":true"); Assert.Contains(OrchestratorCanonicalizer.Validate(json), x => x.Contains("escape is not allowed")); }
    // 上面兩條測的是「未知欄位名」;ValidateEnum 自己的值域拒絕路徑(欄位在、值是別的字串)沒被走過。
    [Fact] public void Orchestrator_UnknownPolicyEnumValueIsRejected() { var json = ValidDefinition(Guid.NewGuid(), Guid.NewGuid()).Replace("\"joinPolicy\":\"repair\"", "\"joinPolicy\":\"bogus\"", StringComparison.Ordinal); Assert.Contains(OrchestratorCanonicalizer.Validate(json), x => x == "definition.policy.joinPolicy must be one of: fail-fast, allow-partial, repair"); }
    // pin 的守門:GUID 必須是 canonical "D" 格式(TryParseExact),revision 必須 >= 1。
    // 既有測試的引用全來自 ValidDefinition 模板,壞形狀的 pin 從沒被驗證過。
    [Fact]
    public void Orchestrator_MalformedPinnedReferencesAreRejected()
    {
        var verifier = Guid.NewGuid(); var template = ValidDefinition(verifier, Guid.NewGuid());
        var nonCanonicalId = template.Replace($"\"id\":\"{ExtractWorkflowId(template)}\"", $"\"id\":\"{Guid.NewGuid():N}\"", StringComparison.Ordinal);
        Assert.Contains(OrchestratorCanonicalizer.Validate(nonCanonicalId), x => x == "definition.workflow must contain a canonical pinned id/revision");
        var zeroRevision = template.Replace($"\"agentId\":\"{verifier:D}\",\"revision\":1", $"\"agentId\":\"{verifier:D}\",\"revision\":0", StringComparison.Ordinal);
        Assert.Contains(OrchestratorCanonicalizer.Validate(zeroRevision), x => x == "definition.verifier must contain a canonical pinned agentId/revision");
    }
    // ValidateStringArray 有 6 個呼叫點,但所有測試餵的陣列都是空的 —— 重複值/空白值/非字串三個等價類全沒測。
    [Theory]
    [InlineData("[\"role:ADMIN\",\"role:ADMIN\"]")]
    [InlineData("[\"   \"]")]
    [InlineData("[1]")]
    public void Orchestrator_AudienceMustBeBoundedUniqueStringArray(string audience)
    {
        var json = ValidDefinition(Guid.NewGuid(), Guid.NewGuid()).Replace("\"audience\":[]", $"\"audience\":{audience}", StringComparison.Ordinal);
        Assert.Contains(OrchestratorCanonicalizer.Validate(json), x => x == "definition.audience must be a bounded unique string array");
    }
    // workerPool 的 1..MaxWorkers:每個既有測試都剛好用 1 個 worker,兩端的 off-point 都沒踩過。
    [Theory]
    [InlineData(0, true)]
    [InlineData(OrchestratorCanonicalizer.MaxWorkers, false)]
    [InlineData(OrchestratorCanonicalizer.MaxWorkers + 1, true)]
    public void Orchestrator_WorkerPoolSizeBoundary(int workers, bool rejected)
    {
        var worker = Guid.NewGuid(); var template = ValidDefinition(Guid.NewGuid(), worker);
        var pool = string.Join(",", Enumerable.Range(0, workers).Select(_ => $"{{\"agentId\":\"{Guid.NewGuid():D}\",\"revision\":1}}"));
        var json = template.Replace($"[{{\"agentId\":\"{worker:D}\",\"revision\":1}}]", $"[{pool}]", StringComparison.Ordinal);
        Assert.Equal(rejected, OrchestratorCanonicalizer.Validate(json).Contains($"definition.workerPool must contain 1..{OrchestratorCanonicalizer.MaxWorkers} pinned workers"));
    }
    // 只有 maxChildRuns/maxTasks 的交叉規則被邊界測過;limits 表裡另外五個上限的數字沒有任何測試背書 ——
    // 打錯一位數(100 → 1000)不會有測試變紅。每個欄位在此各配一組 on-point(上限剛好可接受)與 off-point。
    [Theory]
    [InlineData("maxContextRounds", 100)]
    [InlineData("maxConcurrency", 64)]
    [InlineData("maxRepairRounds", 100)]
    [InlineData("tokenBudget", 10000000)]
    [InlineData("timeoutSeconds", 86400)]
    public void Orchestrator_BudgetFieldBoundsAreEnforcedIndependently(string field, int max)
    {
        var expected = $"definition.budgets.{field} must be 1..{max}";
        Assert.DoesNotContain(BudgetErrors(field, max), x => x == expected);
        Assert.Contains(BudgetErrors(field, max + 1), x => x == expected);
        Assert.Contains(BudgetErrors(field, 0), x => x == expected);
    }
    private static IReadOnlyList<string> BudgetErrors(string field, int value) => OrchestratorCanonicalizer.Validate(ValidDefinition(Guid.NewGuid(), Guid.NewGuid()).Replace($"\"{field}\":1", $"\"{field}\":{value}", StringComparison.Ordinal));
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
