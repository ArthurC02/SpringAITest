using System.Text;
using System.Text.Json.Nodes;
using Backend.Api.Agents;
using Backend.Api.Data;
using Backend.Api.Orchestrators;
using Backend.Api.Workflows;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Backend.Api.Tests;

[Collection("Postgres")]
public sealed class OrchestratorRepositoryTests(PostgresFixture fixture)
{
    [SkippableFact]
    public async Task Publish_PinsAndPersistsExactCanonicalReferences()
    {
        fixture.SkipIfUnavailable(); await DbBootstrap.RunAsync(fixture.DataSource!, NullLogger.Instance); var tenant = "orchestratorrepo-" + Guid.NewGuid().ToString("N");
        await using var c = await fixture.DataSource!.OpenConnectionAsync(); var seed = await SeedPublishableAsync(c, tenant); var repo = new OrchestratorRepository(fixture.DataSource!);
        await c.ExecuteAsync("UPDATE agent_revision SET runtime_workflow_id=@wrong,runtime_workflow_revision=@wrongRevision WHERE agent_id=@verifier AND revision=1", new { wrong = Guid.Parse(AgentDefaults.RuntimeWorkflowId), wrongRevision = AgentDefaults.RuntimeWorkflowRevision, verifier = seed.VerifierId }); Assert.Contains(await repo.ValidateReferencesAsync(tenant, seed.Definition, default), x => x.Contains("does not match immutable revision projection")); await c.ExecuteAsync("UPDATE agent_revision SET runtime_workflow_id=@verifierWorkflow,runtime_workflow_revision=1 WHERE agent_id=@verifier AND revision=1", new { verifierWorkflow = seed.VerifierWorkflowId, verifier = seed.VerifierId });
        var created = await repo.CreateAsync(tenant, "Root", "", seed.Definition, "admin", default); Assert.True(await repo.MarkValidatedAsync(tenant, created.Orchestrator!.Id, 1, seed.Definition, default)); var published = await repo.PublishAsync(tenant, created.Orchestrator.Id, 1, seed.Definition, "admin", default); Assert.Equal(OrchestratorWriteStatus.Success, published.Status); var bytesStored = await c.ExecuteScalarAsync<byte[]>("SELECT canonical_definition FROM orchestrator_revision WHERE orchestrator_id=@id", new { id = created.Orchestrator.Id }); Assert.Equal(Encoding.UTF8.GetBytes(seed.Definition), bytesStored);
    }
    [SkippableFact] public async Task SystemOwnedWorkflow_RejectsMarkValidated() { fixture.SkipIfUnavailable(); await DbBootstrap.RunAsync(fixture.DataSource!, NullLogger.Instance); var repo = new WorkflowRepository(fixture.DataSource!); Assert.False(await repo.MarkValidatedAsync(Backend.Api.Agents.AgentDefaults.SystemTenant, Guid.Parse(Backend.Api.Agents.AgentDefaults.RuntimeWorkflowId), 1, Backend.Api.Agents.AgentDefaults.RuntimeWorkflowDefinition, "{}", default)); }
    [SkippableFact] public async Task Preflight_RejectsCorruptPublishedWorkflowHash() { fixture.SkipIfUnavailable(); await DbBootstrap.RunAsync(fixture.DataSource!, NullLogger.Instance); var tenant = "orchestrator-corrupt-" + Guid.NewGuid().ToString("N"); var wid = Guid.NewGuid(); await using var c = await fixture.DataSource!.OpenConnectionAsync(); await c.ExecuteAsync("INSERT INTO workflow(id,tenant_id,name,kind,enabled,draft_definition,draft_ui_metadata,draft_definition_canonical,draft_ui_metadata_canonical,draft_definition_sha256,draft_ui_metadata_sha256,published_revision) VALUES(@wid,@tenant,'Corrupt','orchestrator',true,'{}','{}',convert_to('{}','UTF8'),convert_to('{}','UTF8'),'bad','bad',1); INSERT INTO workflow_revision(workflow_id,revision,status,definition,ui_metadata,definition_canonical,ui_metadata_canonical,definition_sha256,ui_metadata_sha256) VALUES(@wid,1,'published','{}','{}',convert_to('{}','UTF8'),convert_to('{}','UTF8'),'bad','bad');", new { wid, tenant }); var verifier = Guid.NewGuid(); var worker = Guid.NewGuid(); var definition = $$$"""{"instructions":"root","policy":{"dispatchMode":"bounded-parallel","joinPolicy":"repair","repairPolicy":"redispatch","aggregationPolicy":"verified-only","denialPolicy":"fail-closed"},"workflow":{"id":"{{{wid:D}}}","revision":1},"verifier":{"agentId":"{{{verifier:D}}}","revision":1,"variant":"read-only","independent":true,"outputContract":{"type":"verification-report"}},"workerPool":[{"agentId":"{{{worker:D}}}","revision":1}],"workerPolicy":{"requiredAudience":[],"requiredCapabilities":[],"selection":"pinned-only"},"context":{"readOnly":true,"allowedTools":[],"knowledgeSources":[]},"audience":[],"capabilities":[],"budgets":{"maxContextRounds":1,"maxTasks":1,"maxChildRuns":2,"maxConcurrency":1,"maxRepairRounds":1,"tokenBudget":1,"timeoutSeconds":1}}"""; var errors = await new OrchestratorRepository(fixture.DataSource!).ValidateReferencesAsync(tenant, definition, default); Assert.Contains(errors, x => x.Contains("integrity")); }

    // 同租戶同名 → DB 級 ON CONFLICT(tenant_id,name) DO NOTHING → Duplicate;不同租戶可同名(租戶隔離)。
    [SkippableFact]
    public async Task Create_DuplicateTenantAndName_ReturnsDuplicate()
    {
        fixture.SkipIfUnavailable(); await DbBootstrap.RunAsync(fixture.DataSource!, NullLogger.Instance); var tenant = "orchestratorrepo-dup-" + Guid.NewGuid().ToString("N"); var repo = new OrchestratorRepository(fixture.DataSource!);
        var first = await repo.CreateAsync(tenant, "Root", "", "{}", "admin", default); Assert.Equal(OrchestratorWriteStatus.Success, first.Status);
        var second = await repo.CreateAsync(tenant, "Root", "another description", "{}", "admin", default); Assert.Equal(OrchestratorWriteStatus.Duplicate, second.Status); Assert.Null(second.Orchestrator);
        Assert.Equal(OrchestratorWriteStatus.Success, (await repo.CreateAsync(tenant + "-b", "Root", "", "{}", "admin", default)).Status);
    }

    // Publish 的三個 pre-write 分支:版本落後、草稿未 validate、orchestrator 不存在/他租戶 —— 都不得寫出 revision。
    [SkippableFact]
    public async Task Publish_StaleUnvalidatedOrMissingDraft_NeverWritesRevision()
    {
        fixture.SkipIfUnavailable(); await DbBootstrap.RunAsync(fixture.DataSource!, NullLogger.Instance); var tenant = "orchestratorrepo-stale-" + Guid.NewGuid().ToString("N"); var wid = Guid.NewGuid(); var verifier = Guid.NewGuid();
        var definition = $$$"""{"verifier":{"agentId":"{{{verifier:D}}}","revision":1},"workflow":{"id":"{{{wid:D}}}","revision":1}}"""; var repo = new OrchestratorRepository(fixture.DataSource!);
        var created = await repo.CreateAsync(tenant, "Root", "", definition, "admin", default); Assert.Equal(OrchestratorWriteStatus.Success, created.Status);
        Assert.Equal(OrchestratorWriteStatus.VersionConflict, (await repo.PublishAsync(tenant, created.Orchestrator!.Id, 99, definition, "admin", default)).Status);
        Assert.Equal(OrchestratorWriteStatus.VersionConflict, (await repo.PublishAsync(tenant, created.Orchestrator.Id, 1, definition, "admin", default)).Status);
        Assert.Equal(OrchestratorWriteStatus.NotFound, (await repo.PublishAsync(tenant, Guid.NewGuid(), 1, definition, "admin", default)).Status);
        Assert.Equal(OrchestratorWriteStatus.NotFound, (await repo.PublishAsync(tenant + "-b", created.Orchestrator.Id, 1, definition, "admin", default)).Status);
        Assert.Empty(await repo.RevisionsAsync(tenant, created.Orchestrator.Id, default));
    }

    // Update 的樂觀鎖決策表:成功(版本+1、清空 validated)、版本衝突、不存在/他租戶。
    [SkippableFact]
    public async Task Update_OptimisticLocking_ClearsValidationAndSeparatesConflictFromNotFound()
    {
        fixture.SkipIfUnavailable(); await DbBootstrap.RunAsync(fixture.DataSource!, NullLogger.Instance); var tenant = "orchestratorrepo-update-" + Guid.NewGuid().ToString("N"); var repo = new OrchestratorRepository(fixture.DataSource!);
        var created = await repo.CreateAsync(tenant, "Root", "", "{}", "admin", default); Assert.True(await repo.MarkValidatedAsync(tenant, created.Orchestrator!.Id, 1, "{}", default));
        var updated = await repo.UpdateAsync(tenant, created.Orchestrator.Id, 1, "Renamed", "desc", """{"a":1}""", default);
        Assert.Equal(OrchestratorWriteStatus.Success, updated.Status); Assert.Equal("Renamed", updated.Orchestrator!.Name); Assert.Equal(2, updated.Orchestrator.DraftVersion); Assert.Null(updated.Orchestrator.DraftValidatedVersion); Assert.Equal("""{"a":1}""", updated.Orchestrator.Definition);
        Assert.Equal(OrchestratorWriteStatus.VersionConflict, (await repo.UpdateAsync(tenant, created.Orchestrator.Id, 1, "Again", "", "{}", default)).Status);
        Assert.Equal(OrchestratorWriteStatus.NotFound, (await repo.UpdateAsync(tenant, Guid.NewGuid(), 1, "Ghost", "", "{}", default)).Status);
        Assert.Equal(OrchestratorWriteStatus.NotFound, (await repo.UpdateAsync(tenant + "-b", created.Orchestrator.Id, 2, "Ghost", "", "{}", default)).Status);
    }

    // Restore 未存在的 revision → NotFound;首次 publish 之後 next=(published)+1 的遞增邊界(1 → 2)與舊 row 轉 superseded。
    [SkippableFact]
    public async Task Restore_RequiresExistingRevision_ThenSupersedesAndIncrementsToRevisionTwo()
    {
        fixture.SkipIfUnavailable(); await DbBootstrap.RunAsync(fixture.DataSource!, NullLogger.Instance); var tenant = "orchestratorrepo-restore-" + Guid.NewGuid().ToString("N");
        await using var c = await fixture.DataSource!.OpenConnectionAsync(); var seed = await SeedPublishableAsync(c, tenant); var repo = new OrchestratorRepository(fixture.DataSource!);
        var created = await repo.CreateAsync(tenant, "Root", "", seed.Definition, "admin", default);
        Assert.Equal(OrchestratorWriteStatus.NotFound, (await repo.RestoreAsync(tenant, created.Orchestrator!.Id, 1, seed.Definition, "admin", default)).Status);
        Assert.True(await repo.MarkValidatedAsync(tenant, created.Orchestrator.Id, 1, seed.Definition, default));
        var first = await repo.PublishAsync(tenant, created.Orchestrator.Id, 1, seed.Definition, "admin", default); Assert.Equal(OrchestratorWriteStatus.Success, first.Status); Assert.Equal(1, first.Revision);
        var restored = await repo.RestoreAsync(tenant, created.Orchestrator.Id, 1, seed.Definition, "admin", default); Assert.Equal(OrchestratorWriteStatus.Success, restored.Status); Assert.Equal(2, restored.Revision);
        Assert.Equal(new[] { (2, "published"), (1, "superseded") }, (await repo.RevisionsAsync(tenant, created.Orchestrator.Id, default)).Select(x => (x.Revision, x.Status)).ToArray());
    }

    // 生命週期等價類:workflow 存在但 disabled、Agent 存在但沒有 published revision —— 引用一律拒收。
    [SkippableFact]
    public async Task ValidateReferences_RejectsDisabledWorkflowAndUnpublishedAgents()
    {
        fixture.SkipIfUnavailable(); await DbBootstrap.RunAsync(fixture.DataSource!, NullLogger.Instance); var tenant = "orchestratorrepo-lifecycle-" + Guid.NewGuid().ToString("N");
        await using var c = await fixture.DataSource!.OpenConnectionAsync(); var seed = await SeedPublishableAsync(c, tenant);
        await c.ExecuteAsync("UPDATE workflow SET enabled=false WHERE id=@id", new { id = seed.WorkflowId }); await c.ExecuteAsync("UPDATE agent SET published_revision=NULL WHERE tenant_id=@tenant", new { tenant });
        var errors = await new OrchestratorRepository(fixture.DataSource!).ValidateReferencesAsync(tenant, seed.Definition, default);
        Assert.Contains(errors, x => x == "workflow must pin active same-tenant published orchestrator kind");
        Assert.Contains(errors, x => x == $"agent {seed.VerifierId:D} must pin active same-tenant published revision");
        Assert.Contains(errors, x => x == $"agent {seed.WorkerId:D} must pin active same-tenant published revision");
    }

    // 租戶隔離:同一份「本租戶完全合法」的定義,換成別的租戶驗證時 workflow/verifier/worker 三種引用全部看不見。
    [SkippableFact]
    public async Task ValidateReferences_RejectsCrossTenantPublishedReferences()
    {
        fixture.SkipIfUnavailable(); await DbBootstrap.RunAsync(fixture.DataSource!, NullLogger.Instance); var tenant = "orchestratorrepo-crosstenant-" + Guid.NewGuid().ToString("N");
        await using var c = await fixture.DataSource!.OpenConnectionAsync(); var seed = await SeedPublishableAsync(c, tenant); var repo = new OrchestratorRepository(fixture.DataSource!);
        Assert.Empty(await repo.ValidateReferencesAsync(tenant, seed.Definition, default));
        var errors = await repo.ValidateReferencesAsync(tenant + "-b", seed.Definition, default);
        Assert.Contains(errors, x => x == "workflow must pin active same-tenant published orchestrator kind");
        Assert.Contains(errors, x => x == $"agent {seed.VerifierId:D} must pin active same-tenant published revision");
        Assert.Contains(errors, x => x == $"agent {seed.WorkerId:D} must pin active same-tenant published revision");
    }
    [Fact] public void VerifierWorkflowGuard_RejectsLegacyWorkerAndSemanticInvalid() { Assert.Contains(OrchestratorReferencePolicy.ValidateVerifierWorkflowDefinition(VerifierGraph(), "1"), x => x.Contains("compiler contract")); Assert.Contains(OrchestratorReferencePolicy.ValidateVerifierWorkflowDefinition(AgentDefaults.RuntimeWorkflowDefinition, OrchestratorReferencePolicy.SupportedCompilerContract), x => x.Contains("runtimeVariant") || x.Contains("forbidden")); var broken = VerifierGraph().Replace("\"maxSteps\":40", "\"maxSteps\":0"); Assert.Contains(OrchestratorReferencePolicy.ValidateVerifierWorkflowDefinition(broken, OrchestratorReferencePolicy.SupportedCompilerContract), x => x.Contains("governance")); }
    // governance 的另一半條件:maxConcurrency 的 on-point(1 通過)與 off-point(0 拒收)。
    [Fact] public void VerifierWorkflowGuard_RejectsMaxConcurrencyBelowOne() { Assert.Empty(OrchestratorReferencePolicy.ValidateVerifierWorkflowDefinition(VerifierGraph(), OrchestratorReferencePolicy.SupportedCompilerContract)); var broken = VerifierGraph().Replace("\"maxConcurrency\":1", "\"maxConcurrency\":0"); Assert.Contains(OrchestratorReferencePolicy.ValidateVerifierWorkflowDefinition(broken, OrchestratorReferencePolicy.SupportedCompilerContract), x => x.Contains("governance")); }
    // 圖形狀守衛:edge 指向不存在的 node、以及多於一個 start。
    [Fact] public void VerifierWorkflowGuard_RejectsUnknownEdgeTargetAndDuplicateStart() { var danglingEdge = VerifierGraph().Replace("\"nodeId\":\"end\"", "\"nodeId\":\"nowhere\""); Assert.Contains(OrchestratorReferencePolicy.ValidateVerifierWorkflowDefinition(danglingEdge, OrchestratorReferencePolicy.SupportedCompilerContract), x => x.Contains("edge references an unknown node")); var twoStarts = VerifierGraph().Replace("""{"config":{},"id":"start","type":"start","typeVersion":"1.0"}""", """{"config":{},"id":"start","type":"start","typeVersion":"1.0"},{"config":{},"id":"start2","type":"start","typeVersion":"1.0"}"""); Assert.Contains(OrchestratorReferencePolicy.ValidateVerifierWorkflowDefinition(twoStarts, OrchestratorReferencePolicy.SupportedCompilerContract), x => x.Contains("exactly one start and end")); }
    [Fact] public void AgentRuntimeWorkflowProjection_MustMatchCanonicalBytes() { var id = Guid.NewGuid(); var definition = $$$"""{"runtime_workflow":{"id":"{{{id:D}}}","revision":2}}"""; Assert.Empty(OrchestratorReferencePolicy.ValidateRuntimeWorkflowProjection(definition, id, 2)); Assert.NotEmpty(OrchestratorReferencePolicy.ValidateRuntimeWorkflowProjection(definition, Guid.NewGuid(), 2)); Assert.NotEmpty(OrchestratorReferencePolicy.ValidateRuntimeWorkflowProjection(definition, id, 3)); }
    private static string VerifierGraph() { var root = JsonNode.Parse(AgentDefaults.RuntimeWorkflowDefinition)!.AsObject(); root["runtimeVariant"] = "verifier"; var loop = root["nodes"]!.AsArray().Single(x => x!["type"]!.GetValue<string>() == "bounded_agent_loop")!; var children = loop["children"]!.AsArray(); foreach (var child in children.Where(x => x is not null && new[] { "load_skill", "tool_policy_and_approval_gate", "tool_call_and_observation" }.Contains(x["type"]!.GetValue<string>())).ToArray()) children.Remove(child); return Backend.Api.Agents.AgentCanonicalizer.CanonicalizeDefinition(root.ToJsonString()); }
    /// <summary>種一份「完全合法可 publish」的 orchestrator 引用組合:同租戶 enabled+published 的 orchestrator workflow、
    /// verifier 的 agent-runtime workflow、verifier/worker 兩個 published Agent revision,回傳 canonical 定義與各 id。</summary>
    private static async Task<(string Definition, Guid WorkflowId, Guid VerifierWorkflowId, Guid VerifierId, Guid WorkerId)> SeedPublishableAsync(NpgsqlConnection c, string tenant)
    {
        var wid = Guid.NewGuid(); var verifierWorkflow = Guid.NewGuid(); var verifier = Guid.NewGuid(); var worker = Guid.NewGuid(); var agentDef = """{"allowed_tools":[],"audience":[],"business_rules":{},"capabilities":[],"execution_roles":["ROLE"],"knowledge_sources":[],"output_contract":{},"runtime_limits":{},"runtime_workflow":{"id":"WORKFLOW","revision":REVISION},"skill_bindings":[],"system_prompt":"test"}""";
        var emptyHash = Backend.Api.Skills.SkillHash.Sha256("{}"); await c.ExecuteAsync("INSERT INTO workflow(id,tenant_id,name,kind,enabled,draft_definition,draft_ui_metadata,draft_definition_canonical,draft_ui_metadata_canonical,draft_definition_sha256,draft_ui_metadata_sha256,published_revision) VALUES(@wid,@tenant,'Root','orchestrator',true,'{}','{}',convert_to('{}','UTF8'),convert_to('{}','UTF8'),@emptyHash,@emptyHash,1); INSERT INTO workflow_revision(workflow_id,revision,status,definition,ui_metadata,definition_canonical,ui_metadata_canonical,definition_sha256,ui_metadata_sha256) VALUES(@wid,1,'published','{}','{}',convert_to('{}','UTF8'),convert_to('{}','UTF8'),@emptyHash,@emptyHash);", new { wid, tenant, emptyHash });
        var verifierGraph = VerifierGraph(); var verifierGraphHash = Backend.Api.Skills.SkillHash.Sha256(verifierGraph); await c.ExecuteAsync("INSERT INTO workflow(id,tenant_id,name,kind,enabled,draft_definition,draft_ui_metadata,draft_definition_canonical,draft_ui_metadata_canonical,draft_definition_sha256,draft_ui_metadata_sha256,published_revision) VALUES(@verifierWorkflow,@tenant,'Verifier Runtime','agent-runtime',true,@verifierGraph::jsonb,'{}',convert_to(@verifierGraph,'UTF8'),convert_to('{}','UTF8'),@verifierGraphHash,@emptyHash,1); INSERT INTO workflow_revision(workflow_id,revision,status,definition,ui_metadata,definition_canonical,ui_metadata_canonical,definition_sha256,ui_metadata_sha256,compiler_contract_version) VALUES(@verifierWorkflow,1,'published',@verifierGraph::jsonb,'{}',convert_to(@verifierGraph,'UTF8'),convert_to('{}','UTF8'),@verifierGraphHash,@emptyHash,@contract);", new { verifierWorkflow, tenant, verifierGraph, verifierGraphHash, emptyHash, contract = OrchestratorReferencePolicy.SupportedCompilerContract });
        foreach (var pair in new[] { (verifier, "verifier"), (worker, "worker") }) { var runtimeId = pair.Item2 == "verifier" ? verifierWorkflow : Guid.Parse(Backend.Api.Agents.AgentDefaults.RuntimeWorkflowId); var runtimeRevision = pair.Item2 == "verifier" ? 1 : Backend.Api.Agents.AgentDefaults.RuntimeWorkflowRevision; var text = agentDef.Replace("ROLE", pair.Item2).Replace("WORKFLOW", runtimeId.ToString("D")).Replace("REVISION", runtimeRevision.ToString()); if (pair.Item2 == "verifier") text = text.Replace("\"output_contract\":{}", "\"output_contract\":{\"type\":\"verification-report\"}"); var bytes = Encoding.UTF8.GetBytes(text); var sha = Backend.Api.Skills.SkillHash.Sha256(text); await c.ExecuteAsync("INSERT INTO agent(id,tenant_id,slug,name,draft_definition,draft_definition_canonical,draft_definition_sha256,published_revision) VALUES(@id,@tenant,@slug,@slug,'{}',convert_to('{}','UTF8'),'x',1); INSERT INTO agent_revision(agent_id,revision,status,canonical_definition,definition_sha256,runtime_workflow_id,runtime_workflow_revision) VALUES(@id,1,'published',@bytes,@sha,@runtimeId,@runtimeRevision);", new { id = pair.Item1, tenant, slug = pair.Item2, bytes, sha, runtimeId, runtimeRevision }); }
        var definition = $$$"""{"instructions":"coordinate","policy":{"dispatchMode":"bounded-parallel","joinPolicy":"repair","repairPolicy":"redispatch","aggregationPolicy":"verified-only","denialPolicy":"fail-closed"},"audience":[],"budgets":{"maxChildRuns":3,"maxConcurrency":1,"maxContextRounds":1,"maxRepairRounds":1,"maxTasks":2,"timeoutSeconds":60,"tokenBudget":1000},"capabilities":[],"context":{"allowedTools":[],"knowledgeSources":[],"readOnly":true},"verifier":{"agentId":"{{{verifier:D}}}","independent":true,"outputContract":{"type":"verification-report"},"revision":1,"variant":"read-only"},"workerPool":[{"agentId":"{{{worker:D}}}","revision":1}],"workerPolicy":{"requiredAudience":[],"requiredCapabilities":[],"selection":"pinned-only"},"workflow":{"id":"{{{wid:D}}}","revision":1}}""";
        return (OrchestratorCanonicalizer.Canonicalize(definition), wid, verifierWorkflow, verifier, worker);
    }
}
