using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Backend.Api.Agents;
using Backend.Api.Skills;

namespace Backend.Api.Tests;

public sealed class AgentRunsApiTests : IClassFixture<TestWebAppFactory>
{
    private const string SourceId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    private readonly TestWebAppFactory _factory;

    public AgentRunsApiTests(TestWebAppFactory factory) => _factory = factory;

    private HttpClient Admin(string tenant = "demo-a", string user = "admin-a")
        => _factory.CreateInternalClient().WithTenant(tenant).WithRole("ADMIN").WithUser(user);

    private HttpClient AdminWithCapabilities(params string[] capabilities)
    {
        var client = Admin();
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "X-User-Capabilities",
            string.Join(' ', capabilities));
        return client;
    }

    private static JsonObject AgentBody(string slug) => new()
    {
        ["slug"] = slug,
        ["name"] = "中文測試代理",
        ["description"] = "D3",
        ["system_prompt"] = "你是可靠的中文研究助手",
        ["execution_roles"] = new JsonArray("worker"),
        ["audience"] = new JsonArray("ADMIN"),
        ["allowed_tools"] = new JsonArray("local.calculator"),
        ["knowledge_sources"] = new JsonArray(SourceId),
        ["runtime_limits"] = new JsonObject
        {
            ["max_tool_rounds"] = 2,
            ["timeout_seconds"] = 60,
            ["step_budget"] = 8,
        },
        ["runtime_workflow"] = new JsonObject
        {
            ["id"] = AgentDefaults.RuntimeWorkflowId,
            ["revision"] = AgentDefaults.RuntimeWorkflowRevision,
        },
    };

    private async Task<string> PublishedAgentAsync(
        HttpClient client,
        string suffix,
        Action<JsonObject>? configure = null)
    {
        var body = AgentBody($"d3-{suffix}-{Guid.NewGuid():N}");
        configure?.Invoke(body);
        var create = await client.PostAsJsonAsync(
            "/api/agents", body);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var id = (await create.ReadJsonAsync())["id"]!.GetValue<string>();

        using var validate = new HttpRequestMessage(HttpMethod.Post, $"/api/agents/{id}/validate");
        validate.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(validate)).StatusCode);

        using var publish = new HttpRequestMessage(HttpMethod.Post, $"/api/agents/{id}/publish")
        {
            Content = JsonContent.Create(new { expected_draft_version = 1 }),
        };
        publish.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(publish)).StatusCode);
        return id;
    }

    private static HttpRequestMessage Start(string agentId, string key, string message = "請整理重點")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/agents/{agentId}/runs")
        {
            Content = JsonContent.Create(new { message }),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        return request;
    }

    private static JsonNode DecodeSnapshotEnvelope(JsonNode envelope)
    {
        var objectEnvelope = envelope.AsObject();
        Assert.Equal(
            new[] { "snapshot_canonical_base64", "snapshot_hash" },
            objectEnvelope.Select(pair => pair.Key).Order(StringComparer.Ordinal));

        var hash = objectEnvelope["snapshot_hash"]!.GetValue<string>();
        var encoded = objectEnvelope["snapshot_canonical_base64"]!.GetValue<string>();
        Assert.True(encoded.Length <= AgentExecutionContract.MaxSnapshotCanonicalBase64Length);
        var canonicalBytes = Convert.FromBase64String(encoded);
        Assert.True(canonicalBytes.Length <= AgentExecutionContract.MaxSnapshotCanonicalBytes);
        Assert.Equal(hash, SkillHash.Sha256(canonicalBytes));
        return JsonNode.Parse(Encoding.UTF8.GetString(canonicalBytes))!;
    }

    [Fact]
    public async Task Start_PinsPublishedSnapshot_AndUnicodeHashMatchesCanonicalArtifact()
    {
        var client = Admin();
        var agentId = await PublishedAgentAsync(client, "snapshot");

        var response = await client.SendAsync(Start(agentId, "start-snapshot"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var run = await response.ReadJsonAsync();
        Assert.Equal("queued", run["status"]!.GetValue<string>());
        Assert.Equal(
            response.Headers.GetValues("X-Agent-Run-Command-Id").Single(),
            run["command_id"]!.GetValue<string>());
        Assert.Equal(run["id"]!.GetValue<string>(), run["root_run_id"]!.GetValue<string>());
        Assert.Equal(1, run["agent_revision"]!.GetValue<int>());
        Assert.Equal(AgentDefaults.RuntimeWorkflowRevision, run["workflow_revision"]!.GetValue<int>());
        Assert.Equal(8, run["runtime_limits"]!["step_budget"]!.GetValue<int>());

        var artifactResponse = await client.GetAsync(
            $"/api/agent-runs/{run["id"]!.GetValue<string>()}/execution-artifact");
        Assert.Equal(HttpStatusCode.OK, artifactResponse.StatusCode);
        var envelope = await artifactResponse.ReadJsonAsync();
        Assert.Equal(
            run["snapshot_hash"]!.GetValue<string>(),
            envelope["snapshot_hash"]!.GetValue<string>());
        var artifact = DecodeSnapshotEnvelope(envelope);
        Assert.Equal(
            "你是可靠的中文研究助手",
            artifact["agent"]!["system_prompt"]!.GetValue<string>());
        // ADMIN has no implicit runtime grant.
        Assert.Empty(artifact["caller"]!["tool_grants"]!.AsArray());
        Assert.Empty(artifact["caller"]!["knowledge_source_grants"]!.AsArray());
        Assert.Equal(
            artifact["workflow"]!["definition_sha256"]!.GetValue<string>(),
            SkillHash.Sha256(CanonicalJson(artifact["workflow"]!["definition"]!)));
    }

    [Fact]
    public async Task Start_GroupAudienceUsesAuthenticatedGroupsAndSnapshotsCanonicalSet()
    {
        var builder = Admin();
        var agentId = await PublishedAgentAsync(
            builder,
            "group-audience",
            body => body["audience"] = new JsonArray("group:operations"));

        var denied = await Admin().SendAsync(Start(agentId, "group-denied"));
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);

        var allowed = Admin().WithGroups("zeta", "operations");
        var response = await allowed.SendAsync(Start(agentId, "group-allowed"));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var run = await response.ReadJsonAsync();
        var artifactResponse = await allowed.GetAsync(
            $"/api/agent-runs/{run["id"]!.GetValue<string>()}/execution-artifact");
        Assert.Equal(HttpStatusCode.OK, artifactResponse.StatusCode);
        var snapshot = DecodeSnapshotEnvelope(await artifactResponse.ReadJsonAsync());
        Assert.Equal(
            new[] { "operations", "zeta" },
            snapshot["caller"]!["groups"]!.AsArray()
                .Select(group => group!.GetValue<string>())
                .ToArray());
    }

    [Theory]
    [InlineData("*")]
    [InlineData("operations *")]
    [InlineData("Operations")]
    [InlineData("group:operations")]
    [InlineData("operations,finance")]
    public async Task Start_MalformedGroupHeaderRejectsWholeSet(string groups)
    {
        var client = Admin();
        var agentId = await PublishedAgentAsync(client, "malformed-group");
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "X-User-Groups",
            groups);

        var response = await client.SendAsync(
            Start(agentId, $"malformed-group-{Guid.NewGuid():N}"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Start_Idempotency_ReplaysSameRun_AndRejectsDifferentPayload()
    {
        var client = Admin();
        var agentId = await PublishedAgentAsync(client, "idem");

        var first = await client.SendAsync(Start(agentId, "same-key"));
        var second = await client.SendAsync(Start(agentId, "same-key"));
        var conflict = await client.SendAsync(Start(agentId, "same-key", "不同問題"));

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Equal("true", first.Headers.GetValues(
            "X-Agent-Run-Dispatch-Required").Single());
        Assert.Equal("false", first.Headers.GetValues(
            "X-Agent-Run-Replayed").Single());
        Assert.Equal("false", second.Headers.GetValues(
            "X-Agent-Run-Dispatch-Required").Single());
        Assert.Equal("true", second.Headers.GetValues(
            "X-Agent-Run-Replayed").Single());
        Assert.NotNull((await first.ReadJsonAsync())["command_id"]);
        Assert.Null((await second.ReadJsonAsync())["command_id"]);
        Assert.Equal(
            (await first.ReadJsonAsync())["id"]!.GetValue<string>(),
            (await second.ReadJsonAsync())["id"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task Start_ExactPersistedCapabilityClaims_IntersectPinnedAgentScope()
    {
        var agentId = await PublishedAgentAsync(Admin(), "capability-grants");
        var caller = AdminWithCapabilities(
            "workflow.manage",
            "tool.use:local.calculator",
            "tool.use:not-pinned",
            $"knowledge.read:{SourceId}",
            "knowledge.read:bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");

        var started = await caller.SendAsync(Start(agentId, "capability-start"));
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var run = await started.ReadJsonAsync();
        var envelope = await (await caller.GetAsync(
            $"/api/agent-runs/{run["id"]!.GetValue<string>()}/execution-artifact"))
            .ReadJsonAsync();
        var artifact = DecodeSnapshotEnvelope(envelope);

        Assert.Equal(
            new[] { "local.calculator" },
            artifact["caller"]!["tool_grants"]!.AsArray()
                .Select(item => item!.GetValue<string>()));
        Assert.Equal(
            new[] { SourceId },
            artifact["caller"]!["knowledge_source_grants"]!.AsArray()
                .Select(item => item!.GetValue<string>()));
    }

    [Theory]
    [InlineData("tool.use:*")]
    [InlineData("tool.use")]
    [InlineData("knowledge.read:*")]
    [InlineData("knowledge.read:AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA")]
    public async Task Start_RejectsMalformedOrWildcardRuntimeGrant(string claim)
    {
        var agentId = await PublishedAgentAsync(Admin(), "invalid-capability");
        var caller = AdminWithCapabilities(claim);

        var response = await caller.SendAsync(Start(
            agentId,
            "invalid-capability-" + Guid.NewGuid().ToString("N")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Start_EmptyRevisionGrants_FailClosed()
    {
        var agentId = await PublishedAgentAsync(
            Admin(),
            "empty-grants",
            body =>
            {
                body["allowed_tools"] = new JsonArray();
                body["knowledge_sources"] = new JsonArray();
            });
        // 呼叫端**帶著**兩個合法 claim:這樣「交集為空」的唯一成因就是 revision 側是空的,
        // 否則(用不帶 capability 的 client)這條測試會因為 caller 側為空而假綠。
        var client = AdminWithCapabilities(
            "tool.use:local.calculator",
            $"knowledge.read:{SourceId}");

        var started = await client.SendAsync(Start(agentId, "empty-grants-start"));
        var run = await started.ReadJsonAsync();
        var envelope = await (await client.GetAsync(
            $"/api/agent-runs/{run["id"]!.GetValue<string>()}/execution-artifact"))
            .ReadJsonAsync();
        var artifact = DecodeSnapshotEnvelope(envelope);

        Assert.Empty(artifact["caller"]!["tool_grants"]!.AsArray());
        Assert.Empty(artifact["caller"]!["knowledge_source_grants"]!.AsArray());
    }

    [Fact]
    public async Task DispatchAck_KeepsCommandInputInternal_AndStopsRecovery()
    {
        const string secretMessage = "private recovery input";
        var owner = Admin();
        var agentId = await PublishedAgentAsync(owner, "recovery");
        var start = await owner.SendAsync(Start(
            agentId,
            "recovery-start",
            secretMessage));

        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
        var publicBody = await start.Content.ReadAsStringAsync();
        Assert.DoesNotContain(secretMessage, publicBody, StringComparison.Ordinal);
        Assert.DoesNotContain("command_input", publicBody, StringComparison.Ordinal);
        Assert.DoesNotContain("claim_token", publicBody, StringComparison.Ordinal);
        var runId = JsonNode.Parse(publicBody)!["id"]!.GetValue<string>();
        var commandId = start.Headers.GetValues(
            "X-Agent-Run-Command-Id").Single();
        var initialClaim = start.Headers.GetValues(
            "X-Agent-Run-Dispatch-Claim").Single();

        var badAck = await owner.PostAsJsonAsync(
            $"/api/agent-runs/{runId}/commands/{commandId}/dispatch/complete",
            new { claim_token = "wrong-claim" });
        Assert.Equal(HttpStatusCode.Conflict, badAck.StatusCode);
        var ack = await owner.PostAsJsonAsync(
            $"/api/agent-runs/{runId}/commands/{commandId}/dispatch/complete",
            new { claim_token = initialClaim });
        Assert.Equal(HttpStatusCode.NoContent, ack.StatusCode);

        var internalClient = _factory.CreateInternalClient();
        var claims = await Task.WhenAll(
            internalClient.PostAsJsonAsync(
                "/api/agent-runs/recovery/claim",
                new { worker_id = "recovery-a", limit = 100, lease_seconds = 30 }),
            internalClient.PostAsJsonAsync(
                "/api/agent-runs/recovery/claim",
                new { worker_id = "recovery-b", limit = 100, lease_seconds = 30 }));
        Assert.All(claims, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        var items = new List<JsonNode>();
        foreach (var response in claims)
        {
            items.AddRange((await response.ReadJsonAsync())["items"]!.AsArray()
                .Where(item => item!["run_id"]!.GetValue<string>() == runId)
                .Select(item => item!));
        }

        Assert.Empty(items);
    }

    [Fact]
    public async Task RecoveryClaim_RequiresInternalToken_ButNotCallerIdentity()
    {
        var untrusted = _factory.CreateClient();
        var missingToken = await untrusted.PostAsJsonAsync(
            "/api/agent-runs/recovery/claim",
            new { worker_id = "workflow", limit = 1, lease_seconds = 30 });
        Assert.Equal(HttpStatusCode.Unauthorized, missingToken.StatusCode);

        var internalOnly = await _factory.CreateInternalClient().PostAsJsonAsync(
            "/api/agent-runs/recovery/claim",
            new { worker_id = "workflow", limit = 1, lease_seconds = 30 });
        Assert.Equal(HttpStatusCode.OK, internalOnly.StatusCode);
    }

    [Fact]
    public async Task Run_IsTenantAndOwnerScoped()
    {
        var owner = Admin();
        var agentId = await PublishedAgentAsync(owner, "owner");
        var start = await owner.SendAsync(Start(agentId, "owner-key"));
        var runId = (await start.ReadJsonAsync())["id"]!.GetValue<string>();

        Assert.Equal(HttpStatusCode.NotFound, (await Admin("demo-b", "admin-b")
            .GetAsync($"/api/runs/{runId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Admin("demo-a", "other-admin")
            .GetAsync($"/api/runs/{runId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Admin("demo-b", "admin-b")
            .GetAsync($"/api/agent-runs/{runId}/execution-artifact")).StatusCode);
    }

    [Fact]
    public async Task WaitingInput_Resume_ThenCancel_IsAtomic_AndTerminalImmutable()
    {
        var client = Admin();
        var agentId = await PublishedAgentAsync(client, "lifecycle");
        var start = await client.SendAsync(Start(agentId, "lifecycle-start"));
        var run = await start.ReadJsonAsync();
        var runId = run["id"]!.GetValue<string>();

        var withoutLease = await client.PostAsJsonAsync(
            $"/api/agent-runs/{runId}/transitions",
            new { expected_version = 1, to_status = "running" });
        Assert.Equal(HttpStatusCode.Conflict, withoutLease.StatusCode);

        var leaseResponse = await client.PostAsJsonAsync(
            $"/api/agent-runs/{runId}/lease",
            new { expected_version = 1, owner = "workflow-test", duration_seconds = 30 });
        Assert.Equal(HttpStatusCode.OK, leaseResponse.StatusCode);
        var lease = await leaseResponse.ReadJsonAsync();
        var leaseToken = lease["lease_token"]!.GetValue<string>();
        var leaseGeneration = lease["lease_generation"]!.GetValue<long>();
        var eventAckCursor = lease["event_ack_cursor"]!.GetValue<long>();

        var running = await client.PostAsJsonAsync(
            $"/api/agent-runs/{runId}/transitions",
            new
            {
                expected_version = 2,
                to_status = "running",
                lease_token = leaseToken,
                lease_generation = leaseGeneration,
                expected_event_ack_cursor = eventAckCursor,
            });
        Assert.Equal(HttpStatusCode.OK, running.StatusCode);

        // 非法 waiting transition 的完整矩陣(缺 checkpoint_ref / 版本未前進 / pending_input 過大 /
        // result 過大)由 InMemoryAgentRunRecoveryTests.WaitingInput_RequiresCheckpointIdentityAndStrictVersionAdvance
        // 直接打同一份生產碼驗證;這裡只留一個代表案,證明 InvalidState 在 HTTP 層映射成 409。
        var missingCheckpointRef = await client.PostAsJsonAsync(
            $"/api/agent-runs/{runId}/transitions",
            new
            {
                expected_version = 3,
                to_status = "waiting_input",
                lease_token = leaseToken,
                lease_generation = leaseGeneration,
                expected_event_ack_cursor = eventAckCursor,
                checkpoint_version = 1,
            });
        Assert.Equal(HttpStatusCode.Conflict, missingCheckpointRef.StatusCode);

        var pending = JsonDocument.Parse("""{"question":"需要哪個期間？"}""").RootElement.Clone();
        var waiting = await client.PostAsJsonAsync(
            $"/api/agent-runs/{runId}/transitions",
            new
            {
                expected_version = 3,
                to_status = "waiting_input",
                lease_token = leaseToken,
                lease_generation = leaseGeneration,
                expected_event_ack_cursor = eventAckCursor,
                checkpoint_ref = V2CheckpointRef(leaseGeneration),
                checkpoint_version = 1,
                pending_input = pending,
            });
        Assert.Equal(HttpStatusCode.OK, waiting.StatusCode);

        using var resume = new HttpRequestMessage(HttpMethod.Post, $"/api/runs/{runId}/resume")
        {
            Content = JsonContent.Create(new
            {
                message = "最近一季",
                expected_checkpoint_version = 1,
            }),
        };
        resume.Headers.TryAddWithoutValidation("Idempotency-Key", "resume-1");
        var resumed = await client.SendAsync(resume);
        Assert.Equal(HttpStatusCode.Accepted, resumed.StatusCode);
        var resumedBody = await resumed.ReadJsonAsync();
        Assert.Equal("queued", resumedBody["status"]!.GetValue<string>());
        Assert.NotNull(resumedBody["command_id"]);

        using var cancel = new HttpRequestMessage(HttpMethod.Post, $"/api/runs/{runId}/cancel")
        {
            Content = JsonContent.Create(new { reason = "停止測試" }),
        };
        cancel.Headers.TryAddWithoutValidation("Idempotency-Key", "cancel-1");
        var cancelled = await client.SendAsync(cancel);
        Assert.Equal(HttpStatusCode.Accepted, cancelled.StatusCode);
        var cancelRequested = await cancelled.ReadJsonAsync();
        Assert.Equal("queued", cancelRequested["status"]!.GetValue<string>());
        Assert.True(cancelRequested["cancel_requested"]!.GetValue<bool>());
        Assert.NotNull(cancelRequested["command_id"]);

        var cancelLease = await (await client.PostAsJsonAsync(
            $"/api/agent-runs/{runId}/lease",
            new
            {
                expected_version = cancelRequested["state_version"]!.GetValue<long>(),
                owner = "workflow-test",
                duration_seconds = 30,
            })).ReadJsonAsync();
        var cancelToken = cancelLease["lease_token"]!.GetValue<string>();
        var cancelGeneration = cancelLease["lease_generation"]!.GetValue<long>();
        var cancelStateVersion =
            cancelLease["run"]!["state_version"]!.GetValue<long>();
        var snapshotHash = cancelLease["run"]!["snapshot_hash"]!.GetValue<string>();
        var cancelEvent = await client.PostAsJsonAsync(
            $"/api/agent-runs/{runId}/events",
            new
            {
                expected_version = cancelStateVersion,
                lease_token = cancelToken,
                lease_generation = cancelGeneration,
                event_cursor_start = 0,
                events = new[]
                {
                    new
                    {
                        event_id = Guid.NewGuid(),
                        event_type = "run_cancelled",
                        node_id = "supervisor",
                        snapshot_hash = snapshotHash,
                        payload = new { status = "cancelled" },
                    },
                },
            });
        Assert.Equal(HttpStatusCode.OK, cancelEvent.StatusCode);
        var terminal = await (await client.PostAsJsonAsync(
            $"/api/agent-runs/{runId}/transitions",
            new
            {
                expected_version = cancelStateVersion,
                to_status = "cancelled",
                lease_token = cancelToken,
                lease_generation = cancelGeneration,
                expected_event_ack_cursor = 1,
                checkpoint_ref = V2CheckpointRef(cancelGeneration),
                checkpoint_version = 2,
            })).ReadJsonAsync();
        Assert.Equal("cancelled", terminal["status"]!.GetValue<string>());
        var terminalEvents = await (await client.GetAsync(
            $"/api/runs/{runId}/events?after_sequence=0&limit=100")).ReadJsonAsync();
        Assert.Single(
            terminalEvents["events"]!.AsArray(),
            item => item!["event_type"]!.GetValue<string>() == "run_cancelled");

        var illegal = await client.PostAsJsonAsync(
            $"/api/agent-runs/{runId}/transitions",
            new
            {
                expected_version = terminal["state_version"]!.GetValue<long>(),
                to_status = "running",
            });
        Assert.Equal(HttpStatusCode.Conflict, illegal.StatusCode);
    }

    [Fact]
    public async Task Events_AreCursorOrdered_Idempotent_AndRejectSensitivePayload()
    {
        var client = Admin();
        var agentId = await PublishedAgentAsync(client, "events");
        var start = await client.SendAsync(Start(agentId, "events-start"));
        var run = await start.ReadJsonAsync();
        var runId = run["id"]!.GetValue<string>();
        var snapshotHash = run["snapshot_hash"]!.GetValue<string>();
        var lease = await (await client.PostAsJsonAsync(
            $"/api/agent-runs/{runId}/lease",
            new { expected_version = 1, owner = "event-worker", duration_seconds = 30 }))
            .ReadJsonAsync();
        var leaseToken = lease["lease_token"]!.GetValue<string>();
        var leaseGeneration = lease["lease_generation"]!.GetValue<long>();
        var eventId = Guid.NewGuid();
        var body = new
        {
            expected_version = 2,
            lease_token = leaseToken,
            lease_generation = leaseGeneration,
            event_cursor_start = 0,
            events = new[]
            {
                new
                {
                    event_id = eventId,
                    event_type = "model_step",
                    node_id = "model",
                    snapshot_hash = snapshotHash,
                    payload = new { status = "ok", step = 1 },
                },
            },
        };

        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsJsonAsync($"/api/agent-runs/{runId}/events", body)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsJsonAsync($"/api/agent-runs/{runId}/events", body)).StatusCode);
        var divergentReplay = await client.PostAsJsonAsync(
            $"/api/agent-runs/{runId}/events",
            new
            {
                expected_version = 2,
                lease_token = leaseToken,
                lease_generation = leaseGeneration,
                event_cursor_start = 0,
                events = new[]
                {
                    new
                    {
                        event_id = eventId,
                        event_type = "model_step",
                        node_id = "model",
                        snapshot_hash = snapshotHash,
                        payload = new { status = "different", step = 1 },
                    },
                },
            });
        Assert.Equal(HttpStatusCode.Conflict, divergentReplay.StatusCode);

        var events = await (await client.GetAsync(
            $"/api/runs/{runId}/events?after_sequence=1&limit=10")).ReadJsonAsync();
        Assert.Single(events["events"]!.AsArray());
        Assert.Equal(2, events["next_sequence"]!.GetValue<long>());

        // 敏感 payload 規則:除了 payload 之外一切合法(已知 event_type、有 node_id、cursor 連續),
        // 唯一拒因就是 payload 帶 prompt。訊息也一併比對,避免將來被別的規則「順便」擋掉而假綠。
        object SensitiveBatch(object payload) => new
        {
            expected_version = 2,
            lease_token = leaseToken,
            lease_generation = leaseGeneration,
            event_cursor_start = 1,
            events = new[]
            {
                new
                {
                    event_id = Guid.NewGuid(),
                    event_type = "model_step",
                    node_id = "model",
                    snapshot_hash = snapshotHash,
                    payload,
                },
            },
        };
        var rejected = await client.PostAsJsonAsync(
            $"/api/agent-runs/{runId}/events", SensitiveBatch(new { prompt = "secret" }));
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Equal(
            "event payload 含敏感欄位或超過上限",
            (await rejected.ReadJsonAsync())["message"]!.GetValue<string>());

        // 對照組:同一批次換成不含敏感欄位的 payload 就會被接受 —— 證明拒因確實是 payload。
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PostAsJsonAsync(
                $"/api/agent-runs/{runId}/events", SensitiveBatch(new { status = "ok" }))).StatusCode);
    }

    // GET /api/runs/{id}/events 的游標守門(AgentRunController:50-55)。off-point 進 400,
    // on-point(limit=200)必須放行 —— 用 "abc" 等安全內部值測不出把 200 寫成 100 這種錯。
    [Theory]
    [InlineData("after_sequence=-1&limit=10", HttpStatusCode.BadRequest)]
    [InlineData("after_sequence=0&limit=0", HttpStatusCode.BadRequest)]
    [InlineData("after_sequence=0&limit=201", HttpStatusCode.BadRequest)]
    [InlineData("after_sequence=0&limit=1", HttpStatusCode.OK)]
    [InlineData("after_sequence=0&limit=200", HttpStatusCode.OK)]
    public async Task Events_CursorQueryBoundaries(string query, HttpStatusCode expected)
    {
        var client = Admin();
        var agentId = await PublishedAgentAsync(client, "events-range");
        var run = await (await client.SendAsync(Start(agentId, $"events-range-{Guid.NewGuid():N}")))
            .ReadJsonAsync();
        var runId = run["id"]!.GetValue<string>();

        var response = await client.GetAsync($"/api/runs/{runId}/events?{query}");

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Events_AfterSequenceBeyondTail_ReturnsEmptyWithoutRewindingCursor()
    {
        var client = Admin();
        var agentId = await PublishedAgentAsync(client, "events-tail");
        var run = await (await client.SendAsync(Start(agentId, "events-tail-start"))).ReadJsonAsync();
        var runId = run["id"]!.GetValue<string>();

        var head = await (await client.GetAsync(
            $"/api/runs/{runId}/events?after_sequence=0&limit=100")).ReadJsonAsync();
        var tail = head["next_sequence"]!.GetValue<long>();

        var beyond = await (await client.GetAsync(
            $"/api/runs/{runId}/events?after_sequence={tail + 1_000}&limit=100")).ReadJsonAsync();

        Assert.Empty(beyond["events"]!.AsArray());
        // 空頁不得把游標倒退回去,否則輪詢的客戶端會無限重讀舊事件。
        Assert.True(beyond["next_sequence"]!.GetValue<long>() >= tail);
    }

    // /api/agent-runs/recovery/claim 的輸入守門(AgentRunRecoveryController:25-27)。
    // 這支路由不帶呼叫者身分,參數就是唯一的濫用面 —— limit=1000 會一次抽乾整個佇列。
    [Theory]
    [InlineData(0, 30, "worker")]
    [InlineData(101, 30, "worker")]
    [InlineData(20, 4, "worker")]
    [InlineData(20, 301, "worker")]
    [InlineData(20, 30, "")]
    public async Task RecoveryClaim_RejectsOutOfRangeLimitLeaseOrWorker(
        int limit, int leaseSeconds, string workerId)
    {
        var response = await _factory.CreateInternalClient().PostAsJsonAsync(
            "/api/agent-runs/recovery/claim",
            new { worker_id = workerId, limit, lease_seconds = leaseSeconds });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(100, 300)]
    public async Task RecoveryClaim_AcceptsInclusiveBoundaries(int limit, int leaseSeconds)
    {
        var response = await _factory.CreateInternalClient().PostAsJsonAsync(
            "/api/agent-runs/recovery/claim",
            new { worker_id = new string('w', 200), limit, lease_seconds = leaseSeconds });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task RunningCancelRequest_DominatesLaterWorkerCompletion()
    {
        var client = Admin();
        var agentId = await PublishedAgentAsync(client, "cancel-dominance");
        var run = await (await client.SendAsync(Start(agentId, "cancel-dominance-start")))
            .ReadJsonAsync();
        var runId = run["id"]!.GetValue<string>();

        var lease = await (await client.PostAsJsonAsync(
            $"/api/agent-runs/{runId}/lease",
            new { expected_version = 1, owner = "worker-a", duration_seconds = 30 }))
            .ReadJsonAsync();
        var leaseToken = lease["lease_token"]!.GetValue<string>();
        var leaseGeneration = lease["lease_generation"]!.GetValue<long>();
        _ = await client.PostAsJsonAsync(
            $"/api/agent-runs/{runId}/transitions",
            new
            {
                expected_version = 2,
                to_status = "running",
                lease_token = leaseToken,
                lease_generation = leaseGeneration,
                expected_event_ack_cursor = 0,
            });

        using var cancel = new HttpRequestMessage(HttpMethod.Post, $"/api/runs/{runId}/cancel")
        {
            Content = JsonContent.Create(new { reason = "stop" }),
        };
        cancel.Headers.TryAddWithoutValidation("Idempotency-Key", "cancel-dominance");
        var cancelResponse = await client.SendAsync(cancel);
        Assert.Equal(HttpStatusCode.Accepted, cancelResponse.StatusCode);
        var cancelRequested = await cancelResponse.ReadJsonAsync();
        Assert.Equal("running", cancelRequested["status"]!.GetValue<string>());
        Assert.True(cancelRequested["cancel_requested"]!.GetValue<bool>());
        var expectedVersion = cancelRequested["state_version"]!.GetValue<long>();
        var snapshotHash = run["snapshot_hash"]!.GetValue<string>();

        static object AuditEvent(string snapshotHash, string eventType) => new
        {
            event_id = Guid.NewGuid(),
            event_type = eventType,
            node_id = "supervisor",
            snapshot_hash = snapshotHash,
            payload = new { status = "ok" },
        };
        var noLeaseAudit = await client.PostAsJsonAsync(
            $"/api/agent-runs/{runId}/events",
            new
            {
                expected_version = expectedVersion,
                lease_generation = leaseGeneration,
                event_cursor_start = 0,
                events = new[] { AuditEvent(snapshotHash, "run_preflight") },
            });
        Assert.Equal(HttpStatusCode.Conflict, noLeaseAudit.StatusCode);
        var staleLeaseAudit = await client.PostAsJsonAsync(
            $"/api/agent-runs/{runId}/events",
            new
            {
                expected_version = expectedVersion,
                lease_token = "stale-token",
                lease_generation = leaseGeneration,
                event_cursor_start = 0,
                events = new[] { AuditEvent(snapshotHash, "model_step") },
            });
        Assert.Equal(HttpStatusCode.Conflict, staleLeaseAudit.StatusCode);
        var unsafeAudit = await client.PostAsJsonAsync(
            $"/api/agent-runs/{runId}/events",
            new
            {
                expected_version = expectedVersion,
                lease_token = leaseToken,
                lease_generation = leaseGeneration,
                event_cursor_start = 0,
                events = new[]
                {
                    AuditEvent(snapshotHash, "business_action_completed"),
                },
            });
        Assert.Equal(HttpStatusCode.Conflict, unsafeAudit.StatusCode);
        var safeAudit = await client.PostAsJsonAsync(
            $"/api/agent-runs/{runId}/events",
            new
            {
                expected_version = expectedVersion,
                lease_token = leaseToken,
                lease_generation = leaseGeneration,
                event_cursor_start = 0,
                events = new[]
                {
                    AuditEvent(snapshotHash, "run_preflight"),
                    AuditEvent(snapshotHash, "tool_completed"),
                    AuditEvent(snapshotHash, "run_cancelled"),
                },
            });
        Assert.Equal(HttpStatusCode.OK, safeAudit.StatusCode);

        var completion = await client.PostAsJsonAsync(
            $"/api/agent-runs/{runId}/transitions",
            new
            {
                expected_version = expectedVersion,
                to_status = "completed",
                lease_token = leaseToken,
                lease_generation = leaseGeneration,
                expected_event_ack_cursor = 3,
                result = new { status = "too-late" },
            });
        Assert.Equal(HttpStatusCode.Conflict, completion.StatusCode);

        var terminal = await client.PostAsJsonAsync(
            $"/api/agent-runs/{runId}/transitions",
            new
            {
                expected_version = expectedVersion,
                to_status = "cancelled",
                lease_token = leaseToken,
                lease_generation = leaseGeneration,
                expected_event_ack_cursor = 3,
                checkpoint_ref = V2CheckpointRef(leaseGeneration),
                checkpoint_version = 1,
            });
        Assert.Equal(HttpStatusCode.OK, terminal.StatusCode);
        Assert.Equal(
            "cancelled",
            (await terminal.ReadJsonAsync())["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Start_RejectsUnpublishedOrAudienceEmptyAgent()
    {
        var client = Admin();
        var create = await client.PostAsJsonAsync(
            "/api/agents", AgentBody($"d3-unpublished-{Guid.NewGuid():N}"));
        var id = (await create.ReadJsonAsync())["id"]!.GetValue<string>();

        Assert.Equal(
            HttpStatusCode.Conflict,
            (await client.SendAsync(Start(id, "unpublished-key"))).StatusCode);

        var noAudience = AgentBody($"d3-noaud-{Guid.NewGuid():N}");
        noAudience["audience"] = new JsonArray();
        var created = await client.PostAsJsonAsync("/api/agents", noAudience);
        var noAudienceId = (await created.ReadJsonAsync())["id"]!.GetValue<string>();
        using var validate = new HttpRequestMessage(
            HttpMethod.Post, $"/api/agents/{noAudienceId}/validate");
        validate.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        _ = await client.SendAsync(validate);
        using var publish = new HttpRequestMessage(
            HttpMethod.Post, $"/api/agents/{noAudienceId}/publish")
        {
            Content = JsonContent.Create(new { expected_draft_version = 1 }),
        };
        publish.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(publish)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await client.SendAsync(Start(noAudienceId, "no-aud-key"))).StatusCode);
    }

    private static string CanonicalJson(JsonNode node)
    {
        var canonical = Canonicalize(node);
        return canonical.ToJsonString(new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = false,
        });
    }

    private static string V2CheckpointRef(long generation)
        => $"v2:{generation}:{new string('a', 64)}:{Guid.NewGuid():D}";

    private static JsonNode Canonicalize(JsonNode node) => node switch
    {
        JsonObject obj => new JsonObject(
            obj.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => KeyValuePair.Create(
                    p.Key,
                    p.Value is null ? null : Canonicalize(p.Value)))),
        JsonArray array => new JsonArray(
            array.Select(item => item is null ? null : Canonicalize(item)).ToArray()),
        _ => node.DeepClone(),
    };
}
