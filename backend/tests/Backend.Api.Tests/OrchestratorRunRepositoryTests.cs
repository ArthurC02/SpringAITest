using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Backend.Api.AgentRuns;
using Backend.Api.Agents;
using Backend.Api.OrchestratorRuns;
using Backend.Api.Orchestrators;
using Backend.Api.Workflows;

namespace Backend.Api.Tests;

public sealed class OrchestratorRunRepositoryTests
{
    [Fact]
    public async Task RootSnapshot_IsIndependentFromD3AgentRun_AndPinsPublishedGraphAndBudgets()
    {
        var workflows = new Data.InMemory.InMemoryWorkflowRepository();
        var workflow = await workflows.CreateAsync("t", "root", "orchestrator", "{\"schemaVersion\":1}", "{}", "u", default);
        Assert.True(await workflows.MarkValidatedAsync("t", workflow.Workflow!.Id, 1, "{\"schemaVersion\":1}", "{}", default));
        await workflows.PublishAsync("t", workflow.Workflow.Id, 1, "{\"schemaVersion\":1}", "{}", WorkflowCompilerContracts.Current, "u", default);
        var workerWorkflow = await CreateWorkflow(workflows, "worker", "agent-runtime");
        var verifierWorkflow = await CreateWorkflow(workflows, "verifier", "agent-runtime");
        var workerId = Guid.NewGuid(); var verifierId = Guid.NewGuid();
        var definition = Definition(workflow.Workflow.Id, workerId, verifierId);
        var orchestrator = new StubOrchestrators(Guid.NewGuid(), definition);
        var agents = new StubAgents(workerId, verifierId, workerWorkflow, verifierWorkflow);
        var runs = new InMemoryOrchestratorRunRepository(orchestrator, workflows, agents);

        var created = await runs.CreateAsync("t", "u", "ADMIN", ["ops"], ["workflow.manage"], orchestrator.Id, "console-1", "plan it", "start-1", default);

        Assert.Equal(OrchestratorRunWriteStatus.Success, created.Status);
        Assert.NotNull(created.Run);
        Assert.Equal(1, created.Run!.OrchestratorRevision);
        Assert.Equal(1, created.Run.WorkflowRevision);
        var artifact = await runs.ExecutionArtifactAsync("t", "u", created.Run.Id, default);
        using var envelope = JsonDocument.Parse(artifact!);
        var snapshot = Encoding.UTF8.GetString(Convert.FromBase64String(envelope.RootElement.GetProperty("snapshot_canonical_base64").GetString()!));
        using var doc = JsonDocument.Parse(snapshot);
        Assert.False(doc.RootElement.TryGetProperty("agent", out _));
        Assert.Equal(created.Run.SnapshotHash, doc.RootElement.GetProperty("snapshot_hash").GetString());
        Assert.Equal(3, doc.RootElement.GetProperty("limits").GetProperty("max_child_runs").GetInt32());
        Assert.Equal(workflow.Workflow.Id.ToString("D"), doc.RootElement.GetProperty("workflow_id").GetString());
        Assert.Single(doc.RootElement.GetProperty("workers").EnumerateArray());
        Assert.Equal(AgentExecutionContract.DefaultTokenBudget,
            doc.RootElement.GetProperty("workers")[0].GetProperty("token_cap").GetInt32());
        Assert.Equal(AgentExecutionContract.DefaultTokenBudget,
            doc.RootElement.GetProperty("verifier").GetProperty("token_cap").GetInt32());
        Assert.Equal("root-runtime-adapter-1", doc.RootElement.GetProperty("graph").GetProperty("runtime_adapter_version").GetString());
        Assert.Equal("plan it", doc.RootElement.GetProperty("root_input").GetProperty("message").GetString());
        Assert.True(DateTimeOffset.TryParse(doc.RootElement.GetProperty("root_input").GetProperty("observed_at").GetString(), out _));
        var claim = await runs.ClaimCommandAsync("t", "u", created.Run.Id, created.Dispatch!.CommandId, "workflow-1", 30, default);
        Assert.NotNull(claim);
        Assert.Equal(created.Run.SnapshotHash, claim!.SnapshotHash);
        Assert.True(claim.LeaseGeneration > 0);
        var renewed = await runs.RenewCommandAsync("t", "u", created.Run.Id, claim.CommandId, claim.ClaimToken, claim.LeaseGeneration, 30, default);
        Assert.NotNull(renewed);
        Assert.Equal(claim.LeaseGeneration, renewed!.LeaseGeneration);
        Assert.Equal(claim.ClaimToken, renewed.ClaimToken);
        Assert.Null(await runs.RenewCommandAsync("t", "u", created.Run.Id, claim.CommandId, "stale", claim.LeaseGeneration, 30, default));
        Assert.Equal(OrchestratorRunDispatchCompleteStatus.Conflict,
            await runs.CompleteDispatchAsync("t", "u", created.Run.Id, claim.CommandId, "wrong", default));
        Assert.Equal(OrchestratorRunDispatchCompleteStatus.Success,
            await runs.CompleteDispatchAsync("t", "u", created.Run.Id, claim.CommandId, claim.ClaimToken, default));
        var reclaimable = await runs.CreateAsync("t", "u", "ADMIN", [], [], orchestrator.Id, "console-2", "reclaim", "start-3", default);
        var oldClaim = await runs.ClaimCommandAsync("t", "u", reclaimable.Run!.Id, reclaimable.Dispatch!.CommandId, "worker-old", 1, default);
        Assert.NotNull(oldClaim);
        await Task.Delay(1100);
        Assert.Null(await runs.RenewCommandAsync("t", "u", reclaimable.Run.Id, oldClaim!.CommandId, oldClaim.ClaimToken, oldClaim.LeaseGeneration, 30, default));
        var recovered = await runs.ClaimRecoveryAsync("worker-new", 10, 30, default);
        var recoveredClaim = Assert.Single(recovered.Items).Claim;
        Assert.Equal(oldClaim!.CommandId, recoveredClaim.CommandId);
        Assert.True(recoveredClaim.LeaseGeneration > oldClaim.LeaseGeneration);
        Assert.Equal(OrchestratorRunDispatchCompleteStatus.Conflict,
            await runs.CompleteDispatchAsync("t", "u", reclaimable.Run.Id, oldClaim.CommandId, oldClaim.ClaimToken, default));
        Assert.Null(await runs.CreateChildAsync("t", "u", created.Run.Id,
            new OrchestratorChildCreateRequest("forged-cap", 1, "worker", workerId, 1,
                TaskEnvelope: TaskEnvelope(), TokenCap: AgentExecutionContract.DefaultTokenBudget + 1), default));
        var child = await runs.CreateChildAsync("t", "u", created.Run.Id,
            new OrchestratorChildCreateRequest("research-a", 1, "worker", workerId, 1,
                TaskEnvelope: TaskEnvelope(), TokenCap: AgentExecutionContract.DefaultTokenBudget), default);
        Assert.NotNull(child);
        Assert.Equal(created.Run.Id, child!.OrchestratorRootRunId);
        Assert.Null(await runs.CreateChildAsync("t", "u", created.Run.Id,
            new OrchestratorChildCreateRequest("research-a", 1, "worker", workerId, 1,
                TaskEnvelope: TaskEnvelope(), TokenCap: AgentExecutionContract.DefaultTokenBudget), default));
        Assert.Null(await runs.CreateChildAsync("t", "u", created.Run.Id,
            new OrchestratorChildCreateRequest("write", 1, "worker", workerId, 1,
                WriteIntent: true, TaskEnvelope: TaskEnvelope(), TokenCap: AgentExecutionContract.DefaultTokenBudget), default));

        var duplicate = await runs.CreateAsync("t", "u", "ADMIN", [], [], orchestrator.Id, "console-1", "new work", "start-2", default);
        Assert.Equal(OrchestratorRunWriteStatus.Conflict, duplicate.Status);
        var cancelled = await runs.CancelAsync("t", "u", created.Run.Id, "stop", "cancel-1", default);
        Assert.Equal("cancelled", cancelled.Run!.Status);
        var events = await runs.EventsAsync("t", "u", created.Run.Id, 0, 10, default);
        Assert.Equal(["run_created", "child_created", "run_cancelled"], events!.Events.Select(x => x.EventType));
        var firstPage = await runs.EventsAsync("t", "u", created.Run.Id, 0, 1, default);
        var secondPage = await runs.EventsAsync("t", "u", created.Run.Id, firstPage!.NextSequence, 1, default);
        var thirdPage = await runs.EventsAsync("t", "u", created.Run.Id, secondPage!.NextSequence, 1, default);
        Assert.Equal(1, firstPage.NextSequence);
        Assert.Equal(2, secondPage.NextSequence);
        Assert.Equal(3, thirdPage!.NextSequence);
        Assert.Equal(["run_created", "child_created", "run_cancelled"], new[] { firstPage.Events[0].EventType, secondPage.Events[0].EventType, thirdPage.Events[0].EventType });
    }

    private static async Task<Guid> CreateWorkflow(Data.InMemory.InMemoryWorkflowRepository workflows, string name, string kind)
    { var item=await workflows.CreateAsync("t",name,kind,"{\"schemaVersion\":1}","{}","u",default); await workflows.MarkValidatedAsync("t",item.Workflow!.Id,1,"{\"schemaVersion\":1}","{}",default); await workflows.PublishAsync("t",item.Workflow.Id,1,"{\"schemaVersion\":1}","{}",WorkflowCompilerContracts.Current,"u",default); return item.Workflow.Id; }
    private static JsonElement TaskEnvelope() => JsonDocument.Parse("""{"objective":"research","required_capabilities":["research"],"context":{"query":"q"},"context_provenance":[{"context_key":"query","source_type":"caller","source_id":"user","observed_at":"2026-01-01T00:00:00Z","content_sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}],"write_intent":false,"delegation_depth":0,"repair_of":null}""").RootElement.Clone();
    private static string Definition(Guid workflow, Guid worker, Guid verifier) => new JsonObject
    {
        ["instructions"]="root",
        ["policy"]=new JsonObject{{"dispatchMode","bounded-parallel"},{"joinPolicy","repair"},{"repairPolicy","redispatch"},{"aggregationPolicy","verified-only"},{"denialPolicy","fail-closed"}},
        ["workflow"]=new JsonObject{{"id",workflow.ToString("D")},{"revision",1}},
        ["verifier"]=new JsonObject{{"agentId",verifier.ToString("D")},{"revision",1},{"variant","read-only"},{"independent",true},{"outputContract",new JsonObject{{"type","verification-report"}}}},
        ["workerPool"]=new JsonArray(new JsonObject{{"agentId",worker.ToString("D")},{"revision",1}}),
        ["workerPolicy"]=new JsonObject{{"requiredAudience",new JsonArray()},{"requiredCapabilities",new JsonArray()},{"selection","pinned-only"}},
        ["context"]=new JsonObject{{"readOnly",true},{"allowedTools",new JsonArray()},{"knowledgeSources",new JsonArray()}},
        ["audience"]=new JsonArray(), ["capabilities"]=new JsonArray(),
        ["budgets"]=new JsonObject{{"maxContextRounds",1},{"maxTasks",2},{"maxChildRuns",3},{"maxConcurrency",1},{"maxRepairRounds",1},{"tokenBudget",10},{"timeoutSeconds",10}},
    }.ToJsonString();

    private sealed class StubOrchestrators(Guid id, string definition) : IOrchestratorRepository
    {
        public Guid Id => id;
        public Task<Orchestrator?> GetAsync(string t, Guid requested, CancellationToken ct) => Task.FromResult(requested == id ? new Orchestrator(id,"root","",true,1,null,1,definition,DateTime.UtcNow,DateTime.UtcNow) : null);
        public Task<string?> RevisionAsync(string t, Guid requested, int revision, CancellationToken ct) => Task.FromResult<string?>(requested == id && revision == 1 ? definition : null);
        public Task<IReadOnlyList<OrchestratorInfo>> ListAsync(string t,CancellationToken ct)=>throw new NotSupportedException(); public Task<OrchestratorWriteResult> CreateAsync(string a,string b,string c,string d,string e,CancellationToken f)=>throw new NotSupportedException(); public Task<OrchestratorWriteResult> UpdateAsync(string a,Guid b,long c,string d,string e,string f,CancellationToken g)=>throw new NotSupportedException(); public Task<IReadOnlyList<string>> ValidateReferencesAsync(string a,string b,CancellationToken c)=>throw new NotSupportedException(); public Task<bool> MarkValidatedAsync(string a,Guid b,long c,string d,CancellationToken e)=>throw new NotSupportedException(); public Task<OrchestratorWriteResult> PublishAsync(string a,Guid b,long c,string d,string e,CancellationToken f)=>throw new NotSupportedException(); public Task<IReadOnlyList<OrchestratorRevisionInfo>> RevisionsAsync(string a,Guid b,CancellationToken c)=>throw new NotSupportedException(); public Task<OrchestratorWriteResult> RestoreAsync(string a,Guid b,int c,string d,string e,CancellationToken f)=>throw new NotSupportedException(); public Task<bool> SetEnabledAsync(string a,Guid b,bool c,CancellationToken d)=>throw new NotSupportedException();
    }
    private sealed class StubAgents(Guid worker,Guid verifier,Guid workerWorkflow,Guid verifierWorkflow) : Backend.Api.Agents.IAgentRepository
    {
        private static string Definition(Guid workflow,string role) => new JsonObject { ["system_prompt"]="",["execution_roles"]=new JsonArray(role),["capabilities"]=new JsonArray("research"),["output_contract"]=new JsonObject(),["audience"]=new JsonArray("role:ADMIN"),["allowed_tools"]=new JsonArray(),["skill_bindings"]=new JsonArray(),["knowledge_sources"]=new JsonArray(),["business_rules"]=new JsonObject{{"version",1},{"rules",new JsonArray()}},["runtime_limits"]=new JsonObject(),["runtime_workflow"]=new JsonObject{{"id",workflow.ToString("D")},{"revision",1}} }.ToJsonString();
        private (Guid Workflow,string Role) Source(Guid id)=>(id==worker?(workerWorkflow,"worker"):(verifierWorkflow,"verifier"));
        public Task<Backend.Api.Agents.Agent?> GetAsync(string t,Guid id,CancellationToken ct){if(id!=worker&&id!=verifier)return Task.FromResult<Backend.Api.Agents.Agent?>(null);var x=Source(id);var d=Definition(x.Workflow,x.Role);return Task.FromResult<Backend.Api.Agents.Agent?>(new(id,x.Role,x.Role,"",true,1,null,1,d,Skills.SkillHash.Sha256(d),DateTime.UtcNow,DateTime.UtcNow));}
        public Task<string?> GetRevisionDefinitionAsync(string t,Guid id,int r,CancellationToken ct){var x=Source(id);return Task.FromResult<string?>(r==1&& (id==worker||id==verifier)?Definition(x.Workflow,x.Role):null);}
        public async Task<IReadOnlyList<Backend.Api.Agents.AgentRevisionInfo>> ListRevisionsAsync(string t,Guid id,CancellationToken ct){var d=await GetRevisionDefinitionAsync(t,id,1,ct);if(d is null)return [];var x=Source(id);return [new(1,"published",Skills.SkillHash.Sha256(d),x.Workflow,1,[],"u",DateTime.UtcNow)];}
        public Task<IReadOnlyList<Backend.Api.Agents.AgentInfo>> ListAsync(string a,CancellationToken b)=>throw new NotSupportedException(); public Task<Backend.Api.Agents.Agent?> CreateAsync(string a,string b,string c,string d,string e,string f,string g,CancellationToken h)=>throw new NotSupportedException(); public Task<Backend.Api.Agents.AgentDraftResult> UpdateDraftAsync(string a,Guid b,long c,string d,string e,string f,string g,CancellationToken h)=>throw new NotSupportedException(); public Task<IReadOnlyList<Backend.Api.Agents.AgentValidationError>> ValidateReferencesAsync(string a,string b,CancellationToken c)=>throw new NotSupportedException(); public Task<bool> MarkValidatedAsync(string a,Guid b,long c,string d,string e,CancellationToken f)=>throw new NotSupportedException(); public Task<Backend.Api.Agents.AgentPublishResult> PublishAsync(string a,Guid b,long c,string d,string e,string f,CancellationToken g)=>throw new NotSupportedException(); public Task<Backend.Api.Agents.AgentPublishResult> RestoreAsync(string a,Guid b,int c,string d,string e,string f,CancellationToken g)=>throw new NotSupportedException(); public Task<bool> SetEnabledAsync(string a,Guid b,bool c,CancellationToken d)=>throw new NotSupportedException();
    }
}
