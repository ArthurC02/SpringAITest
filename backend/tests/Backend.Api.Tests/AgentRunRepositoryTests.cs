using Backend.Api.AgentRuns;
using Backend.Api.Agents;
using Backend.Api.Data.InMemory;
using Backend.Api.Skills;
using Dapper;
using Npgsql;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Backend.Api.Tests;

/// <summary>
/// Real-PostgreSQL coverage for D3 invariants that the in-memory API tests cannot prove:
/// Dapper record mapping, jsonb canonical snapshot recovery, lease/CAS and command uniqueness.
/// </summary>
[Collection("Postgres")]
public sealed class AgentRunRepositoryTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    public AgentRunRepositoryTests(PostgresFixture fixture) => _fixture = fixture;

    private AgentRunRepository Runs => new(_fixture.DataSource!);
    private AgentRepository Agents => new(_fixture.DataSource!);

    private static JsonDocument DecodeSnapshotArtifact(string artifact)
    {
        using var envelope = JsonDocument.Parse(artifact);
        var root = envelope.RootElement;
        Assert.Equal(2, root.EnumerateObject().Count());
        var hash = root.GetProperty("snapshot_hash").GetString();
        var encoded = root.GetProperty("snapshot_canonical_base64").GetString()!;
        Assert.True(encoded.Length <= AgentExecutionContract.MaxSnapshotCanonicalBase64Length);
        var bytes = Convert.FromBase64String(encoded);
        Assert.True(bytes.Length <= AgentExecutionContract.MaxSnapshotCanonicalBytes);
        Assert.Equal(hash, SkillHash.Sha256(bytes));
        return JsonDocument.Parse(bytes);
    }

    public Task InitializeAsync() => CleanupAsync();

    public Task DisposeAsync() => CleanupAsync();

    private async Task CleanupAsync()
    {
        if (!_fixture.Available)
        {
            return;
        }

        await using var connection = await _fixture.DataSource!.OpenConnectionAsync();
        await connection.ExecuteAsync(
            "DELETE FROM agent_run_command WHERE tenant_id LIKE 'agentrunrepo-%';"
            + " DELETE FROM agent_run_event WHERE run_id IN"
            + " (SELECT id FROM agent_run WHERE tenant_id LIKE 'agentrunrepo-%');"
            + " DELETE FROM agent_run_skill WHERE run_id IN"
            + " (SELECT id FROM agent_run WHERE tenant_id LIKE 'agentrunrepo-%');"
            + " DELETE FROM agent_run WHERE tenant_id LIKE 'agentrunrepo-%';"
            + " DELETE FROM agent_revision_skill WHERE agent_id IN"
            + " (SELECT id FROM agent WHERE tenant_id LIKE 'agentrunrepo-%');"
            + " DELETE FROM agent_revision WHERE agent_id IN"
            + " (SELECT id FROM agent WHERE tenant_id LIKE 'agentrunrepo-%');"
            + " DELETE FROM agent WHERE tenant_id LIKE 'agentrunrepo-%';"
            + " DELETE FROM skill_revision WHERE skill_id IN"
            + " (SELECT id FROM skill WHERE tenant_id LIKE 'agentrunrepo-%');"
            + " DELETE FROM skill WHERE tenant_id LIKE 'agentrunrepo-%';");
    }

    [SkippableFact]
    public async Task Create_GetArtifact_LeaseAndTransition_RoundTripThroughDapper()
    {
        _fixture.SkipIfUnavailable();
        const string tenant = "agentrunrepo-roundtrip";
        var agent = await PublishedAgentAsync(tenant, "roundtrip");

        var created = await Runs.CreateDirectAsync(
            tenant, "admin-a", "ADMIN", agent.Id, "請整理重點", "start-1", default);

        Assert.Equal(AgentRunWriteStatus.Success, created.Status);
        Assert.Equal("queued", created.Run!.Status);
        var loaded = await Runs.GetAsync(tenant, "admin-a", created.Run.Id, default);
        Assert.Equal(created.Run.SnapshotHash, loaded!.SnapshotHash);
        Assert.Empty(loaded.Skills);

        var artifact = await Runs.GetExecutionArtifactAsync(
            tenant, "admin-a", created.Run.Id, default);
        Assert.NotNull(artifact);
        Assert.Contains("\"snapshot_hash\":\"" + created.Run.SnapshotHash + "\"", artifact);
        using var snapshot = DecodeSnapshotArtifact(artifact);
        Assert.Equal(created.Run.Id, snapshot.RootElement.GetProperty("run_id").GetGuid());

        var lease = await Runs.ClaimLeaseAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            new AgentRunLeaseRequest(created.Run.StateVersion, "worker-a", 300),
            default);
        Assert.Equal(AgentRunWriteStatus.Success, lease.Status);
        var running = await Runs.TransitionAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            new AgentRunTransitionRequest(
                lease.Lease!.Run.StateVersion,
                AgentRunStatuses.Running,
                lease.Lease.LeaseToken,
                lease.Lease.LeaseGeneration,
                lease.Lease.EventAckCursor),
            default);
        Assert.Equal(AgentRunWriteStatus.Success, running.Status);
        Assert.Equal("running", running.Run!.Status);

        var auditEvent = new AgentRunEventAppend(
            Guid.NewGuid(),
            "model_step",
            "model",
            running.Run.SnapshotHash,
            JsonSerializer.SerializeToElement(new { status = "ok" }));
        var staleLease = await Runs.AppendEventsAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            new AgentRunEventsAppendRequest(
                running.Run.StateVersion,
                "wrong-token",
                lease.Lease.LeaseGeneration,
                0,
                new[] { auditEvent }),
            default);
        Assert.Equal(AgentRunWriteStatus.Conflict, staleLease.Status);
        var appended = await Runs.AppendEventsAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            new AgentRunEventsAppendRequest(
                running.Run.StateVersion,
                lease.Lease.LeaseToken,
                lease.Lease.LeaseGeneration,
                0,
                new[] { auditEvent }),
            default);
        Assert.Equal(AgentRunWriteStatus.Success, appended.Status);
        var divergentReplay = await Runs.AppendEventsAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            new AgentRunEventsAppendRequest(
                running.Run.StateVersion,
                lease.Lease.LeaseToken,
                lease.Lease.LeaseGeneration,
                0,
                new[]
                {
                    auditEvent with
                    {
                        Payload = JsonSerializer.SerializeToElement(
                            new { status = "different" }),
                    },
                }),
            default);
        Assert.Equal(AgentRunWriteStatus.Conflict, divergentReplay.Status);

        var cancel = await Runs.CancelAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            "stop",
            "cancel-1",
            default);
        Assert.Equal(AgentRunWriteStatus.Success, cancel.Status);
        Assert.True(cancel.Run!.CancelRequested);
        Assert.NotNull(cancel.Dispatch);
        await using (var connection = await _fixture.DataSource!.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE agent_run_command"
                + " SET dispatch_claim_expires_at=now()-interval '1 second'"
                + " WHERE id=@commandId;"
                + " UPDATE agent_run"
                + " SET lease_expires_at=now()-interval '1 second'"
                + " WHERE id=@runId",
                new
                {
                    commandId = cancel.Dispatch.CommandId,
                    runId = created.Run.Id,
                });
        }
        var recoveredCancel = await Runs.ClaimRecoveryAsync(
            new AgentRunRecoveryClaimRequest("cancel-delivery", 100, 30),
            default);
        var recoveredCommand = Assert.Single(
            recoveredCancel.Items,
            item => item.RunId == created.Run.Id);
        Assert.Equal("cancel", recoveredCommand.CommandType);
        Assert.Equal(AgentRunStatuses.Running, recoveredCommand.RunStatus);
        Assert.Equal(
            AgentRunDispatchCompleteStatus.Success,
            await Runs.CompleteDispatchAsync(
                tenant,
                "admin-a",
                created.Run.Id,
                recoveredCommand.CommandId,
                recoveredCommand.ClaimToken,
                default));

        var noLeaseAudit = await Runs.AppendEventsAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            new AgentRunEventsAppendRequest(
                recoveredCommand.StateVersion,
                null,
                recoveredCommand.LeaseGeneration,
                recoveredCommand.EventAckCursor,
                new[]
                {
                    auditEvent with
                    {
                        EventId = Guid.NewGuid(),
                        EventType = "run_preflight",
                    },
                }),
            default);
        Assert.Equal(AgentRunWriteStatus.Conflict, noLeaseAudit.Status);
        var staleLeaseAudit = await Runs.AppendEventsAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            new AgentRunEventsAppendRequest(
                recoveredCommand.StateVersion,
                "stale-token",
                recoveredCommand.LeaseGeneration,
                recoveredCommand.EventAckCursor,
                new[]
                {
                    auditEvent with
                    {
                        EventId = Guid.NewGuid(),
                        EventType = "model_step",
                    },
                }),
            default);
        Assert.Equal(AgentRunWriteStatus.Conflict, staleLeaseAudit.Status);
        var unsafeEvent = await Runs.AppendEventsAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            new AgentRunEventsAppendRequest(
                recoveredCommand.StateVersion,
                recoveredCommand.LeaseToken,
                recoveredCommand.LeaseGeneration,
                recoveredCommand.EventAckCursor,
                new[]
                {
                    auditEvent with
                    {
                        EventId = Guid.NewGuid(),
                        EventType = "business_action_completed",
                    },
                }),
            default);
        Assert.Equal(AgentRunWriteStatus.InvalidState, unsafeEvent.Status);
        var cancelAudit = await Runs.AppendEventsAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            new AgentRunEventsAppendRequest(
                recoveredCommand.StateVersion,
                recoveredCommand.LeaseToken,
                recoveredCommand.LeaseGeneration,
                recoveredCommand.EventAckCursor,
                new[]
                {
                    auditEvent with
                    {
                        EventId = Guid.NewGuid(),
                        EventType = "run_preflight",
                    },
                    auditEvent with
                    {
                        EventId = Guid.NewGuid(),
                        EventType = "tool_completed",
                    },
                    auditEvent with
                    {
                        EventId = Guid.NewGuid(),
                        EventType = "run_cancelled",
                    },
                }),
            default);
        Assert.Equal(AgentRunWriteStatus.Success, cancelAudit.Status);

        var tooLate = await Runs.TransitionAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            new AgentRunTransitionRequest(
                recoveredCommand.StateVersion,
                AgentRunStatuses.Completed,
                recoveredCommand.LeaseToken,
                LeaseGeneration: recoveredCommand.LeaseGeneration,
                ExpectedEventAckCursor: cancelAudit.Run!.EventAckCursor),
            default);
        Assert.Equal(AgentRunWriteStatus.InvalidState, tooLate.Status);

        var terminal = await Runs.TransitionAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            new AgentRunTransitionRequest(
                recoveredCommand.StateVersion,
                AgentRunStatuses.Cancelled,
                recoveredCommand.LeaseToken,
                LeaseGeneration: recoveredCommand.LeaseGeneration,
                ExpectedEventAckCursor: cancelAudit.Run!.EventAckCursor,
                CheckpointRef: V2CheckpointRef(recoveredCommand.LeaseGeneration),
                CheckpointVersion: 1),
            default);
        Assert.Equal(AgentRunWriteStatus.Success, terminal.Status);
        Assert.Equal("cancelled", terminal.Run!.Status);
    }

    [SkippableFact]
    public async Task BootstrapPinnedIdentityConstraint_RejectsMissingCallerProjection()
    {
        _fixture.SkipIfUnavailable();
        const string tenant = "agentrunrepo-identity-constraint";
        var agent = await PublishedAgentAsync(tenant, "identity-constraint");
        var created = await Runs.CreateDirectAsync(
            tenant,
            "admin-a",
            "ADMIN",
            agent.Id,
            "identity",
            "identity",
            default);
        await using var connection =
            await _fixture.DataSource!.OpenConnectionAsync();
        var definition = await connection.QuerySingleAsync<string>(
            "SELECT pg_get_constraintdef(oid) FROM pg_constraint"
            + " WHERE conrelid='agent_run'::regclass"
            + " AND conname='ck_agent_run_pinned_identity_v2'");
        Assert.Contains("IS DISTINCT FROM", definition);

        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => connection.ExecuteAsync(
                "UPDATE agent_run SET execution_snapshot='{}'::jsonb"
                + " WHERE id=@runId",
                new { runId = created.Run!.Id }));
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
    }

    [SkippableFact]
    public async Task CanonicalDefinitionAndSnapshot_UseExactByteaAcrossPublishStartAndArtifact()
    {
        _fixture.SkipIfUnavailable();
        const string tenant = "agentrunrepo-canonical-bytes";
        var outputContract = JsonDocument.Parse(
            """
            {
              "type":"object",
              "properties":{
                "value":{"type":"number","enum":[1e+3,-0,1.230,0.00000100]},
                "label":{"type":"string","enum":["雪","\\u96ea"]}
              },
              "required":["value"],
              "additionalProperties":false
            }
            """).RootElement.Clone();
        var definition = AgentCanonicalizer.Canonicalize(new AgentUpsert(
            Slug: null,
            Name: null,
            Description: null,
            SystemPrompt: "Unicode 雪 and canonical numeric lexemes",
            ExecutionRoles: new[] { "worker" },
            Capabilities: null,
            OutputContract: outputContract,
            Audience: new[] { "ADMIN" },
            AllowedTools: Array.Empty<string>(),
            SkillBindings: null,
            KnowledgeSources: null,
            BusinessRules: null,
            RuntimeLimits: new AgentRuntimeLimits(StepBudget: 8),
            RuntimeWorkflow: new AgentWorkflowRef(
                AgentDefaults.RuntimeWorkflowId,
                AgentDefaults.RuntimeWorkflowRevision)));
        var lifecycleDefinition =
            AgentCanonicalizer.CanonicalizeForLifecycleWrite(definition);
        var definitionBytes = Encoding.UTF8.GetBytes(lifecycleDefinition);
        var definitionHash = SkillHash.Sha256(definitionBytes);

        var agent = await Agents.CreateAsync(
            tenant,
            "canonical-bytes",
            "Canonical bytes",
            "D3 exact-byte test",
            definition,
            SkillHash.Sha256(definition),
            "admin-a",
            default);
        Assert.NotNull(agent);
        Assert.Equal(definition, agent!.DraftDefinition);
        Assert.Equal(SkillHash.Sha256(Encoding.UTF8.GetBytes(agent.DraftDefinition)),
            agent.DraftDefinitionSha256);

        var memorySkills = new InMemorySkillRepository();
        var memoryAgents = new InMemoryAgentRepository(memorySkills);
        var memoryAgent = await memoryAgents.CreateAsync(
            tenant,
            "canonical-bytes",
            "Canonical bytes",
            "D3 exact-byte test",
            definition,
            SkillHash.Sha256(definition),
            "admin-a",
            default);
        Assert.NotNull(memoryAgent);
        Assert.Equal(agent.DraftDefinition, memoryAgent!.DraftDefinition);
        Assert.Equal(agent.DraftDefinitionSha256, memoryAgent.DraftDefinitionSha256);

        var updated = await Agents.UpdateDraftAsync(
            tenant,
            agent.Id,
            agent.DraftVersion,
            agent.Name,
            agent.Description,
            definition,
            SkillHash.Sha256(definition),
            default);
        var memoryUpdated = await memoryAgents.UpdateDraftAsync(
            tenant,
            memoryAgent.Id,
            memoryAgent.DraftVersion,
            memoryAgent.Name,
            memoryAgent.Description,
            definition,
            SkillHash.Sha256(definition),
            default);
        Assert.Equal(AgentWriteStatus.Success, updated.Status);
        Assert.Equal(AgentWriteStatus.Success, memoryUpdated.Status);
        Assert.Equal(updated.Agent!.DraftDefinition, memoryUpdated.Agent!.DraftDefinition);
        Assert.Equal(
            updated.Agent.DraftDefinitionSha256,
            memoryUpdated.Agent.DraftDefinitionSha256);

        Assert.True(await Agents.MarkValidatedAsync(
            tenant,
            agent.Id,
            updated.Agent.DraftVersion,
            definition,
            SkillHash.Sha256(definition),
            default));
        Assert.True(await memoryAgents.MarkValidatedAsync(
            tenant,
            memoryAgent.Id,
            memoryUpdated.Agent.DraftVersion,
            definition,
            SkillHash.Sha256(definition),
            default));
        var publish = await Agents.PublishAsync(
            tenant,
            agent.Id,
            updated.Agent.DraftVersion,
            definition,
            SkillHash.Sha256(definition),
            "admin-a",
            default);
        var memoryPublish = await memoryAgents.PublishAsync(
            tenant,
            memoryAgent.Id,
            memoryUpdated.Agent.DraftVersion,
            definition,
            SkillHash.Sha256(definition),
            "admin-a",
            default);
        Assert.Equal(AgentWriteStatus.Success, publish.Status);
        Assert.Equal(AgentWriteStatus.Success, memoryPublish.Status);
        Assert.Equal(
            lifecycleDefinition,
            await memoryAgents.GetRevisionDefinitionAsync(
                tenant,
                memoryAgent.Id,
                1,
                default));

        await using var connection = await _fixture.DataSource!.OpenConnectionAsync();
        var revision = await connection.QuerySingleAsync<CanonicalBytesRow>(
            "SELECT canonical_definition AS Bytes,definition_sha256 AS Hash"
            + " FROM agent_revision WHERE agent_id=@agentId AND revision=1",
            new { agentId = agent.Id });
        Assert.Equal(definitionBytes, revision.Bytes);
        Assert.Equal(definitionHash, revision.Hash);
        Assert.Contains("1e+3", lifecycleDefinition, StringComparison.Ordinal);
        Assert.Contains("-0", lifecycleDefinition, StringComparison.Ordinal);
        Assert.Contains("1.230", lifecycleDefinition, StringComparison.Ordinal);
        Assert.Contains("0.00000100", lifecycleDefinition, StringComparison.Ordinal);

        var created = await Runs.CreateDirectAsync(
            tenant,
            "admin-a",
            "ADMIN",
            agent.Id,
            "exact bytes",
            "canonical-bytes-start",
            default);
        Assert.Equal(AgentRunWriteStatus.Success, created.Status);
        var run = await connection.QuerySingleAsync<CanonicalBytesRow>(
            "SELECT execution_snapshot_canonical AS Bytes,snapshot_sha256 AS Hash"
            + " FROM agent_run WHERE id=@runId",
            new { runId = created.Run!.Id });
        Assert.Equal(SkillHash.Sha256(run.Bytes), run.Hash);
        var snapshotText = new UTF8Encoding(false, true).GetString(run.Bytes);
        Assert.Contains("1e+3", snapshotText, StringComparison.Ordinal);
        Assert.Contains("-0", snapshotText, StringComparison.Ordinal);
        Assert.Contains("1.230", snapshotText, StringComparison.Ordinal);
        Assert.Contains("0.00000100", snapshotText, StringComparison.Ordinal);

        var artifact = await Runs.GetExecutionArtifactAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            default);
        using var envelope = JsonDocument.Parse(artifact!);
        Assert.Equal(
            run.Bytes,
            Convert.FromBase64String(
                envelope.RootElement
                    .GetProperty("snapshot_canonical_base64")
                    .GetString()!));
        Assert.Equal(
            run.Hash,
            envelope.RootElement.GetProperty("snapshot_hash").GetString());

        async Task AssertRunAuthorityRejected(byte[] bytes, string hash)
        {
            await connection.ExecuteAsync(
                "UPDATE agent_run SET execution_snapshot_canonical=@bytes,"
                + " snapshot_sha256=@hash WHERE id=@runId",
                new { bytes, hash, runId = created.Run.Id });
            try
            {
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => Runs.GetExecutionArtifactAsync(
                        tenant,
                        "admin-a",
                        created.Run.Id,
                        default));
            }
            finally
            {
                await connection.ExecuteAsync(
                    "UPDATE agent_run SET execution_snapshot_canonical=@bytes,"
                    + " snapshot_sha256=@hash WHERE id=@runId",
                    new { bytes = run.Bytes, hash = run.Hash, runId = created.Run.Id });
            }
        }

        var revisionCorruptionSequence = 0;
        async Task AssertRevisionAuthorityRejected(byte[] bytes, string hash)
        {
            await connection.ExecuteAsync(
                "UPDATE agent_revision SET canonical_definition=@bytes,"
                + " definition_sha256=@hash WHERE agent_id=@agentId AND revision=1",
                new { bytes, hash, agentId = agent.Id });
            try
            {
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => Agents.GetRevisionDefinitionAsync(
                        tenant,
                        agent.Id,
                        1,
                        default));
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => Runs.CreateDirectAsync(
                        tenant,
                        "admin-a",
                        "ADMIN",
                        agent.Id,
                        "corrupt revision must not start",
                        $"canonical-corrupt-{++revisionCorruptionSequence}",
                        default));
            }
            finally
            {
                await connection.ExecuteAsync(
                    "UPDATE agent_revision SET canonical_definition=@bytes,"
                    + " definition_sha256=@hash WHERE agent_id=@agentId AND revision=1",
                    new
                    {
                        bytes = revision.Bytes,
                        hash = revision.Hash,
                        agentId = agent.Id,
                    });
            }
        }

        async Task AssertDraftAuthorityRejected(byte[] bytes, string hash)
        {
            await connection.ExecuteAsync(
                "UPDATE agent SET draft_definition_canonical=@bytes,"
                + " draft_definition_sha256=@hash WHERE id=@agentId",
                new { bytes, hash, agentId = agent.Id });
            try
            {
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => Agents.GetAsync(tenant, agent.Id, default));
            }
            finally
            {
                await connection.ExecuteAsync(
                    "UPDATE agent SET draft_definition_canonical=@bytes,"
                    + " draft_definition_sha256=@hash WHERE id=@agentId",
                    new
                    {
                        bytes = definitionBytes,
                        hash = definitionHash,
                        agentId = agent.Id,
                    });
            }
        }

        var invalidUtf8 = new byte[] { 0xff };
        var invalidJson = "{"u8.ToArray();
        var nonCanonicalSnapshot = Encoding.UTF8.GetBytes(snapshotText + " ");
        var nonCanonicalDefinition = Encoding.UTF8.GetBytes(lifecycleDefinition + " ");
        await AssertRunAuthorityRejected(nonCanonicalSnapshot, run.Hash);
        await AssertRunAuthorityRejected(
            invalidUtf8,
            SkillHash.Sha256(invalidUtf8));
        await AssertRunAuthorityRejected(
            invalidJson,
            SkillHash.Sha256(invalidJson));
        await AssertRunAuthorityRejected(
            run.Bytes,
            new string('0', 64));

        await AssertRevisionAuthorityRejected(nonCanonicalDefinition, revision.Hash);
        await AssertRevisionAuthorityRejected(
            invalidUtf8,
            SkillHash.Sha256(invalidUtf8));
        await AssertRevisionAuthorityRejected(
            invalidJson,
            SkillHash.Sha256(invalidJson));
        await AssertRevisionAuthorityRejected(
            revision.Bytes,
            new string('0', 64));

        await AssertDraftAuthorityRejected(nonCanonicalDefinition, definitionHash);
        await AssertDraftAuthorityRejected(
            invalidUtf8,
            SkillHash.Sha256(invalidUtf8));
        await AssertDraftAuthorityRejected(
            invalidJson,
            SkillHash.Sha256(invalidJson));
        await AssertDraftAuthorityRejected(
            definitionBytes,
            string.Empty);

        foreach (var (table, column, read) in new[]
                 {
                     ("agent_run", "execution_snapshot_canonical", (Func<Task>)(async () =>
                     {
                         await Assert.ThrowsAsync<InvalidOperationException>(
                             () => Runs.GetExecutionArtifactAsync(
                                 tenant, "admin-a", created.Run.Id, default));
                     })),
                     ("agent_revision", "canonical_definition", (Func<Task>)(async () =>
                     {
                         await Assert.ThrowsAsync<InvalidOperationException>(
                             () => Agents.GetRevisionDefinitionAsync(
                                 tenant, agent.Id, 1, default));
                         await Assert.ThrowsAsync<InvalidOperationException>(
                             () => Runs.CreateDirectAsync(
                                 tenant,
                                 "admin-a",
                                 "ADMIN",
                                 agent.Id,
                                 "null revision must not start",
                                 $"canonical-corrupt-{++revisionCorruptionSequence}",
                                 default));
                     })),
                     ("agent", "draft_definition_canonical", (Func<Task>)(async () =>
                     {
                         await Assert.ThrowsAsync<InvalidOperationException>(
                             () => Agents.GetAsync(tenant, agent.Id, default));
                     })),
                 })
        {
            await connection.ExecuteAsync(
                $"UPDATE {table} SET {column}=NULL WHERE "
                + (table == "agent_run"
                    ? "id=@id"
                    : table == "agent_revision"
                        ? "agent_id=@id AND revision=1"
                        : "id=@id"),
                new { id = table == "agent_run" ? created.Run.Id : agent.Id });
            try
            {
                await read();
            }
            finally
            {
                if (table == "agent_run")
                {
                    await connection.ExecuteAsync(
                        "UPDATE agent_run SET execution_snapshot_canonical=@bytes"
                        + " WHERE id=@id",
                        new { bytes = run.Bytes, id = created.Run.Id });
                }
                else if (table == "agent_revision")
                {
                    await connection.ExecuteAsync(
                        "UPDATE agent_revision SET canonical_definition=@bytes"
                        + " WHERE agent_id=@id AND revision=1",
                        new { bytes = revision.Bytes, id = agent.Id });
                }
                else
                {
                    await connection.ExecuteAsync(
                        "UPDATE agent SET draft_definition_canonical=@bytes WHERE id=@id",
                        new { bytes = definitionBytes, id = agent.Id });
                }
            }
        }

        var nullableHashes = (await connection.QueryAsync<string>(
            "SELECT table_name||'.'||column_name"
            + " FROM information_schema.columns"
            + " WHERE table_schema='public' AND is_nullable<>'NO'"
            + " AND (table_name,column_name) IN ("
            + " ('agent','draft_definition_sha256'),"
            + " ('agent_revision','definition_sha256'),"
            + " ('agent_run','snapshot_sha256'))")).ToArray();
        Assert.Empty(nullableHashes);
    }

    [SkippableFact]
    public async Task WaitingInput_RequiresCheckpointIdentity_AndResumeRejectsMissingStoredRef()
    {
        _fixture.SkipIfUnavailable();
        const string tenant = "agentrunrepo-waiting-checkpoint-invariant";
        var agent = await PublishedAgentAsync(tenant, "waiting-checkpoint-invariant");
        var created = await Runs.CreateDirectAsync(
            tenant,
            "admin-a",
            "ADMIN",
            agent.Id,
            "initial prompt",
            "start-key",
            default);
        var lease = await Runs.ClaimLeaseAsync(
            tenant,
            "admin-a",
            created.Run!.Id,
            new AgentRunLeaseRequest(created.Run.StateVersion, "worker-a", 300),
            default);
        var running = await Runs.TransitionAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            new AgentRunTransitionRequest(
                lease.Lease!.Run.StateVersion,
                AgentRunStatuses.Running,
                lease.Lease.LeaseToken,
                LeaseGeneration: lease.Lease.LeaseGeneration,
                ExpectedEventAckCursor: lease.Lease.EventAckCursor),
            default);

        var missingRef = await Runs.TransitionAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            new AgentRunTransitionRequest(
                running.Run!.StateVersion,
                AgentRunStatuses.WaitingInput,
                lease.Lease.LeaseToken,
                LeaseGeneration: lease.Lease.LeaseGeneration,
                ExpectedEventAckCursor: lease.Lease.EventAckCursor,
                CheckpointVersion: 1),
            default);
        var nonAdvancedVersion = await Runs.TransitionAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            new AgentRunTransitionRequest(
                running.Run.StateVersion,
                AgentRunStatuses.WaitingInput,
                lease.Lease.LeaseToken,
                LeaseGeneration: lease.Lease.LeaseGeneration,
                ExpectedEventAckCursor: lease.Lease.EventAckCursor,
                CheckpointRef: V2CheckpointRef(lease.Lease.LeaseGeneration),
                CheckpointVersion: 0),
            default);
        var oversizedPendingInput = await Runs.TransitionAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            new AgentRunTransitionRequest(
                running.Run.StateVersion,
                AgentRunStatuses.WaitingInput,
                lease.Lease.LeaseToken,
                LeaseGeneration: lease.Lease.LeaseGeneration,
                ExpectedEventAckCursor: lease.Lease.EventAckCursor,
                CheckpointRef: V2CheckpointRef(lease.Lease.LeaseGeneration),
                CheckpointVersion: 1,
                PendingInput: JsonSerializer.SerializeToElement(
                    new { value = new string('p', 64 * 1024) })),
            default);
        var oversizedResult = await Runs.TransitionAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            new AgentRunTransitionRequest(
                running.Run.StateVersion,
                AgentRunStatuses.WaitingInput,
                lease.Lease.LeaseToken,
                LeaseGeneration: lease.Lease.LeaseGeneration,
                ExpectedEventAckCursor: lease.Lease.EventAckCursor,
                CheckpointRef: V2CheckpointRef(lease.Lease.LeaseGeneration),
                CheckpointVersion: 1,
                Result: JsonSerializer.SerializeToElement(
                    new { value = new string('r', 1024 * 1024) })),
            default);
        Assert.Equal(AgentRunWriteStatus.InvalidState, missingRef.Status);
        Assert.Equal(AgentRunWriteStatus.InvalidState, nonAdvancedVersion.Status);
        Assert.Equal(AgentRunWriteStatus.InvalidState, oversizedPendingInput.Status);
        Assert.Equal(AgentRunWriteStatus.InvalidState, oversizedResult.Status);

        var waiting = await Runs.TransitionAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            new AgentRunTransitionRequest(
                running.Run.StateVersion,
                AgentRunStatuses.WaitingInput,
                lease.Lease.LeaseToken,
                LeaseGeneration: lease.Lease.LeaseGeneration,
                ExpectedEventAckCursor: lease.Lease.EventAckCursor,
                CheckpointRef: V2CheckpointRef(lease.Lease.LeaseGeneration),
                CheckpointVersion: 1),
            default);
        Assert.Equal(AgentRunWriteStatus.Success, waiting.Status);

        await using (var connection = await _fixture.DataSource!.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE agent_run SET checkpoint_ref=NULL WHERE id=@runId",
                new { runId = created.Run.Id });
        }
        var corruptResume = await Runs.ResumeAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            "must not dispatch",
            1,
            "resume-corrupt-checkpoint",
            default);

        Assert.Equal(AgentRunWriteStatus.InvalidState, corruptResume.Status);
        Assert.Null(corruptResume.Dispatch);
        var unchanged = await Runs.GetAsync(
            tenant, "admin-a", created.Run.Id, default);
        Assert.Equal(AgentRunStatuses.WaitingInput, unchanged!.Status);
        Assert.Null(unchanged.CheckpointRef);
        await using (var connection = await _fixture.DataSource!.OpenConnectionAsync())
        {
            Assert.Equal(
                0,
                await connection.ExecuteScalarAsync<int>(
                    "SELECT count(*) FROM agent_run_command"
                    + " WHERE run_id=@runId AND command_type='resume'",
                    new { runId = created.Run.Id }));
        }
    }

    [SkippableFact]
    public async Task CreateDirect_LegacyPublishedSnapshotOutsideContractFailsBeforeQueue()
    {
        _fixture.SkipIfUnavailable();
        const string tenant = "agentrunrepo-invalid-legacy-snapshot";
        var agent = await PublishedAgentAsync(tenant, "invalid-legacy-snapshot");
        var definition = await Agents.GetRevisionDefinitionAsync(
            tenant, agent.Id, 1, default);
        Assert.NotNull(definition);
        var invalid = JsonNode.Parse(definition!)!.AsObject();
        var oversizedPrompt =
            new string('p', AgentExecutionContract.MaxSystemPromptLength + 1);
        invalid["system_prompt"] = oversizedPrompt;
        var invalidDefinition =
            AgentCanonicalizer.CanonicalizeDefinition(invalid.ToJsonString());
        await using (var connection = await _fixture.DataSource!.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE agent_revision SET system_prompt=@oversizedPrompt,"
                + " canonical_definition=@canonicalDefinition,"
                + " definition_sha256=@definitionHash"
                + " WHERE agent_id=@agentId AND revision=1",
                new
                {
                    oversizedPrompt,
                    canonicalDefinition = Encoding.UTF8.GetBytes(invalidDefinition),
                    definitionHash = SkillHash.Sha256(invalidDefinition),
                    agentId = agent.Id,
                });
        }

        var rejected = await Runs.CreateDirectAsync(
            tenant,
            "admin-a",
            "ADMIN",
            agent.Id,
            "must not queue",
            "invalid-legacy-start",
            default);

        Assert.Equal(AgentRunWriteStatus.InvalidState, rejected.Status);
        Assert.Null(rejected.Run);
        Assert.Null(rejected.Dispatch);
        await using (var connection = await _fixture.DataSource!.OpenConnectionAsync())
        {
            Assert.Equal(
                0,
                await connection.ExecuteScalarAsync<int>(
                    "SELECT count(*) FROM agent_run WHERE tenant_id=@tenant",
                    new { tenant }));
        }
    }

    [SkippableFact]
    public async Task ConcurrentStart_SameIdempotencyKey_PersistsOneRun()
    {
        _fixture.SkipIfUnavailable();
        const string tenant = "agentrunrepo-concurrent";
        var agent = await PublishedAgentAsync(tenant, "concurrent");

        var attempts = await Task.WhenAll(
            Runs.CreateDirectAsync(
                tenant, "admin-a", "ADMIN", agent.Id, "same", "same-key", default),
            Runs.CreateDirectAsync(
                tenant, "admin-a", "ADMIN", agent.Id, "same", "same-key", default));

        Assert.All(attempts, item =>
            Assert.Contains(item.Status, new[]
            {
                AgentRunWriteStatus.Success,
                AgentRunWriteStatus.Replay,
            }));
        Assert.Single(attempts.Select(item => item.Run!.Id).Distinct());

        await using var connection = await _fixture.DataSource!.OpenConnectionAsync();
        Assert.Equal(
            1,
            await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM agent_run WHERE tenant_id=@tenant",
                new { tenant }));
    }

    [SkippableFact]
    public async Task Recovery_DurablyReplaysOriginalInput_ClaimsOnce_AndAckIsScoped()
    {
        _fixture.SkipIfUnavailable();
        const string tenant = "agentrunrepo-recovery";
        const string message = "durable private command input";
        var agent = await PublishedAgentAsync(tenant, "recovery");

        var created = await Runs.CreateDirectAsync(
            tenant,
            "admin-a",
            "ADMIN",
            agent.Id,
            message,
            "recovery-start",
            default);
        Assert.Equal(AgentRunWriteStatus.Success, created.Status);
        Assert.NotNull(created.Dispatch);
        Assert.DoesNotContain(
            message,
            JsonSerializer.Serialize(created.Run),
            StringComparison.Ordinal);

        var replay = await Runs.CreateDirectAsync(
            tenant,
            "admin-a",
            "ADMIN",
            agent.Id,
            message,
            "recovery-start",
            default);
        Assert.Equal(AgentRunWriteStatus.Replay, replay.Status);
        Assert.True(replay.Replayed);
        Assert.Null(replay.Dispatch);
        Assert.Equal(created.Run!.Id, replay.Run!.Id);

        await using (var connection = await _fixture.DataSource!.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE agent_run_command"
                + " SET dispatch_claim_expires_at=now()-interval '1 second'"
                + " WHERE run_id=@runId",
                new { runId = created.Run.Id });
        }

        var claims = await Task.WhenAll(
            Runs.ClaimRecoveryAsync(
                new AgentRunRecoveryClaimRequest("worker-a", 100, 30),
                default),
            Runs.ClaimRecoveryAsync(
                new AgentRunRecoveryClaimRequest("worker-b", 100, 30),
                default));
        var recovered = Assert.Single(
            claims.SelectMany(result => result.Items),
            item => item.RunId == created.Run.Id);
        Assert.Equal(created.Dispatch!.CommandId, recovered.CommandId);
        Assert.Equal("start", recovered.CommandType);
        Assert.Equal(message, recovered.Input.GetProperty("message").GetString());
        Assert.Equal(tenant, recovered.TenantId);
        Assert.Equal("admin-a", recovered.UserId);
        Assert.Equal("ADMIN", recovered.Role);
        Assert.Equal(2, recovered.DispatchAttempt);

        Assert.Equal(
            AgentRunDispatchCompleteStatus.Conflict,
            await Runs.CompleteDispatchAsync(
                tenant,
                "admin-a",
                created.Run.Id,
                recovered.CommandId,
                "wrong-claim",
                default));
        Assert.Equal(
            AgentRunDispatchCompleteStatus.NotFound,
            await Runs.CompleteDispatchAsync(
                tenant + "-other",
                "admin-a",
                created.Run.Id,
                recovered.CommandId,
                recovered.ClaimToken,
                default));
        Assert.Equal(
            AgentRunDispatchCompleteStatus.Success,
            await Runs.CompleteDispatchAsync(
                tenant,
                "admin-a",
                created.Run.Id,
                recovered.CommandId,
                recovered.ClaimToken,
                default));

        var afterAck = await Runs.ClaimRecoveryAsync(
            new AgentRunRecoveryClaimRequest("worker-after-ack", 100, 30),
            default);
        Assert.DoesNotContain(
            afterAck.Items,
            item => item.RunId == created.Run.Id);

        Assert.True(recovered.LeaseGeneration >= 1);
        Assert.NotEmpty(recovered.LeaseToken);
        var whileLeased = await Runs.ClaimRecoveryAsync(
            new AgentRunRecoveryClaimRequest("worker-c", 100, 30),
            default);
        Assert.DoesNotContain(
            whileLeased.Items,
            item => item.RunId == created.Run.Id);
    }

    [SkippableFact]
    public async Task ConcurrentAckAndRecovery_SerializeWithoutReopeningAcknowledgedCommand()
    {
        _fixture.SkipIfUnavailable();
        const string tenant = "agentrunrepo-ack-reclaim-race";
        var agent = await PublishedAgentAsync(tenant, "ack-reclaim-race");

        for (var iteration = 0; iteration < 20; iteration++)
        {
            var created = await Runs.CreateDirectAsync(
                tenant,
                "admin-a",
                "ADMIN",
                agent.Id,
                $"race-{iteration}",
                $"race-start-{iteration}",
                default);
            Assert.NotNull(created.Dispatch);
            await using (var connection = await _fixture.DataSource!.OpenConnectionAsync())
            {
                await connection.ExecuteAsync(
                    "UPDATE agent_run_command"
                    + " SET dispatch_claim_expires_at=now()-interval '1 second'"
                    + " WHERE id=@commandId",
                    new { commandId = created.Dispatch!.CommandId });
            }

            var gate = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var ack = AckAfterGateAsync();
            var reclaim = ReclaimAfterGateAsync();
            gate.SetResult(true);
            await Task.WhenAll(ack, reclaim);

            var reclaimed = reclaim.Result.Items.SingleOrDefault(
                item => item.RunId == created.Run!.Id);
            Assert.True(
                (ack.Result == AgentRunDispatchCompleteStatus.Success)
                ^ (reclaimed is not null),
                $"iteration {iteration}: ACK and reclaim must not both succeed");
            if (reclaimed is null)
            {
                Assert.Equal(AgentRunDispatchCompleteStatus.Success, ack.Result);
            }
            else
            {
                Assert.Equal(AgentRunDispatchCompleteStatus.Conflict, ack.Result);
                Assert.Equal(created.Dispatch.CommandId, reclaimed.CommandId);
                Assert.Equal(2, reclaimed.DispatchAttempt);
            }

            async Task<AgentRunDispatchCompleteStatus> AckAfterGateAsync()
            {
                await gate.Task;
                return await Runs.CompleteDispatchAsync(
                    tenant,
                    "admin-a",
                    created.Run!.Id,
                    created.Dispatch!.CommandId,
                    created.Dispatch.ClaimToken,
                    default);
            }

            async Task<AgentRunRecoveryClaimResponse> ReclaimAfterGateAsync()
            {
                await gate.Task;
                return await Runs.ClaimRecoveryAsync(
                    new AgentRunRecoveryClaimRequest(
                        $"race-worker-{iteration}",
                        100,
                        30),
                    default);
            }
        }
    }

    [SkippableFact]
    public async Task CrashAfterAck_ExpiredExecutionLeaseRecoversOriginalInputOnce()
    {
        _fixture.SkipIfUnavailable();
        const string tenant = "agentrunrepo-crash-after-ack";
        const string message = "recover after accepted worker crashes";
        var agent = await PublishedAgentAsync(tenant, "crash-after-ack");
        var created = await Runs.CreateDirectAsync(
            tenant,
            "admin-a",
            "ADMIN",
            agent.Id,
            message,
            "crash-start",
            default);
        var lease = await Runs.ClaimLeaseAsync(
            tenant,
            "admin-a",
            created.Run!.Id,
            new AgentRunLeaseRequest(created.Run.StateVersion, "crashing-worker", 300),
            default);
        var running = await Runs.TransitionAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            new AgentRunTransitionRequest(
                lease.Lease!.Run.StateVersion,
                AgentRunStatuses.Running,
                lease.Lease.LeaseToken,
                LeaseGeneration: lease.Lease.LeaseGeneration,
                ExpectedEventAckCursor: lease.Lease.EventAckCursor),
            default);
        Assert.Equal(AgentRunWriteStatus.Success, running.Status);
        Assert.Equal(
            AgentRunDispatchCompleteStatus.Success,
            await Runs.CompleteDispatchAsync(
                tenant,
                "admin-a",
                created.Run.Id,
                created.Dispatch!.CommandId,
                created.Dispatch.ClaimToken,
                default));

        await using (var connection = await _fixture.DataSource!.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE agent_run SET lease_expires_at=now()-interval '1 second'"
                + " WHERE id=@runId",
                new { runId = created.Run.Id });
        }

        var recovery = await Runs.ClaimRecoveryAsync(
            new AgentRunRecoveryClaimRequest("replacement-worker", 100, 30),
            default);
        var recovered = Assert.Single(
            recovery.Items,
            item => item.RunId == created.Run.Id);
        Assert.Equal("start", recovered.CommandType);
        Assert.Equal(message, recovered.Input.GetProperty("message").GetString());
        Assert.Equal(2, recovered.DispatchAttempt);

        var concurrentSecondClaim = await Runs.ClaimRecoveryAsync(
            new AgentRunRecoveryClaimRequest("duplicate-worker", 100, 30),
            default);
        Assert.DoesNotContain(
            concurrentSecondClaim.Items,
            item => item.RunId == created.Run.Id);
        Assert.Equal(
            AgentRunDispatchCompleteStatus.Success,
            await Runs.CompleteDispatchAsync(
                tenant,
                "admin-a",
                created.Run.Id,
                recovered.CommandId,
                recovered.ClaimToken,
                default));
        var afterRecoveryAck = await Runs.ClaimRecoveryAsync(
            new AgentRunRecoveryClaimRequest("post-ack-worker", 100, 30),
            default);
        Assert.DoesNotContain(
            afterRecoveryAck.Items,
            item => item.RunId == created.Run.Id);
    }

    [SkippableFact]
    public async Task ResumeCrashAfterAck_ExpiredExecutionLeaseRecoversLatestResumeInput()
    {
        _fixture.SkipIfUnavailable();
        const string tenant = "agentrunrepo-resume-crash-after-ack";
        const string resumeMessage = "durable resume answer";
        var agent = await PublishedAgentAsync(tenant, "resume-crash-after-ack");
        var created = await Runs.CreateDirectAsync(
            tenant,
            "admin-a",
            "ADMIN",
            agent.Id,
            "initial prompt",
            "resume-crash-start",
            default);
        Assert.Equal(
            AgentRunDispatchCompleteStatus.Success,
            await Runs.CompleteDispatchAsync(
                tenant,
                "admin-a",
                created.Run!.Id,
                created.Dispatch!.CommandId,
                created.Dispatch.ClaimToken,
                default));
        var firstLease = await Runs.ClaimLeaseAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            new AgentRunLeaseRequest(created.Run.StateVersion, "worker-a", 300),
            default);
        var running = await Runs.TransitionAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            new AgentRunTransitionRequest(
                firstLease.Lease!.Run.StateVersion,
                AgentRunStatuses.Running,
                firstLease.Lease.LeaseToken,
                LeaseGeneration: firstLease.Lease.LeaseGeneration,
                ExpectedEventAckCursor: firstLease.Lease.EventAckCursor),
            default);
        var waiting = await Runs.TransitionAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            new AgentRunTransitionRequest(
                running.Run!.StateVersion,
                AgentRunStatuses.WaitingInput,
                firstLease.Lease.LeaseToken,
                LeaseGeneration: firstLease.Lease.LeaseGeneration,
                ExpectedEventAckCursor: firstLease.Lease.EventAckCursor,
                CheckpointRef: V2CheckpointRef(firstLease.Lease.LeaseGeneration),
                CheckpointVersion: 1),
            default);
        Assert.Equal(AgentRunWriteStatus.Success, waiting.Status);
        var resumed = await Runs.ResumeAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            resumeMessage,
            1,
            "resume-command",
            default);
        Assert.Equal(AgentRunWriteStatus.Success, resumed.Status);
        var secondLease = await Runs.ClaimLeaseAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            new AgentRunLeaseRequest(
                resumed.Run!.StateVersion,
                "worker-a",
                300),
            default);
        Assert.Equal(AgentRunWriteStatus.Success, secondLease.Status);
        var resumedRunning = await Runs.TransitionAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            new AgentRunTransitionRequest(
                secondLease.Lease!.Run.StateVersion,
                AgentRunStatuses.Running,
                secondLease.Lease.LeaseToken,
                LeaseGeneration: secondLease.Lease.LeaseGeneration,
                ExpectedEventAckCursor: secondLease.Lease.EventAckCursor),
            default);
        Assert.Equal(AgentRunWriteStatus.Success, resumedRunning.Status);
        var secondCheckpointRef =
            V2CheckpointRef(secondLease.Lease.LeaseGeneration);
        var payloadMutation = await Runs.TransitionAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            new AgentRunTransitionRequest(
                resumedRunning.Run!.StateVersion,
                AgentRunStatuses.Running,
                secondLease.Lease.LeaseToken,
                LeaseGeneration: secondLease.Lease.LeaseGeneration,
                ExpectedEventAckCursor: secondLease.Lease.EventAckCursor,
                CheckpointRef: secondCheckpointRef,
                CheckpointVersion: 2,
                Result: JsonSerializer.SerializeToElement(
                    new { forbidden = true })),
            default);
        Assert.Equal(AgentRunWriteStatus.InvalidState, payloadMutation.Status);
        var secondPromotion = await Runs.TransitionAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            new AgentRunTransitionRequest(
                resumedRunning.Run.StateVersion,
                AgentRunStatuses.Running,
                secondLease.Lease.LeaseToken,
                LeaseGeneration: secondLease.Lease.LeaseGeneration,
                ExpectedEventAckCursor: secondLease.Lease.EventAckCursor,
                CheckpointRef: secondCheckpointRef,
                CheckpointVersion: 2),
            default);
        Assert.Equal(AgentRunWriteStatus.Success, secondPromotion.Status);
        var staleDirectClaim = await Runs.ClaimCommandAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            resumed.Dispatch!.CommandId,
            new AgentRunCommandClaimRequest("late-direct-worker", 30),
            default);
        Assert.Equal(
            AgentRunWriteStatus.InvalidState,
            staleDirectClaim.Status);
        Assert.Equal(
            secondPromotion.Run!.StateVersion,
            (await Runs.GetAsync(
                tenant,
                "admin-a",
                created.Run.Id,
                default))!.StateVersion);
        Assert.Equal(
            AgentRunDispatchCompleteStatus.Success,
            await Runs.CompleteDispatchAsync(
                tenant,
                "admin-a",
                created.Run.Id,
                resumed.Dispatch.CommandId,
                resumed.Dispatch.ClaimToken,
                default));

        await using (var connection = await _fixture.DataSource!.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE agent_run SET lease_expires_at=now()-interval '1 second'"
                + " WHERE id=@runId",
                new { runId = created.Run.Id });
        }

        var recovery = await Runs.ClaimRecoveryAsync(
            new AgentRunRecoveryClaimRequest("resume-replacement", 100, 30),
            default);
        var recovered = Assert.Single(
            recovery.Items,
            item => item.RunId == created.Run.Id);
        Assert.Equal("resume", recovered.CommandType);
        Assert.Equal(
            resumeMessage,
            recovered.Input.GetProperty("message").GetString());
        Assert.Equal(
            1,
            recovered.Input.GetProperty("expected_checkpoint_version").GetInt64());
        Assert.Equal(
            waiting.Run!.CheckpointRef,
            recovered.Input.GetProperty("expected_checkpoint_ref").GetString());
        Assert.Equal(AgentRunStatuses.Running, recovered.RunStatus);
        Assert.Equal(secondCheckpointRef, recovered.CheckpointRef);
        Assert.Equal(2, recovered.CheckpointVersion);
        var thirdCheckpointRef = V2CheckpointRef(recovered.LeaseGeneration);
        var thirdPromotion = await Runs.TransitionAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            new AgentRunTransitionRequest(
                recovered.StateVersion,
                AgentRunStatuses.Running,
                recovered.LeaseToken,
                LeaseGeneration: recovered.LeaseGeneration,
                ExpectedEventAckCursor: recovered.EventAckCursor,
                CheckpointRef: thirdCheckpointRef,
                CheckpointVersion: 3),
            default);
        Assert.Equal(AgentRunWriteStatus.Success, thirdPromotion.Status);
        Assert.Equal(
            AgentRunDispatchCompleteStatus.Success,
            await Runs.CompleteDispatchAsync(
                tenant,
                "admin-a",
                created.Run.Id,
                recovered.CommandId,
                recovered.ClaimToken,
                default));
        await using (var connection = await _fixture.DataSource!.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE agent_run SET lease_expires_at=now()-interval '1 second'"
                + " WHERE id=@runId;"
                + " UPDATE agent_run_command"
                + " SET dispatch_completed_at=now()-interval '31 seconds'"
                + " WHERE id=@commandId",
                new
                {
                    runId = created.Run.Id,
                    commandId = recovered.CommandId,
                });
        }

        var secondRecovery = Assert.Single(
            (await Runs.ClaimRecoveryAsync(
                new AgentRunRecoveryClaimRequest(
                    "resume-second-replacement",
                    100,
                    30),
                default)).Items,
            item => item.RunId == created.Run.Id);
        Assert.Equal("resume", secondRecovery.CommandType);
        Assert.Equal(
            1,
            secondRecovery.Input
                .GetProperty("expected_checkpoint_version")
                .GetInt64());
        Assert.Equal(
            waiting.Run.CheckpointRef,
            secondRecovery.Input.GetProperty("expected_checkpoint_ref").GetString());
        Assert.Equal(thirdCheckpointRef, secondRecovery.CheckpointRef);
        Assert.Equal(3, secondRecovery.CheckpointVersion);
        Assert.DoesNotContain(
            (await Runs.ClaimRecoveryAsync(
                new AgentRunRecoveryClaimRequest("resume-duplicate", 100, 30),
                default)).Items,
            item => item.RunId == created.Run.Id);
    }

    [SkippableFact]
    public async Task SlowAllocation_MintsDispatchClaimAfterSnapshotLockWait()
    {
        _fixture.SkipIfUnavailable();
        const string tenant = "agentrunrepo-delayed-claim";
        var agent = await PublishedAgentAsync(tenant, "delayed-claim");
        await using var blocker = await _fixture.DataSource!.OpenConnectionAsync();
        await using var blockerTx = await blocker.BeginTransactionAsync();
        await blocker.ExecuteAsync(
            "SELECT id FROM agent WHERE id=@agentId FOR UPDATE",
            new { agentId = agent.Id },
            blockerTx);

        var allocation = Runs.CreateDirectAsync(
            tenant,
            "admin-a",
            "ADMIN",
            agent.Id,
            "delayed",
            "delayed-start",
            default);
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.False(allocation.IsCompleted);

        var releasedAt = DateTime.UtcNow;
        await blockerTx.CommitAsync();
        var created = await allocation;

        Assert.NotNull(created.Dispatch);
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        var window = await connection.QuerySingleAsync<DispatchWindowRow>(
            "SELECT last_dispatch_at AS LastDispatchAt,"
            + " dispatch_claim_expires_at AS ClaimExpiresAt"
            + " FROM agent_run_command WHERE id=@commandId",
            new { commandId = created.Dispatch.CommandId });
        Assert.True(
            window.LastDispatchAt >= releasedAt.AddMilliseconds(-500),
            "dispatch claim must start after the allocation lock wait, not before it");
        Assert.InRange(
            (window.ClaimExpiresAt - window.LastDispatchAt).TotalSeconds,
            29.9,
            30.1);
    }

    [SkippableFact]
    public async Task Cancel_QueuesOneDurableCommand_ThenWorkerTerminatesExactlyOnce()
    {
        _fixture.SkipIfUnavailable();
        const string tenant = "agentrunrepo-immediate-cancel";
        var agent = await PublishedAgentAsync(tenant, "immediate-cancel");
        var created = await Runs.CreateDirectAsync(
            tenant, "admin-a", "ADMIN", agent.Id, "cancel", "start", default);

        var first = await Runs.CancelAsync(
            tenant, "admin-a", created.Run!.Id, "stop", "cancel", default);
        var replay = await Runs.CancelAsync(
            tenant, "admin-a", created.Run.Id, "stop", "cancel", default);
        var repeatedNewKey = await Runs.CancelAsync(
            tenant, "admin-a", created.Run.Id, "already stopped", "cancel-noop", default);
        var events = await Runs.GetEventsAsync(
            tenant, "admin-a", created.Run.Id, 0, 100, default);

        Assert.Equal(AgentRunWriteStatus.Success, first.Status);
        Assert.Equal(AgentRunWriteStatus.Replay, replay.Status);
        Assert.Equal(AgentRunWriteStatus.Replay, repeatedNewKey.Status);
        Assert.Null(repeatedNewKey.Dispatch);
        Assert.Equal(first.Run!.StateVersion, repeatedNewKey.Run!.StateVersion);
        Assert.DoesNotContain(events!.Events, item => item.EventType == "run_cancelled");
        Assert.Single(events.Events, item => item.EventType == "cancel_requested");
        Assert.NotNull(first.Dispatch);
        var directClaim = await Runs.ClaimCommandAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            first.Dispatch.CommandId,
            new AgentRunCommandClaimRequest("direct-cancel-cleanup", 30),
            default);
        Assert.Equal(AgentRunWriteStatus.Success, directClaim.Status);
        Assert.Equal(JsonValueKind.Undefined, directClaim.Item!.Input.ValueKind);
        Assert.Equal(JsonValueKind.Undefined, directClaim.Item.Snapshot.ValueKind);
        Assert.Equal("ADMIN", directClaim.Item.Role);
        Assert.Equal(
            AgentRunStatuses.Cancelled,
            directClaim.Item.TargetTerminal);

        await using (var connection = await _fixture.DataSource!.OpenConnectionAsync())
        {
            Assert.Equal(
                1,
                await connection.ExecuteScalarAsync<int>(
                    "SELECT count(*) FROM agent_run_command"
                    + " WHERE run_id=@runId AND command_type='cancel'",
                    new { runId = created.Run.Id }));
            await connection.ExecuteAsync(
                "UPDATE agent_run_command"
                + " SET dispatch_claim_expires_at=now()-interval '1 second'"
                + " WHERE id=@commandId",
                new { commandId = first.Dispatch.CommandId });
        }
        var recovered = await Runs.ClaimRecoveryAsync(
            new AgentRunRecoveryClaimRequest("cancel-scrubber", 100, 30),
            default);
        var cancelCommand = Assert.Single(
            recovered.Items,
            item => item.RunId == created.Run.Id);
        Assert.Equal("cancel", cancelCommand.CommandType);
        Assert.Equal(AgentRunStatuses.Queued, cancelCommand.RunStatus);
        Assert.Equal(JsonValueKind.Undefined, cancelCommand.Input.ValueKind);
        Assert.Equal(JsonValueKind.Undefined, cancelCommand.Snapshot.ValueKind);
        Assert.Equal("ADMIN", cancelCommand.Role);
        Assert.Equal(
            AgentRunStatuses.Cancelled,
            cancelCommand.TargetTerminal);

        var cancelledEvent = await Runs.AppendEventsAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            new AgentRunEventsAppendRequest(
                cancelCommand.StateVersion,
                cancelCommand.LeaseToken,
                cancelCommand.LeaseGeneration,
                cancelCommand.EventAckCursor,
                new[]
                {
                    new AgentRunEventAppend(
                        Guid.NewGuid(),
                        "run_cancelled",
                        "worker",
                        cancelCommand.SnapshotHash,
                        JsonSerializer.SerializeToElement(new { status = "cancelled" })),
                }),
            default);
        Assert.Equal(AgentRunWriteStatus.Success, cancelledEvent.Status);

        var terminal = await Runs.TransitionAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            new AgentRunTransitionRequest(
                cancelCommand.StateVersion,
                AgentRunStatuses.Cancelled,
                cancelCommand.LeaseToken,
                LeaseGeneration: cancelCommand.LeaseGeneration,
                ExpectedEventAckCursor: cancelledEvent.Run!.EventAckCursor,
                CheckpointRef: V2CheckpointRef(cancelCommand.LeaseGeneration),
                CheckpointVersion: 1),
            default);
        Assert.Equal(AgentRunWriteStatus.Success, terminal.Status);

        Assert.Equal(
            AgentRunDispatchCompleteStatus.Success,
            await Runs.CompleteDispatchAsync(
                tenant,
                "admin-a",
                created.Run.Id,
                cancelCommand.CommandId,
                cancelCommand.ClaimToken,
                default));
        var afterAck = await Runs.ClaimRecoveryAsync(
            new AgentRunRecoveryClaimRequest("cancel-scrubber-2", 100, 30),
            default);
        Assert.DoesNotContain(
            afterAck.Items,
            item => item.RunId == created.Run.Id);
        var finalEvents = await Runs.GetEventsAsync(
            tenant, "admin-a", created.Run.Id, 0, 100, default);
        Assert.Single(finalEvents!.Events, item => item.EventType == "run_cancelled");
    }

    [SkippableFact]
    public async Task LeaseGeneration_RenewalAndConcurrentTakeoverFenceOldOwner()
    {
        _fixture.SkipIfUnavailable();
        const string tenant = "agentrunrepo-generation-fencing";
        var agent = await PublishedAgentAsync(tenant, "generation-fencing");
        var created = await Runs.CreateDirectAsync(
            tenant, "admin-a", "ADMIN", agent.Id, "start", "start", default);

        var first = await Runs.ClaimLeaseAsync(
            tenant,
            "admin-a",
            created.Run!.Id,
            new AgentRunLeaseRequest(created.Run.StateVersion, "worker-a", 300),
            default);
        Assert.Equal(AgentRunWriteStatus.Success, first.Status);
        Assert.Equal(1, first.Lease!.LeaseGeneration);

        var staleVersionRenewal = await Runs.ClaimLeaseAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            new AgentRunLeaseRequest(created.Run.StateVersion, "worker-a", 300),
            default);
        Assert.Equal(AgentRunWriteStatus.Conflict, staleVersionRenewal.Status);

        var renewed = await Runs.ClaimLeaseAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            new AgentRunLeaseRequest(first.Lease.Run.StateVersion, "worker-a", 300),
            default);
        Assert.Equal(AgentRunWriteStatus.Success, renewed.Status);
        Assert.Equal(first.Lease.LeaseGeneration, renewed.Lease!.LeaseGeneration);
        Assert.NotEqual(first.Lease.LeaseToken, renewed.Lease.LeaseToken);

        var activeOwnerMismatch = await Runs.ClaimLeaseAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            new AgentRunLeaseRequest(
                renewed.Lease.Run.StateVersion,
                "worker-b",
                300),
            default);
        Assert.Equal(AgentRunWriteStatus.InvalidState, activeOwnerMismatch.Status);

        await using (var connection = await _fixture.DataSource!.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE agent_run SET lease_expires_at=clock_timestamp()-interval '1 second'"
                + " WHERE id=@runId",
                new { runId = created.Run.Id });
        }

        var takeoverAttempts = await Task.WhenAll(
            Runs.ClaimLeaseAsync(
                tenant,
                "admin-a",
                created.Run.Id,
                new AgentRunLeaseRequest(
                    renewed.Lease.Run.StateVersion,
                    "worker-b",
                    300),
                default),
            Runs.ClaimLeaseAsync(
                tenant,
                "admin-a",
                created.Run.Id,
                new AgentRunLeaseRequest(
                    renewed.Lease.Run.StateVersion,
                    "worker-c",
                    300),
                default));
        var takeover = Assert.Single(
            takeoverAttempts,
            result => result.Status == AgentRunWriteStatus.Success);
        var takeoverOwner = takeoverAttempts[0].Status == AgentRunWriteStatus.Success
            ? "worker-b"
            : "worker-c";
        Assert.Single(
            takeoverAttempts,
            result => result.Status == AgentRunWriteStatus.Conflict);
        Assert.Equal(
            renewed.Lease.LeaseGeneration + 1,
            takeover.Lease!.LeaseGeneration);

        var oldGenerationEvent = await Runs.AppendEventsAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            new AgentRunEventsAppendRequest(
                takeover.Lease.Run.StateVersion,
                renewed.Lease.LeaseToken,
                renewed.Lease.LeaseGeneration,
                takeover.Lease.EventAckCursor,
                new[]
                {
                    NewRunEvent(
                        "model_step",
                        takeover.Lease.Run.SnapshotHash,
                        new { owner = "stale" }),
                }),
            default);
        Assert.Equal(AgentRunWriteStatus.Conflict, oldGenerationEvent.Status);

        var takeoverRenewal = await Runs.ClaimLeaseAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            new AgentRunLeaseRequest(
                takeover.Lease.Run.StateVersion,
                takeoverOwner,
                300),
            default);
        Assert.Equal(AgentRunWriteStatus.Success, takeoverRenewal.Status);
        Assert.Equal(
            takeover.Lease.LeaseGeneration,
            takeoverRenewal.Lease!.LeaseGeneration);
    }

    [SkippableFact]
    public async Task CheckpointPromotion_RequiresStrictV2RefForCurrentGeneration()
    {
        _fixture.SkipIfUnavailable();
        const string tenant = "agentrunrepo-checkpoint-v2";
        var agent = await PublishedAgentAsync(tenant, "checkpoint-v2");
        var created = await Runs.CreateDirectAsync(
            tenant, "admin-a", "ADMIN", agent.Id, "start", "start", default);
        var lease = await Runs.ClaimLeaseAsync(
            tenant,
            "admin-a",
            created.Run!.Id,
            new AgentRunLeaseRequest(created.Run.StateVersion, "worker-a", 300),
            default);
        var running = await Runs.TransitionAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            new AgentRunTransitionRequest(
                lease.Lease!.Run.StateVersion,
                AgentRunStatuses.Running,
                lease.Lease.LeaseToken,
                lease.Lease.LeaseGeneration,
                lease.Lease.EventAckCursor),
            default);
        Assert.Equal(AgentRunWriteStatus.Success, running.Status);

        var strictFailures = new[]
        {
            "checkpoint-legacy",
            V2CheckpointRef(lease.Lease.LeaseGeneration + 1),
            $"v2:{lease.Lease.LeaseGeneration}:{new string('C', 64)}:{Guid.NewGuid():D}",
            V2CheckpointRef(lease.Lease.LeaseGeneration) + "\n",
            $"v2:{lease.Lease.LeaseGeneration}:{new string('c', 64)}:{Guid.NewGuid():B}",
        };
        foreach (var checkpointRef in strictFailures)
        {
            var rejected = await Runs.TransitionAsync(
                tenant,
                "admin-a",
                created.Run.Id,
                new AgentRunTransitionRequest(
                    running.Run!.StateVersion,
                    AgentRunStatuses.WaitingInput,
                    lease.Lease.LeaseToken,
                    lease.Lease.LeaseGeneration,
                    lease.Lease.EventAckCursor,
                    checkpointRef,
                    1),
                default);
            Assert.Equal(AgentRunWriteStatus.InvalidState, rejected.Status);
        }

        var canonicalRef = V2CheckpointRef(lease.Lease.LeaseGeneration);
        var promoted = await Runs.TransitionAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            new AgentRunTransitionRequest(
                running.Run!.StateVersion,
                AgentRunStatuses.WaitingInput,
                lease.Lease.LeaseToken,
                lease.Lease.LeaseGeneration,
                lease.Lease.EventAckCursor,
                canonicalRef,
                1),
            default);
        Assert.Equal(AgentRunWriteStatus.Success, promoted.Status);
        Assert.Equal(lease.Lease.LeaseGeneration, promoted.Run!.CheckpointGeneration);
        Assert.Equal(canonicalRef, promoted.Run.CheckpointRef);
        Assert.Equal(1, promoted.Run.CheckpointVersion);

        // Moving the same logical checkpoint into a replacement execution
        // generation must not invalidate a resume version already shown to a
        // caller. The generation/ref and state-version CAS fence the physical
        // clone; only a new logical graph checkpoint advances the public
        // checkpoint version.
        var resumed = await Runs.ResumeAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            "resume after worker replacement",
            promoted.Run.CheckpointVersion,
            "replacement-resume",
            default);
        Assert.Equal(AgentRunWriteStatus.Success, resumed.Status);
        var replacementClaim = await Runs.ClaimCommandAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            resumed.Dispatch!.CommandId,
            new AgentRunCommandClaimRequest("worker-b", 300),
            default);
        Assert.Equal(AgentRunWriteStatus.Success, replacementClaim.Status);
        var replacement = Assert.IsType<AgentRunRecoveryItem>(
            replacementClaim.Item);
        Assert.Equal(
            lease.Lease.LeaseGeneration,
            replacement.LeaseGeneration);
        var generationOnlyRef = V2CheckpointRef(
            replacement.LeaseGeneration + 1);

        // A physical clone into a later generation may preserve the logical
        // resume version. Use a recovery-style replacement generation here
        // to exercise that narrow promotion contract.
        await using (var connection = await _fixture.DataSource!.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE agent_run SET lease_generation=lease_generation+1,"
                + " lease_token_sha256=@tokenHash WHERE id=@runId",
                new
                {
                    runId = created.Run.Id,
                    tokenHash = SkillHash.Sha256(replacement.LeaseToken!),
                });
        }
        var generationOnlyPromotion = await Runs.TransitionAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            new AgentRunTransitionRequest(
                replacement.StateVersion,
                AgentRunStatuses.Running,
                replacement.LeaseToken,
                replacement.LeaseGeneration + 1,
                replacement.EventAckCursor,
                generationOnlyRef,
                promoted.Run.CheckpointVersion),
            default);
        Assert.Equal(AgentRunWriteStatus.Success, generationOnlyPromotion.Status);
        Assert.Equal(
            promoted.Run.CheckpointVersion,
            generationOnlyPromotion.Run!.CheckpointVersion);
        Assert.Equal(
            replacement.LeaseGeneration + 1,
            generationOnlyPromotion.Run.CheckpointGeneration);
    }

    [SkippableFact]
    public async Task EventOutbox_RequiresContiguousCursorAndExactReplayAcrossGenerations()
    {
        _fixture.SkipIfUnavailable();
        const string tenant = "agentrunrepo-event-outbox";
        var agent = await PublishedAgentAsync(tenant, "event-outbox");
        var created = await Runs.CreateDirectAsync(
            tenant, "admin-a", "ADMIN", agent.Id, "start", "start", default);
        var firstLease = await Runs.ClaimLeaseAsync(
            tenant,
            "admin-a",
            created.Run!.Id,
            new AgentRunLeaseRequest(created.Run.StateVersion, "worker-a", 300),
            default);
        var running = await Runs.TransitionAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            new AgentRunTransitionRequest(
                firstLease.Lease!.Run.StateVersion,
                AgentRunStatuses.Running,
                firstLease.Lease.LeaseToken,
                firstLease.Lease.LeaseGeneration,
                firstLease.Lease.EventAckCursor),
            default);
        var batch = new[]
        {
            NewRunEvent(
                "model_step",
                running.Run!.SnapshotHash,
                new { step = 1 }),
            NewRunEvent(
                "tool_completed",
                running.Run.SnapshotHash,
                new { step = 2 }),
        };

        var appended = await Runs.AppendEventsAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            new AgentRunEventsAppendRequest(
                running.Run.StateVersion,
                firstLease.Lease.LeaseToken,
                firstLease.Lease.LeaseGeneration,
                0,
                batch),
            default);
        Assert.Equal(AgentRunWriteStatus.Success, appended.Status);
        Assert.Equal(2, appended.Run!.EventAckCursor);

        var exactReplay = await Runs.AppendEventsAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            new AgentRunEventsAppendRequest(
                running.Run.StateVersion,
                firstLease.Lease.LeaseToken,
                firstLease.Lease.LeaseGeneration,
                0,
                batch),
            default);
        Assert.Equal(AgentRunWriteStatus.Replay, exactReplay.Status);
        Assert.True(exactReplay.Replayed);

        var third = NewRunEvent(
            "model_step",
            running.Run.SnapshotHash,
            new { step = 3 });
        var partialOverlap = await Runs.AppendEventsAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            new AgentRunEventsAppendRequest(
                running.Run.StateVersion,
                firstLease.Lease.LeaseToken,
                firstLease.Lease.LeaseGeneration,
                1,
                new[] { batch[1], third }),
            default);
        Assert.Equal(AgentRunWriteStatus.Conflict, partialOverlap.Status);
        var gap = await Runs.AppendEventsAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            new AgentRunEventsAppendRequest(
                running.Run.StateVersion,
                firstLease.Lease.LeaseToken,
                firstLease.Lease.LeaseGeneration,
                3,
                new[] { third }),
            default);
        Assert.Equal(AgentRunWriteStatus.Conflict, gap.Status);
        var divergentReplay = await Runs.AppendEventsAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            new AgentRunEventsAppendRequest(
                running.Run.StateVersion,
                firstLease.Lease.LeaseToken,
                firstLease.Lease.LeaseGeneration,
                0,
                new[]
                {
                    batch[0] with
                    {
                        Payload = JsonSerializer.SerializeToElement(new { step = 99 }),
                    },
                    batch[1],
                }),
            default);
        Assert.Equal(AgentRunWriteStatus.Conflict, divergentReplay.Status);

        await using (var connection = await _fixture.DataSource!.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE agent_run SET lease_expires_at=clock_timestamp()-interval '1 second'"
                + " WHERE id=@runId",
                new { runId = created.Run.Id });
        }
        var secondLease = await Runs.ClaimLeaseAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            new AgentRunLeaseRequest(
                running.Run.StateVersion,
                "worker-b",
                300),
            default);
        Assert.Equal(AgentRunWriteStatus.Success, secondLease.Status);
        Assert.Equal(
            firstLease.Lease.LeaseGeneration + 1,
            secondLease.Lease!.LeaseGeneration);
        Assert.Equal(2, secondLease.Lease.EventAckCursor);

        var oldGeneration = await Runs.AppendEventsAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            new AgentRunEventsAppendRequest(
                secondLease.Lease.Run.StateVersion,
                firstLease.Lease.LeaseToken,
                firstLease.Lease.LeaseGeneration,
                2,
                new[] { third }),
            default);
        Assert.Equal(AgentRunWriteStatus.Conflict, oldGeneration.Status);
        var nextGeneration = await Runs.AppendEventsAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            new AgentRunEventsAppendRequest(
                secondLease.Lease.Run.StateVersion,
                secondLease.Lease.LeaseToken,
                secondLease.Lease.LeaseGeneration,
                2,
                new[] { third }),
            default);
        Assert.Equal(AgentRunWriteStatus.Success, nextGeneration.Status);
        Assert.Equal(3, nextGeneration.Run!.EventAckCursor);

        var events = await Runs.GetEventsAsync(
            tenant, "admin-a", created.Run.Id, 0, 100, default);
        var outbox = events!.Events
            .Where(item => item.EventCursor is not null)
            .OrderBy(item => item.EventCursor)
            .ToArray();
        Assert.Collection(
            outbox,
            item =>
            {
                Assert.Equal(0, item.EventCursor);
                Assert.Equal(firstLease.Lease.LeaseGeneration, item.LeaseGeneration);
            },
            item =>
            {
                Assert.Equal(1, item.EventCursor);
                Assert.Equal(firstLease.Lease.LeaseGeneration, item.LeaseGeneration);
            },
            item =>
            {
                Assert.Equal(2, item.EventCursor);
                Assert.Equal(secondLease.Lease.LeaseGeneration, item.LeaseGeneration);
            });
    }

    [SkippableFact]
    public async Task Deadline_UsesPinnedDatabaseClockAndStopsClaimsRecoveryAndEvents()
    {
        _fixture.SkipIfUnavailable();
        const string tenant = "agentrunrepo-deadline";
        var agent = await PublishedAgentAsync(tenant, "deadline");
        var created = await Runs.CreateDirectAsync(
            tenant, "admin-a", "ADMIN", agent.Id, "start", "start", default);
        Assert.Equal(
            AgentExecutionContract.DefaultTimeoutSeconds,
            created.Run!.RuntimeLimits
                .GetProperty("effective_timeout_seconds")
                .GetInt32());
        await using (var connection = await _fixture.DataSource!.OpenConnectionAsync())
        {
            var clock = await connection.QuerySingleAsync<DeadlineClockRow>(
                "SELECT clock_timestamp() AS DbNow,deadline_at AS DeadlineAt"
                + " FROM agent_run WHERE id=@runId",
                new { runId = created.Run.Id });
            Assert.InRange(
                (clock.DeadlineAt - clock.DbNow).TotalSeconds,
                AgentExecutionContract.DefaultTimeoutSeconds - 5,
                AgentExecutionContract.DefaultTimeoutSeconds + 1);
        }

        var lease = await Runs.ClaimLeaseAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            new AgentRunLeaseRequest(created.Run.StateVersion, "worker-a", 300),
            default);
        var running = await Runs.TransitionAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            new AgentRunTransitionRequest(
                lease.Lease!.Run.StateVersion,
                AgentRunStatuses.Running,
                lease.Lease.LeaseToken,
                lease.Lease.LeaseGeneration,
                lease.Lease.EventAckCursor),
            default);
        Assert.Equal(AgentRunWriteStatus.Success, running.Status);
        await using (var connection = await _fixture.DataSource!.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE agent_run SET deadline_at=clock_timestamp()-interval '1 second',"
                + " lease_expires_at=clock_timestamp()+interval '5 minutes'"
                + " WHERE id=@runId",
                new { runId = created.Run.Id });
        }

        var blockedEvent = await Runs.AppendEventsAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            new AgentRunEventsAppendRequest(
                running.Run!.StateVersion,
                lease.Lease.LeaseToken,
                lease.Lease.LeaseGeneration,
                0,
                new[]
                {
                    NewRunEvent(
                        "model_step",
                        running.Run.SnapshotHash,
                        new { after = "deadline" }),
                }),
            default);
        Assert.Equal(AgentRunWriteStatus.InvalidState, blockedEvent.Status);
        var blockedLease = await Runs.ClaimLeaseAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            new AgentRunLeaseRequest(
                running.Run.StateVersion,
                "worker-a",
                300),
            default);
        Assert.Equal(AgentRunWriteStatus.InvalidState, blockedLease.Status);
        var blockedCommand = await Runs.ClaimCommandAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            created.Dispatch!.CommandId,
            new AgentRunCommandClaimRequest("worker-a", 30),
            default);
        Assert.Equal(AgentRunWriteStatus.InvalidState, blockedCommand.Status);
        var recovery = await Runs.ClaimRecoveryAsync(
            new AgentRunRecoveryClaimRequest("recovery-worker", 100, 30),
            default);
        var cleanup = Assert.Single(
            recovery.Items,
            item => item.RunId == created.Run.Id);
        Assert.Equal("deadline_cleanup", cleanup.CommandType);
        Assert.Equal(AgentRunStatuses.Failed, cleanup.TargetTerminal);
        Assert.Equal(lease.Lease.LeaseGeneration + 1, cleanup.LeaseGeneration);
        Assert.Equal(JsonValueKind.Undefined, cleanup.Input.ValueKind);
        Assert.Equal(tenant, cleanup.TenantId);
        Assert.Equal("admin-a", cleanup.UserId);
        Assert.Equal("ADMIN", cleanup.Role);
        Assert.Equal(JsonValueKind.Undefined, cleanup.Snapshot.ValueKind);
        var wire = JsonSerializer.Serialize(cleanup);
        Assert.DoesNotContain("\"input\"", wire, StringComparison.Ordinal);
        Assert.DoesNotContain("\"snapshot\"", wire, StringComparison.Ordinal);
        Assert.Contains("\"tenant_id\"", wire, StringComparison.Ordinal);
        Assert.Contains("\"user_id\"", wire, StringComparison.Ordinal);
        Assert.Contains("\"role\"", wire, StringComparison.Ordinal);

        var staleFailure = await Runs.TransitionAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            new AgentRunTransitionRequest(
                cleanup.StateVersion,
                AgentRunStatuses.Failed,
                lease.Lease.LeaseToken,
                lease.Lease.LeaseGeneration,
                0,
                V2CheckpointRef(lease.Lease.LeaseGeneration),
                1,
                ErrorCode: "deadline_exceeded",
                ErrorMessage: "Agent runtime exceeded its authoritative deadline."),
            default);
        Assert.Equal(AgentRunWriteStatus.Conflict, staleFailure.Status);
        var unsafeCleanupEvent = await Runs.AppendEventsAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            new AgentRunEventsAppendRequest(
                cleanup.StateVersion,
                cleanup.LeaseToken,
                cleanup.LeaseGeneration,
                cleanup.EventAckCursor,
                new[]
                {
                    NewRunEvent(
                        "model_step",
                        cleanup.SnapshotHash,
                        new { status = "unsafe" }),
                }),
            default);
        Assert.Equal(AgentRunWriteStatus.InvalidState, unsafeCleanupEvent.Status);
        var audit = await Runs.AppendEventsAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            new AgentRunEventsAppendRequest(
                cleanup.StateVersion,
                cleanup.LeaseToken,
                cleanup.LeaseGeneration,
                cleanup.EventAckCursor,
                new[]
                {
                    NewRunEvent(
                        "deadline_exceeded",
                        cleanup.SnapshotHash,
                        new { error_code = "deadline_exceeded" }),
                }),
            default);
        Assert.Equal(AgentRunWriteStatus.Success, audit.Status);
        var failed = await Runs.TransitionAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            new AgentRunTransitionRequest(
                cleanup.StateVersion,
                AgentRunStatuses.Failed,
                cleanup.LeaseToken,
                cleanup.LeaseGeneration,
                audit.Run!.EventAckCursor,
                V2CheckpointRef(cleanup.LeaseGeneration),
                1,
                ErrorCode: "deadline_exceeded",
                ErrorMessage: "Agent runtime exceeded its authoritative deadline."),
            default);
        Assert.Equal(AgentRunWriteStatus.Success, failed.Status);
        Assert.Equal(AgentRunStatuses.Failed, failed.Run!.Status);
        Assert.Equal(
            AgentRunDispatchCompleteStatus.Success,
            await Runs.CompleteDispatchAsync(
                tenant,
                "admin-a",
                created.Run.Id,
                cleanup.CommandId,
                cleanup.ClaimToken,
                default));
    }

    [SkippableFact]
    public async Task DeadlineCleanup_FreezesCancelledTarget_AndRejectsLateCancelMutation()
    {
        _fixture.SkipIfUnavailable();
        const string tenant = "agentrunrepo-deadline-cancel";
        var agent = await PublishedAgentAsync(tenant, "deadline-cancel");
        var created = await Runs.CreateDirectAsync(
            tenant, "admin-a", "ADMIN", agent.Id, "private", "start", default);
        var cancelled = await Runs.CancelAsync(
            tenant,
            "admin-a",
            created.Run!.Id,
            "cancel wins",
            "cancel-before-deadline",
            default);
        Assert.Equal(AgentRunWriteStatus.Success, cancelled.Status);
        await using (var connection = await _fixture.DataSource!.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE agent_run SET deadline_at=clock_timestamp()-interval '1 second'"
                + " WHERE id=@runId",
                new { runId = created.Run.Id });
        }

        var cleanup = Assert.Single(
            (await Runs.ClaimRecoveryAsync(
                new AgentRunRecoveryClaimRequest("cleanup-worker", 100, 30),
                default)).Items,
            item => item.RunId == created.Run.Id);
        Assert.Equal("deadline_cleanup", cleanup.CommandType);
        Assert.Equal(AgentRunStatuses.Cancelled, cleanup.TargetTerminal);
        var lateCancel = await Runs.CancelAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            "must not change frozen target",
            "late-cancel",
            default);
        Assert.Equal(AgentRunWriteStatus.Conflict, lateCancel.Status);
        Assert.Null(lateCancel.Dispatch);

        var audit = await Runs.AppendEventsAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            new AgentRunEventsAppendRequest(
                cleanup.StateVersion,
                cleanup.LeaseToken,
                cleanup.LeaseGeneration,
                cleanup.EventAckCursor,
                new[]
                {
                    NewRunEvent(
                        "deadline_exceeded",
                        cleanup.SnapshotHash,
                        new { error_code = "deadline_exceeded" }),
                }),
            default);
        var terminal = await Runs.TransitionAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            new AgentRunTransitionRequest(
                cleanup.StateVersion,
                AgentRunStatuses.Cancelled,
                cleanup.LeaseToken,
                cleanup.LeaseGeneration,
                audit.Run!.EventAckCursor,
                V2CheckpointRef(cleanup.LeaseGeneration),
                1,
                ErrorMessage: "Agent runtime exceeded its authoritative deadline."),
            default);
        Assert.Equal(AgentRunWriteStatus.Success, terminal.Status);
        var terminalReplay = await Runs.CancelAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            "terminal replay",
            "terminal-replay",
            default);
        Assert.Equal(AgentRunWriteStatus.Replay, terminalReplay.Status);
        await using (var connection = await _fixture.DataSource!.OpenConnectionAsync())
        {
            Assert.Equal(
                1,
                await connection.ExecuteScalarAsync<int>(
                    "SELECT count(*) FROM agent_run_command"
                    + " WHERE run_id=@runId AND command_type='deadline_cleanup'",
                    new { runId = created.Run.Id }));
            Assert.Equal(
                1,
                await connection.ExecuteScalarAsync<int>(
                    "SELECT count(*) FROM agent_run_command"
                    + " WHERE run_id=@runId AND command_type='cancel'",
                    new { runId = created.Run.Id }));
        }
    }

    [SkippableFact]
    public async Task DeadlineCleanup_ConcurrentCancelFreezesOneLockOrderedTarget()
    {
        _fixture.SkipIfUnavailable();
        const string tenant = "agentrunrepo-deadline-cancel-race";
        var agent = await PublishedAgentAsync(tenant, "deadline-cancel-race");
        var created = await Runs.CreateDirectAsync(
            tenant, "admin-a", "ADMIN", agent.Id, "private", "start", default);
        await using (var connection = await _fixture.DataSource!.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE agent_run SET deadline_at=clock_timestamp()-interval '1 second'"
                + " WHERE id=@runId",
                new { runId = created.Run!.Id });
        }

        var gate = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelTask = CancelAfterGateAsync();
        var recoveryTask = RecoverAfterGateAsync();
        gate.SetResult(true);
        await Task.WhenAll(cancelTask, recoveryTask);
        Assert.Contains(
            cancelTask.Result.Status,
            new[] { AgentRunWriteStatus.Success, AgentRunWriteStatus.Conflict });

        var cleanup = recoveryTask.Result.Items.SingleOrDefault(
            item => item.RunId == created.Run!.Id);
        if (cleanup is null)
        {
            cleanup = Assert.Single(
                (await Runs.ClaimRecoveryAsync(
                    new AgentRunRecoveryClaimRequest("cleanup-worker-2", 100, 30),
                    default)).Items,
                item => item.RunId == created.Run.Id);
        }
        Assert.Equal(
            cancelTask.Result.Status == AgentRunWriteStatus.Success
                ? AgentRunStatuses.Cancelled
                : AgentRunStatuses.Failed,
            cleanup.TargetTerminal);
        await using (var connection = await _fixture.DataSource!.OpenConnectionAsync())
        {
            Assert.Equal(
                1,
                await connection.ExecuteScalarAsync<int>(
                    "SELECT count(*) FROM agent_run_command"
                    + " WHERE run_id=@runId AND command_type='deadline_cleanup'",
                    new { runId = created.Run.Id }));
            Assert.Equal(
                cancelTask.Result.Status == AgentRunWriteStatus.Success ? 1 : 0,
                await connection.ExecuteScalarAsync<int>(
                    "SELECT count(*) FROM agent_run_command"
                    + " WHERE run_id=@runId AND command_type='cancel'",
                    new { runId = created.Run.Id }));
        }

        async Task<AgentRunWriteResult> CancelAfterGateAsync()
        {
            await gate.Task;
            return await Runs.CancelAsync(
                tenant,
                "admin-a",
                created.Run!.Id,
                "race",
                "race-cancel",
                default);
        }

        async Task<AgentRunRecoveryClaimResponse> RecoverAfterGateAsync()
        {
            await gate.Task;
            return await Runs.ClaimRecoveryAsync(
                new AgentRunRecoveryClaimRequest("cleanup-worker", 100, 30),
                default);
        }
    }

    [SkippableFact]
    public async Task Recovery_DeadLettersPoisonCandidates_WithoutBlockingHealthyTenant()
    {
        _fixture.SkipIfUnavailable();
        const string tenant = "agentrunrepo-poison-isolation";
        var agent = await PublishedAgentAsync(tenant, "poison-isolation");
        var corrupt = await Runs.CreateDirectAsync(
            tenant, "admin-a", "ADMIN", agent.Id, "corrupt", "corrupt", default);
        var exhausted = await Runs.CreateDirectAsync(
            tenant, "admin-a", "ADMIN", agent.Id, "exhausted", "exhausted", default);
        var scalarInput = await Runs.CreateDirectAsync(
            tenant, "admin-a", "ADMIN", agent.Id, "scalar", "scalar", default);
        var emptyObjectInput = await Runs.CreateDirectAsync(
            tenant, "admin-a", "ADMIN", agent.Id, "empty", "empty", default);
        var tamperedMessage = await Runs.CreateDirectAsync(
            tenant, "admin-a", "ADMIN", agent.Id, "original", "message", default);
        var tamperedHash = await Runs.CreateDirectAsync(
            tenant, "admin-a", "ADMIN", agent.Id, "hash", "hash", default);
        var tamperedResumeRef = await Runs.CreateDirectAsync(
            tenant, "admin-a", "ADMIN", agent.Id, "resume", "resume", default);
        var healthy = await Runs.CreateDirectAsync(
            tenant, "admin-a", "ADMIN", agent.Id, "healthy", "healthy", default);
        var resumeLease = await Runs.ClaimLeaseAsync(
            tenant,
            "admin-a",
            tamperedResumeRef.Run!.Id,
            new AgentRunLeaseRequest(
                tamperedResumeRef.Run.StateVersion,
                "resume-worker",
                300),
            default);
        var resumeRunning = await Runs.TransitionAsync(
            tenant,
            "admin-a",
            tamperedResumeRef.Run.Id,
            new AgentRunTransitionRequest(
                resumeLease.Lease!.Run.StateVersion,
                AgentRunStatuses.Running,
                resumeLease.Lease.LeaseToken,
                resumeLease.Lease.LeaseGeneration,
                resumeLease.Lease.EventAckCursor),
            default);
        var resumeWaiting = await Runs.TransitionAsync(
            tenant,
            "admin-a",
            tamperedResumeRef.Run.Id,
            new AgentRunTransitionRequest(
                resumeRunning.Run!.StateVersion,
                AgentRunStatuses.WaitingInput,
                resumeLease.Lease.LeaseToken,
                resumeLease.Lease.LeaseGeneration,
                resumeLease.Lease.EventAckCursor,
                V2CheckpointRef(resumeLease.Lease.LeaseGeneration),
                1),
            default);
        Assert.Equal(AgentRunWriteStatus.Success, resumeWaiting.Status);
        Assert.Equal(
            AgentRunWriteStatus.Success,
            (await Runs.ResumeAsync(
                tenant,
                "admin-a",
                tamperedResumeRef.Run.Id,
                "resume answer",
                1,
                "resume-answer",
                default)).Status);
        var unrelatedResumeRef =
            V2CheckpointRef(resumeLease.Lease.LeaseGeneration);
        await using (var connection = await _fixture.DataSource!.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE agent_run SET execution_snapshot_canonical=decode('7b7d','hex')"
                + " WHERE id=@corruptRunId;"
                + " UPDATE agent_run SET lease_generation=@maxGeneration"
                + " WHERE id=@exhaustedRunId;"
                + " UPDATE agent_run_command SET command_input='42'::jsonb"
                + " WHERE run_id=@scalarInputRunId;"
                + " UPDATE agent_run_command SET command_input='{}'::jsonb"
                + " WHERE run_id=@emptyObjectInputRunId;"
                + " UPDATE agent_run_command"
                + " SET command_input=jsonb_build_object('message','changed')"
                + " WHERE run_id=@tamperedMessageRunId;"
                + " UPDATE agent_run_command"
                + " SET command_input_sha256=@tamperedHash"
                + " WHERE run_id=@tamperedHashRunId;"
                + " UPDATE agent_run_command"
                + " SET command_input=jsonb_set(command_input,"
                + " '{expected_checkpoint_ref}',to_jsonb(CAST(@unrelatedResumeRef AS text)))"
                + " WHERE run_id=@tamperedResumeRefRunId"
                + " AND command_type='resume';"
                + " UPDATE agent_run SET lease_expires_at=clock_timestamp()-interval '1 second'"
                + " WHERE id=@tamperedResumeRefRunId;"
                + " UPDATE agent_run_command"
                + " SET dispatch_claim_expires_at=clock_timestamp()-interval '1 second'"
                + " WHERE run_id IN (@corruptRunId,@exhaustedRunId,"
                + " @scalarInputRunId,@emptyObjectInputRunId,"
                + " @tamperedMessageRunId,@tamperedHashRunId,"
                + " @tamperedResumeRefRunId,@healthyRunId)",
                new
                {
                    corruptRunId = corrupt.Run!.Id,
                    exhaustedRunId = exhausted.Run!.Id,
                    scalarInputRunId = scalarInput.Run!.Id,
                    emptyObjectInputRunId = emptyObjectInput.Run!.Id,
                    tamperedMessageRunId = tamperedMessage.Run!.Id,
                    tamperedHashRunId = tamperedHash.Run!.Id,
                    tamperedResumeRefRunId = tamperedResumeRef.Run.Id,
                    healthyRunId = healthy.Run!.Id,
                    maxGeneration = long.MaxValue,
                    tamperedHash = new string('f', 64),
                    unrelatedResumeRef,
                });
        }

        var recovery = await Runs.ClaimRecoveryAsync(
            new AgentRunRecoveryClaimRequest("healthy-worker", 100, 30),
            default);
        var recovered = Assert.Single(
            recovery.Items,
            item => item.RunId == healthy.Run!.Id);
        Assert.Equal("healthy", recovered.Input.GetProperty("message").GetString());
        var corruptResult = await Runs.GetAsync(
            tenant, "admin-a", corrupt.Run!.Id, default);
        var exhaustedResult = await Runs.GetAsync(
            tenant, "admin-a", exhausted.Run!.Id, default);
        var scalarInputResult = await Runs.GetAsync(
            tenant, "admin-a", scalarInput.Run!.Id, default);
        var emptyObjectInputResult = await Runs.GetAsync(
            tenant, "admin-a", emptyObjectInput.Run!.Id, default);
        var tamperedMessageResult = await Runs.GetAsync(
            tenant, "admin-a", tamperedMessage.Run!.Id, default);
        var tamperedHashResult = await Runs.GetAsync(
            tenant, "admin-a", tamperedHash.Run!.Id, default);
        var tamperedResumeRefResult = await Runs.GetAsync(
            tenant, "admin-a", tamperedResumeRef.Run.Id, default);
        Assert.Equal(AgentRunStatuses.Failed, corruptResult!.Status);
        Assert.Equal("run_recovery_snapshot_invalid", corruptResult.ErrorCode);
        Assert.Equal(AgentRunStatuses.Failed, exhaustedResult!.Status);
        Assert.Equal(
            "run_recovery_generation_exhausted",
            exhaustedResult.ErrorCode);
        Assert.Equal(AgentRunStatuses.Failed, scalarInputResult!.Status);
        Assert.Equal(
            "run_recovery_command_invalid",
            scalarInputResult.ErrorCode);
        Assert.Equal(AgentRunStatuses.Failed, emptyObjectInputResult!.Status);
        Assert.Equal(
            "run_recovery_command_invalid",
            emptyObjectInputResult.ErrorCode);
        Assert.Equal(AgentRunStatuses.Failed, tamperedMessageResult!.Status);
        Assert.Equal(
            "run_recovery_command_invalid",
            tamperedMessageResult.ErrorCode);
        Assert.Equal(AgentRunStatuses.Failed, tamperedHashResult!.Status);
        Assert.Equal(
            "run_recovery_command_invalid",
            tamperedHashResult.ErrorCode);
        Assert.Equal(AgentRunStatuses.Failed, tamperedResumeRefResult!.Status);
        Assert.Equal(
            "run_recovery_command_invalid",
            tamperedResumeRefResult.ErrorCode);
        await using (var connection = await _fixture.DataSource!.OpenConnectionAsync())
        {
            Assert.Equal(
                7,
                await connection.ExecuteScalarAsync<int>(
                    "SELECT count(*) FROM agent_run_event"
                    + " WHERE run_id IN (@corruptRunId,@exhaustedRunId,"
                    + " @scalarInputRunId,@emptyObjectInputRunId,"
                    + " @tamperedMessageRunId,@tamperedHashRunId,"
                    + " @tamperedResumeRefRunId)"
                    + " AND event_type='run_dead_lettered'",
                    new
                    {
                        corruptRunId = corrupt.Run.Id,
                        exhaustedRunId = exhausted.Run.Id,
                        scalarInputRunId = scalarInput.Run.Id,
                        emptyObjectInputRunId = emptyObjectInput.Run.Id,
                        tamperedMessageRunId = tamperedMessage.Run.Id,
                        tamperedHashRunId = tamperedHash.Run.Id,
                        tamperedResumeRefRunId = tamperedResumeRef.Run.Id,
                    }));
            Assert.Equal(
                8,
                await connection.ExecuteScalarAsync<int>(
                    "SELECT count(*) FROM agent_run_command"
                    + " WHERE run_id IN (@corruptRunId,@exhaustedRunId,"
                    + " @scalarInputRunId,@emptyObjectInputRunId,"
                    + " @tamperedMessageRunId,@tamperedHashRunId,"
                    + " @tamperedResumeRefRunId)"
                    + " AND command_input='{}'::jsonb"
                    + " AND dispatch_completed_at IS NOT NULL",
                    new
                    {
                        corruptRunId = corrupt.Run.Id,
                        exhaustedRunId = exhausted.Run.Id,
                        scalarInputRunId = scalarInput.Run.Id,
                        emptyObjectInputRunId = emptyObjectInput.Run.Id,
                        tamperedMessageRunId = tamperedMessage.Run.Id,
                        tamperedHashRunId = tamperedHash.Run.Id,
                        tamperedResumeRefRunId = tamperedResumeRef.Run.Id,
                    }));
        }
    }

    [SkippableFact]
    public async Task CommandClaim_ReturnsAuthoritativeEnvelopeAndCorruptionFailsClosed()
    {
        _fixture.SkipIfUnavailable();
        const string tenant = "agentrunrepo-command-claim";
        const string message = "authoritative private input";
        var agent = await PublishedAgentAsync(tenant, "command-claim");
        var created = await Runs.CreateDirectAsync(
            tenant, "admin-a", "ADMIN", agent.Id, message, "start", default);

        var claimed = await Runs.ClaimCommandAsync(
            tenant,
            "admin-a",
            created.Run!.Id,
            created.Dispatch!.CommandId,
            new AgentRunCommandClaimRequest("workflow-worker", 30),
            default);
        Assert.Equal(AgentRunWriteStatus.Success, claimed.Status);
        var item = Assert.IsType<AgentRunRecoveryItem>(claimed.Item);
        Assert.Equal(created.Dispatch.CommandId, item.CommandId);
        Assert.Equal(created.Run.Id, item.RunId);
        Assert.Equal("start", item.CommandType);
        Assert.Equal(message, item.Input.GetProperty("message").GetString());
        Assert.Equal(tenant, item.TenantId);
        Assert.Equal("admin-a", item.UserId);
        Assert.Equal("ADMIN", item.Role);
        Assert.Equal(created.Run.SnapshotHash, item.SnapshotHash);
        Assert.Equal(1, item.LeaseGeneration);
        Assert.Equal(0, item.CheckpointGeneration);
        Assert.Null(item.CheckpointRef);
        Assert.Equal(0, item.CheckpointVersion);
        Assert.Equal(0, item.EventAckCursor);
        Assert.Equal(created.Run.DeadlineAt, item.DeadlineAt);
        Assert.Equal(2, item.DispatchAttempt);
        using (var snapshot = DecodeSnapshotArtifact(item.Snapshot.GetRawText()))
        {
            Assert.Equal(
                created.Run.Id,
                snapshot.RootElement.GetProperty("run_id").GetGuid());
            Assert.Equal(
                "ADMIN",
                snapshot.RootElement
                    .GetProperty("caller")
                    .GetProperty("role")
                    .GetString());
            Assert.Equal(
                AgentExecutionContract.DefaultTimeoutSeconds,
                snapshot.RootElement
                    .GetProperty("agent")
                    .GetProperty("runtime_limits")
                    .GetProperty("effective_timeout_seconds")
                    .GetInt32());
        }

        Assert.Equal(
            AgentRunDispatchCompleteStatus.Success,
            await Runs.CompleteDispatchAsync(
                tenant,
                "admin-a",
                created.Run.Id,
                item.CommandId,
                item.ClaimToken,
                default));
        var duplicate = await Runs.ClaimCommandAsync(
            tenant,
            "admin-a",
            created.Run.Id,
            item.CommandId,
            new AgentRunCommandClaimRequest("workflow-worker", 30),
            default);
        Assert.Equal(AgentRunWriteStatus.Replay, duplicate.Status);
        Assert.Null(duplicate.Item);

        var corrupt = await Runs.CreateDirectAsync(
            tenant,
            "admin-a",
            "ADMIN",
            agent.Id,
            "must fail before lease mutation",
            "corrupt-start",
            default);
        await using (var connection = await _fixture.DataSource!.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE agent_run SET execution_snapshot_canonical=decode('7b7d','hex')"
                + " WHERE id=@runId",
                new { runId = corrupt.Run!.Id });
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Runs.ClaimCommandAsync(
                tenant,
                "admin-a",
                corrupt.Run!.Id,
                corrupt.Dispatch!.CommandId,
                new AgentRunCommandClaimRequest("workflow-worker", 30),
                default));
        await using (var connection = await _fixture.DataSource!.OpenConnectionAsync())
        {
            var unchanged = await connection.QuerySingleAsync<CommandPreflightRow>(
                "SELECT r.state_version AS StateVersion,"
                + " r.lease_generation AS LeaseGeneration,"
                + " c.dispatch_attempts AS DispatchAttempts,"
                + " c.dispatch_claim_owner AS DispatchClaimOwner"
                + " FROM agent_run r JOIN agent_run_command c ON c.run_id=r.id"
                + " WHERE r.id=@runId AND c.id=@commandId",
                new
                {
                    runId = corrupt.Run!.Id,
                    commandId = corrupt.Dispatch!.CommandId,
                });
            Assert.Equal(corrupt.Run.StateVersion, unchanged.StateVersion);
            Assert.Equal(0, unchanged.LeaseGeneration);
            Assert.Equal(1, unchanged.DispatchAttempts);
            Assert.Equal("platform", unchanged.DispatchClaimOwner);
        }
    }

    [SkippableFact]
    public async Task PinnedSkillSummary_DoesNotDriftWithMutableParentDescription()
    {
        _fixture.SkipIfUnavailable();
        const string tenant = "agentrunrepo-skill-pin";
        const string skillName = "pinned-flow";
        const string definition =
            "name: pinned-flow\ndescription: immutable revision\nrequired_role: USER\nflow: []\n";
        await using (var connection = await _fixture.DataSource!.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                "WITH inserted AS ("
                + " INSERT INTO skill (tenant_id,name,description,current_revision)"
                + " VALUES (@tenant,@skillName,'old parent description',1) RETURNING id)"
                + " INSERT INTO skill_revision"
                + " (skill_id,revision,definition,definition_sha256,created_by,kind)"
                + " SELECT id,1,@definition,@hash,'admin-a','flow' FROM inserted",
                new
                {
                    tenant,
                    skillName,
                    definition,
                    hash = SkillHash.Sha256(definition),
                });
        }
        var agent = await PublishedAgentAsync(tenant, "skill-pin", skillName);

        var first = await Runs.CreateDirectAsync(
            tenant, "admin-a", "ADMIN", agent.Id, "first", "skill-start-1", default);
        var firstArtifact = await Runs.GetExecutionArtifactAsync(
            tenant, "admin-a", first.Run!.Id, default);

        await using (var connection = await _fixture.DataSource!.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE skill SET description='new mutable description'"
                + " WHERE tenant_id=@tenant AND name=@skillName",
                new { tenant, skillName });
        }
        var second = await Runs.CreateDirectAsync(
            tenant, "admin-a", "ADMIN", agent.Id, "second", "skill-start-2", default);
        var secondArtifact = await Runs.GetExecutionArtifactAsync(
            tenant, "admin-a", second.Run!.Id, default);

        using var firstJson = DecodeSnapshotArtifact(firstArtifact!);
        using var secondJson = DecodeSnapshotArtifact(secondArtifact!);
        var firstPin = firstJson.RootElement.GetProperty("skills")[0];
        var secondPin = secondJson.RootElement.GetProperty("skills")[0];
        Assert.Equal("immutable revision", firstPin.GetProperty("description").GetString());
        Assert.Equal("immutable revision", secondPin.GetProperty("description").GetString());
        Assert.Equal(
            firstPin.GetProperty("definition_sha256").GetString(),
            secondPin.GetProperty("definition_sha256").GetString());
    }

    private async Task<Agent> PublishedAgentAsync(
        string tenant,
        string slug,
        string? skillBinding = null)
    {
        var definition = AgentCanonicalizer.Canonicalize(new AgentUpsert(
            Slug: null,
            Name: null,
            Description: null,
            SystemPrompt: "你是可靠的研究助手",
            ExecutionRoles: new[] { "worker" },
            Capabilities: null,
            OutputContract: null,
            Audience: new[] { "ADMIN" },
            AllowedTools: Array.Empty<string>(),
            SkillBindings: skillBinding is null
                ? null
                : new[] { new AgentSkillBinding(skillBinding, "latest") },
            KnowledgeSources: null,
            BusinessRules: null,
            RuntimeLimits: new AgentRuntimeLimits(StepBudget: 8),
            RuntimeWorkflow: new AgentWorkflowRef(
                AgentDefaults.RuntimeWorkflowId,
                AgentDefaults.RuntimeWorkflowRevision)));
        var hash = SkillHash.Sha256(definition);
        var agent = await Agents.CreateAsync(
            tenant, slug, "研究助手", "D3 test", definition, hash, "admin-a", default);
        Assert.NotNull(agent);
        Assert.True(await Agents.MarkValidatedAsync(
            tenant, agent!.Id, agent.DraftVersion, definition, hash, default));
        var publish = await Agents.PublishAsync(
            tenant, agent.Id, agent.DraftVersion, definition, hash, "admin-a", default);
        Assert.Equal(AgentWriteStatus.Success, publish.Status);
        return agent;
    }

    private static string V2CheckpointRef(long generation)
        => $"v2:{generation}:{new string('c', 64)}:{Guid.NewGuid():D}";

    private static AgentRunEventAppend NewRunEvent(
        string eventType,
        string snapshotHash,
        object payload)
        => new(
            Guid.NewGuid(),
            eventType,
            "worker",
            snapshotHash,
            JsonSerializer.SerializeToElement(payload));

    private sealed record DispatchWindowRow(
        DateTime LastDispatchAt,
        DateTime ClaimExpiresAt);

    private sealed record DeadlineClockRow(DateTime DbNow, DateTime DeadlineAt);

    private sealed record CommandPreflightRow(
        long StateVersion,
        long LeaseGeneration,
        int DispatchAttempts,
        string? DispatchClaimOwner);

    private sealed record CanonicalBytesRow(byte[] Bytes, string Hash);
}
