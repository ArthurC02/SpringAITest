using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Backend.Api.Common;
using Backend.Api.Data.InMemory;
using Backend.Api.Orchestrators;
using Backend.Api.Workflows;

namespace Backend.Api.Tests;

public sealed class WorkflowAdminApiTests(TestWebAppFactory factory):IClassFixture<TestWebAppFactory>
{
    private HttpClient Admin(){var c=factory.CreateInternalClient().WithTenant("demo-a").WithUser("system-admin").WithRole("ADMIN");c.DefaultRequestHeaders.Add(IdentityHeaders.CapabilitiesHeader,"workflow.manage");return c;}
    private static object Draft(string name="Harness")=>new{name,kind="orchestrator",definition=new{schemaVersion=1,kind="orchestrator",nodes=Array.Empty<object>(),edges=Array.Empty<object>()},ui_metadata=new{viewport=new{x=0,y=0,zoom=1}}};
    [Fact]public async Task Workflow_Lifecycle_UsesEtagAndImmutableRevision()
    {var client=Admin();var create=await client.PostAsJsonAsync("/api/admin/workflows",Draft());Assert.Equal(HttpStatusCode.Created,create.StatusCode);var json=await create.ReadJsonAsync();var id=json["id"]!.GetValue<string>();var etag=create.Headers.ETag!.ToString();var validateReq=new HttpRequestMessage(HttpMethod.Post,$"/api/admin/workflows/{id}/validate");validateReq.Headers.TryAddWithoutValidation("If-Match",etag);var validate=await client.SendAsync(validateReq);Assert.Equal(HttpStatusCode.OK,validate.StatusCode);var publishReq=new HttpRequestMessage(HttpMethod.Post,$"/api/admin/workflows/{id}/publish"){Content=JsonContent.Create(new{expected_draft_version=1})};publishReq.Headers.TryAddWithoutValidation("If-Match",etag);var publish=await client.SendAsync(publishReq);Assert.Equal(HttpStatusCode.OK,publish.StatusCode);var revisions=await client.GetAsync($"/api/admin/workflows/{id}/revisions");Assert.Contains(WorkflowCompilerContracts.Current,await revisions.Content.ReadAsStringAsync());}
    [Fact]public async Task Workflow_MissingCapability_IsForbidden(){var c=factory.CreateInternalClient().WithTenant("demo-a").WithUser("admin").WithRole("ADMIN");Assert.Equal(HttpStatusCode.Forbidden,(await c.GetAsync("/api/admin/workflows")).StatusCode);}
    [Fact]public async Task Workflow_DraftMutationWithoutEtag_IsPreconditionRequired(){var c=Admin();var created=await c.PostAsJsonAsync("/api/admin/workflows",Draft("NoEtag"));var id=(await created.ReadJsonAsync())["id"]!.GetValue<string>();Assert.Equal((HttpStatusCode)428,(await c.PutAsJsonAsync($"/api/admin/workflows/{id}/draft",Draft("Changed"))).StatusCode);}
    [Fact]public async Task Orchestrator_InvalidTypedDefinition_IsRejected(){var c=Admin();var response=await c.PostAsJsonAsync("/api/admin/orchestrators",new{name="Root",description="x",definition=new{workflow=new{id=Guid.NewGuid(),revision=1}}});Assert.Equal(HttpStatusCode.UnprocessableEntity,response.StatusCode);}
    [Fact]public void Orchestrator_VerifierCannotAlsoBeWorker(){var id=Guid.NewGuid();var json=ValidDefinition(id,id);Assert.Contains(OrchestratorCanonicalizer.Validate(json),x=>x.Contains("must not appear"));}
    [Fact]public void Orchestrator_UnknownPolicyFieldIsRejected(){var json=ValidDefinition(Guid.NewGuid(),Guid.NewGuid()).Replace("\"denialPolicy\":\"fail-closed\"","\"denialPolicy\":\"fail-closed\",\"python\":\"bad\"");Assert.Contains(OrchestratorCanonicalizer.Validate(json),x=>x.Contains("not allowed"));}
    [Fact]public void Orchestrator_NestedUnknownFieldIsRejected(){var json=ValidDefinition(Guid.NewGuid(),Guid.NewGuid()).Replace("\"allowedTools\":[]","\"allowedTools\":[],\"escape\":true");Assert.Contains(OrchestratorCanonicalizer.Validate(json),x=>x.Contains("escape is not allowed"));}
    [Fact]public void Orchestrator_ContextToolsUseRegistryRisk_NotNameHeuristics(){var definition=ValidDefinition(Guid.NewGuid(),Guid.NewGuid()).Replace("\"allowedTools\":[]","\"allowedTools\":[\"write_named_but_safe\",\"delete_document\",\"missing\"]");var errors=OrchestratorCanonicalizer.ValidateContextTools(definition,[new("write_named_but_safe","read"),new("delete_document","privileged")]);Assert.DoesNotContain(errors,x=>x.Contains("write_named_but_safe"));Assert.Contains(errors,x=>x.Contains("delete_document")&&x.Contains("risk"));Assert.Contains(errors,x=>x.Contains("missing")&&x.Contains("unknown"));}
    [Fact]public void Orchestrator_WriteCapableVerifierRevisionIsRejected(){var definition="""{"execution_roles":["verifier"],"allowed_tools":["write.document"],"output_contract":{"type":"verification-report"}}""";Assert.Contains(OrchestratorReferencePolicy.ValidateAgentDefinition(definition,new(Guid.NewGuid(),1,true)),x=>x.Contains("read-only"));}
    [Fact]public async Task Orchestrator_InMemoryPublishAtomicallyRechecksReferencesAndWorkflowKind()
    {
        var skills=new InMemorySkillRepository();var agents=new InMemoryAgentRepository(skills);var workflows=new InMemoryWorkflowRepository();var repo=new InMemoryOrchestratorRepository(workflows,agents);
        var workflow=await workflows.CreateAsync("t","wrong-kind","agent-runtime","{}","{}","u",default);var workflowId=workflow.Workflow!.Id;
        Assert.True(await workflows.MarkValidatedAsync("t",workflowId,1,"{}","{}",default));
        Assert.Equal(WorkflowWriteStatus.Success,(await workflows.PublishAsync("t",workflowId,1,"{}","{}",WorkflowCompilerContracts.Current,"u",default)).Status);
        var template=ValidDefinition(Guid.NewGuid(),Guid.NewGuid());var definition=template.Replace(ExtractWorkflowId(template),workflowId.ToString("D"));
        var errors=await repo.ValidateReferencesAsync("t",definition,default);Assert.Contains(errors,x=>x.Contains("orchestrator kind"));
        var created=await repo.CreateAsync("t","root","",definition,"u",default);Assert.True(await repo.MarkValidatedAsync("t",created.Orchestrator!.Id,1,definition,default));
        Assert.Equal(OrchestratorWriteStatus.VersionConflict,(await repo.PublishAsync("t",created.Orchestrator.Id,1,definition,"u",default)).Status);
    }
    private static string ExtractWorkflowId(string definition)=>JsonNode.Parse(definition)!["workflow"]!["id"]!.GetValue<string>();
    [Fact] public void OrchestratorBudget_ReservesFirstVerifierChild()
    {
        var invalid=ValidDefinition(Guid.NewGuid(),Guid.NewGuid()).Replace("\"maxChildRuns\":2","\"maxChildRuns\":1",StringComparison.Ordinal);
        Assert.Contains(OrchestratorCanonicalizer.Validate(invalid),x=>x.Contains("maxTasks + 1",StringComparison.Ordinal));
    }
    private static string ValidDefinition(Guid verifier,Guid worker)=>$$$"""{"instructions":"root","policy":{"dispatchMode":"bounded-parallel","joinPolicy":"repair","repairPolicy":"redispatch","aggregationPolicy":"verified-only","denialPolicy":"fail-closed"},"workflow":{"id":"{{{Guid.NewGuid():D}}}","revision":1},"verifier":{"agentId":"{{{verifier:D}}}","revision":1,"variant":"read-only","independent":true,"outputContract":{"type":"verification-report"}},"workerPool":[{"agentId":"{{{worker:D}}}","revision":1}],"workerPolicy":{"requiredAudience":[],"requiredCapabilities":[],"selection":"pinned-only"},"context":{"readOnly":true,"allowedTools":[],"knowledgeSources":[]},"audience":[],"capabilities":[],"budgets":{"maxContextRounds":1,"maxTasks":1,"maxChildRuns":2,"maxConcurrency":1,"maxRepairRounds":1,"tokenBudget":1,"timeoutSeconds":1}}""";
}
