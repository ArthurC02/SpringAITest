using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Service;
using Platform.Service.Dtos;
using Platform.Service.Exceptions;
using Platform.Service.Options;

namespace Platform.Service.Tests;

public sealed class AgentChatRuntimeTests
{
    private static readonly UserContext User = new("alice", "tenant-a", "USER");
    private static readonly Guid Orchestrator = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Run = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Command = Guid.Parse("33333333-3333-3333-3333-333333333333");

    // ---- canary 選擇閘:Enabled 與「租戶在 allowlist」是兩個獨立條件,各自單獨可否決 ----
    // 每一案只讓一個條件不成立(另一個成立),否則 IsCanaryTenant 掉了其中一半也不會有測試變紅。

    [Fact]
    public async Task DisabledFlag_AllowlistedTenant_UsesLegacyWithoutCallingBackend()
    {
        var handler = new QueueHandler();
        var result = await Build(handler, DisabledButAllowlisted())
            .RunAsync("hello", "c1", null, Identity());
        Assert.Null(result);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task DisabledFlag_AllowlistedTenant_ExplicitSelection_FailsClosed()
    {
        var handler = new QueueHandler();
        await Assert.ThrowsAsync<WorkflowNotFoundException>(
            () => Build(handler, DisabledButAllowlisted())
                .RunAsync("hello", "c1", Orchestrator, Identity()));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task EnabledButTenantNotAllowlisted_UsesLegacy_WithoutCallingBackend()
    {
        var handler = new QueueHandler();
        var result = await Build(handler, EnabledFor("tenant-other"))
            .RunAsync("hello", "c1", null, Identity());
        Assert.Null(result);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task EnabledButTenantNotAllowlisted_ExplicitSelection_FailsClosed()
    {
        var handler = new QueueHandler();
        await Assert.ThrowsAsync<WorkflowNotFoundException>(
            () => Build(handler, EnabledFor("tenant-other"))
                .RunAsync("hello", "c1", Orchestrator, Identity()));
        Assert.Empty(handler.Requests);
    }

    // allowlist 比對是 StringComparer.Ordinal:只差大小寫的租戶不得被放進 canary。
    [Fact]
    public async Task TenantAllowlist_IsCaseSensitive_DifferentCaseStaysLegacy()
    {
        var handler = new QueueHandler();
        var result = await Build(handler, EnabledFor("TENANT-A"))
            .RunAsync("hello", "c1", null, Identity());
        Assert.Null(result);
        Assert.Empty(handler.Requests);
    }

    // 匿名沒有可授權的身分:即使旗標開著、目標租戶在 allowlist,也一律留在 legacy;明確指定則 fail-closed。
    [Fact]
    public async Task Anonymous_EnabledAllowlistedTenant_UsesLegacy_WithoutCallingBackend()
    {
        var handler = new QueueHandler();
        var result = await Build(handler, Enabled())
            .RunAsync("hello", "c1", null, AnonymousIdentity());
        Assert.Null(result);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Anonymous_ExplicitSelection_FailsClosed()
    {
        var handler = new QueueHandler();
        await Assert.ThrowsAsync<WorkflowNotFoundException>(
            () => Build(handler, Enabled())
                .RunAsync("hello", "c1", Orchestrator, AnonymousIdentity()));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ResolverLegacy_UsesLegacyAndDoesNotAllocate()
    {
        var handler = new QueueHandler(
            Json(HttpStatusCode.NotFound, """{"message":"none"}"""),
            Json(HttpStatusCode.OK, """{"mode":"legacy"}"""));
        var result = await Build(handler, Enabled()).RunAsync("hello", "c1", null, Identity());
        Assert.Null(result);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("/api/runtime-discovery/resolve", handler.Requests[1].Path);
    }

    [Fact]
    public async Task CanaryDefault_AllocatesKicksPollsAndRendersAggregateAnswer()
    {
        var backend = new QueueHandler(
            Json(HttpStatusCode.NotFound, """{"message":"none"}"""),
            Json(HttpStatusCode.OK, $"{{\"mode\":\"orchestrator\",\"orchestrator\":{{\"id\":\"{Orchestrator}\"}}}}"),
            Json(HttpStatusCode.Accepted, Accepted()),
            Json(HttpStatusCode.OK, $"{{\"mode\":\"orchestrator\",\"run\":{{\"id\":\"{Run}\",\"status\":\"running\"}}}}"),
            Json(HttpStatusCode.OK, $"{{\"mode\":\"orchestrator\",\"run\":{{\"id\":\"{Run}\",\"status\":\"completed\",\"result\":{{\"aggregate\":{{\"answer\":\"done\"}}}}}}}}"));
        var workflow = new QueueHandler(Json(HttpStatusCode.Accepted, "{}"));
        var identity = Identity();

        var result = await Build(backend, Enabled(), workflow)
            .RunAsync("hello", "c1", null, identity);

        Assert.Equal("done", result!.Text);
        Assert.Equal(5, backend.Requests.Count);
        Assert.Equal("/api/chat-runs", backend.Requests[2].Path);
        Assert.False(string.IsNullOrWhiteSpace(backend.Requests[2].IdempotencyKey));
        Assert.Contains("\"conversation_id\":\"c1\"", backend.Requests[2].Body);
        Assert.Contains(Orchestrator.ToString(), backend.Requests[2].Body, StringComparison.OrdinalIgnoreCase);
        Assert.Single(workflow.Requests);
        Assert.Equal($"/orchestrator-runs/{Run:D}/dispatch", workflow.Requests[0].Path);
        Assert.Contains(Command.ToString(), workflow.Requests[0].Body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Run, identity.TurnMetadata!.RootRunId);
        Assert.Equal(Orchestrator, identity.TurnMetadata.OrchestratorId);
    }

    // 明確指定 Orchestrator 卻被 resolver 判成 legacy:必須 409 conflict,絕不靜默降級成 legacy 聊天。
    [Fact]
    public async Task ExplicitOrchestrator_ResolverReturnsLegacy_Conflicts_WithoutAllocating()
    {
        var backend = new QueueHandler(
            Json(HttpStatusCode.NotFound, """{"message":"none"}"""),
            Json(HttpStatusCode.OK, """{"mode":"legacy"}"""));

        await Assert.ThrowsAsync<DownstreamConflictException>(
            () => Build(backend, Enabled()).RunAsync("hello", "c1", Orchestrator, Identity()));

        Assert.Equal(
            ["/api/chat-runs/active", "/api/runtime-discovery/resolve"],
            backend.Requests.Select(x => x.Path));
    }

    // 既有 active root 正在跑(running/queued)且選擇相同:只輪詢該 run,不 resume、不 allocate。
    [Fact]
    public async Task ActiveRunningRoot_PollsExistingRun_WithoutResumeOrAllocate()
    {
        var backend = new QueueHandler(
            Json(HttpStatusCode.OK, Active(Orchestrator, "running", "{}")),
            Json(HttpStatusCode.OK, Active(Orchestrator, "completed", """{"aggregate":{"answer":"done"}}""")));

        var result = await Build(backend, Enabled()).RunAsync("next turn", "c1", null, Identity());

        Assert.Equal("done", result!.Text);
        Assert.Equal(
            ["/api/chat-runs/active", $"/api/chat-runs/{Run:D}"],
            backend.Requests.Select(x => x.Path));
    }

    [Fact]
    public async Task ExplicitResolver404_DoesNotFallbackOrAllocate()
    {
        var backend = new QueueHandler(
            Json(HttpStatusCode.NotFound, """{"message":"none"}"""),
            Json(HttpStatusCode.NotFound, """{"message":"not found"}"""));
        await Assert.ThrowsAsync<WorkflowNotFoundException>(
            () => Build(backend, Enabled()).RunAsync("hello", "c1", Orchestrator, Identity()));
        Assert.Equal(2, backend.Requests.Count);
    }

    [Fact]
    public async Task AmbiguousActiveRoot_ConflictsAndNeverAllocatesAnotherRun()
    {
        var backend = new QueueHandler(
            Json(HttpStatusCode.Conflict, """{"message":"Multiple active chat root runs require operator intervention"}"""));

        await Assert.ThrowsAsync<DownstreamConflictException>(
            () => Build(backend, Enabled()).RunAsync("hello", "c1", null, Identity()));

        var request = Assert.Single(backend.Requests);
        Assert.Equal("/api/chat-runs/active", request.Path);
    }

    [Fact]
    public async Task WorkflowKickFailure_IsRecoverable()
    {
        var backend = new QueueHandler(
            Json(HttpStatusCode.NotFound, """{"message":"none"}"""),
            Json(HttpStatusCode.OK, $"{{\"mode\":\"orchestrator\",\"orchestrator\":{{\"id\":\"{Orchestrator}\"}}}}"),
            Json(HttpStatusCode.Accepted, Accepted()),
            Json(HttpStatusCode.OK, """{"run":{"status":"completed","result":{"aggregate":{"answer":"recovered"}}}}"""));
        var workflow = new QueueHandler(Json(HttpStatusCode.ServiceUnavailable, "{}"));
        var result = await Build(backend, Enabled(), workflow)
            .RunAsync("hello", "c1", null, Identity());
        Assert.Equal("recovered", result!.Text);
    }

    [Fact]
    public async Task WaitingRoot_ReturnsQuestion_ThenNextTurnResumesSameRun()
    {
        var waiting = Active(
            Orchestrator,
            "waiting_input",
            """{"clarification":["Which region?"]}""");
        var backend = new QueueHandler(
            Json(HttpStatusCode.NotFound, """{"message":"none"}"""),
            Json(HttpStatusCode.OK, $"{{\"mode\":\"orchestrator\",\"orchestrator\":{{\"id\":\"{Orchestrator}\"}}}}"),
            Json(HttpStatusCode.Accepted, Accepted()),
            Json(HttpStatusCode.OK, waiting),
            Json(HttpStatusCode.OK, waiting),
            Json(HttpStatusCode.Accepted, Accepted()),
            Json(HttpStatusCode.OK,
                $"{{\"run\":{{\"status\":\"completed\",\"result\":{{\"aggregate\":{{\"answer\":\"resumed\"}}}}}}}}"));
        var workflow = new QueueHandler(Json(HttpStatusCode.Accepted, "{}"));
        var runtime = Build(backend, Enabled(), workflow);

        var question = await runtime.RunAsync("initial", "c1", null, Identity());
        var answer = await runtime.RunAsync("Taiwan", "c1", null, Identity());

        Assert.Equal("Which region?", question!.Text);
        Assert.Equal("resumed", answer!.Text);
        Assert.Equal($"/api/chat-runs/{Run:D}/resume", backend.Requests[5].Path);
        Assert.Contains("\"input\":\"Taiwan\"", backend.Requests[5].Body);
        Assert.Equal(2, workflow.Requests.Count);
    }

    [Fact]
    public async Task SwitchingExplicitOrchestrator_CancelsActiveBeforeNewAllocation()
    {
        var other = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var backend = new QueueHandler(
            Json(HttpStatusCode.OK, Active(Orchestrator, "waiting_input", """{"clarification":["q"]}""")),
            Json(HttpStatusCode.Accepted, "{}"),
            Json(HttpStatusCode.OK, $"{{\"mode\":\"orchestrator\",\"orchestrator\":{{\"id\":\"{other}\"}}}}"),
            Json(HttpStatusCode.Accepted, Accepted(other)),
            Json(HttpStatusCode.OK,
                """{"run":{"status":"completed","result":{"aggregate":{"answer":"switched"}}}}"""));
        var workflow = new QueueHandler(Json(HttpStatusCode.Accepted, "{}"));

        var answer = await Build(backend, Enabled(), workflow)
            .RunAsync("new task", "c1", other, Identity());

        Assert.Equal("switched", answer!.Text);
        Assert.Equal($"/api/orchestrator-runs/{Run:D}/cancel", backend.Requests[1].Path);
        Assert.Equal("/api/runtime-discovery/resolve", backend.Requests[2].Path);
        Assert.Equal("/api/chat-runs", backend.Requests[3].Path);
    }

    [Fact]
    public async Task LongCallerAndConversationIds_UseBoundedOpaqueAllocationAndResumeKeys()
    {
        var tenant = new string('t', 1_024);
        var user = new UserContext(new string('u', 1_024), tenant, "USER");
        var conversation = new string('c', 1_024);
        var waiting = Active(Orchestrator, "waiting_input", """{"clarification":["Which region?"]}""");
        var backend = new QueueHandler(
            Json(HttpStatusCode.NotFound, """{"message":"none"}"""),
            Json(HttpStatusCode.OK, $"{{\"mode\":\"orchestrator\",\"orchestrator\":{{\"id\":\"{Orchestrator}\"}}}}"),
            Json(HttpStatusCode.Accepted, Accepted()),
            Json(HttpStatusCode.OK, waiting),
            Json(HttpStatusCode.OK, waiting),
            Json(HttpStatusCode.Accepted, Accepted()),
            Json(HttpStatusCode.OK, """{"run":{"status":"completed","result":{"aggregate":{"answer":"resumed"}}}}"""));
        var workflow = new QueueHandler(Json(HttpStatusCode.Accepted, "{}"));
        var runtime = Build(backend, EnabledFor(tenant), workflow);

        await runtime.RunAsync("initial", conversation, null, Identity(user, conversation));
        await runtime.RunAsync("Taiwan", conversation, null, Identity(user, conversation));

        AssertOpaqueKey(backend.Requests[2].IdempotencyKey, "chat", tenant, user.UserId, conversation);
        AssertOpaqueKey(backend.Requests[5].IdempotencyKey, "resume", tenant, user.UserId, conversation);
    }

    [Fact]
    public async Task LongCallerAndConversationIds_UseBoundedOpaqueSwitchKey()
    {
        var tenant = new string('t', 1_024);
        var user = new UserContext(new string('u', 1_024), tenant, "USER");
        var conversation = new string('c', 1_024);
        var other = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var backend = new QueueHandler(
            Json(HttpStatusCode.OK, Active(Orchestrator, "waiting_input", """{"clarification":["q"]}""")),
            Json(HttpStatusCode.Accepted, "{}"),
            Json(HttpStatusCode.OK, $"{{\"mode\":\"orchestrator\",\"orchestrator\":{{\"id\":\"{other}\"}}}}"),
            Json(HttpStatusCode.Accepted, Accepted(other)),
            Json(HttpStatusCode.OK, """{"run":{"status":"completed","result":{"aggregate":{"answer":"switched"}}}}"""));
        var workflow = new QueueHandler(Json(HttpStatusCode.Accepted, "{}"));

        await Build(backend, EnabledFor(tenant), workflow)
            .RunAsync("new task", conversation, other, Identity(user, conversation));

        AssertOpaqueKey(backend.Requests[1].IdempotencyKey, "switch", tenant, user.UserId, conversation);
    }

    [Fact]
    public async Task LogicalAttemptId_MakesRetriesStableWithoutReplayingIdenticalMessagesForever()
    {
        var tenant = new string('t', 1_024);
        var user = new UserContext(new string('u', 1_024), tenant, "USER");
        var conversation = new string('c', 1_024);

        var first = await AllocateKeyAsync("attempt-1");
        var retry = await AllocateKeyAsync("attempt-1");
        var distinctAttempt = await AllocateKeyAsync("attempt-2");

        Assert.Equal(first, retry);
        Assert.NotEqual(first, distinctAttempt);
        AssertOpaqueKey(first, "chat", tenant, user.UserId, conversation);

        async Task<string> AllocateKeyAsync(string attempt)
        {
            var backend = new QueueHandler(
                Json(HttpStatusCode.NotFound, """{"message":"none"}"""),
                Json(HttpStatusCode.NotFound, """{"message":"none"}"""),
                Json(HttpStatusCode.NotFound, """{"message":"none"}"""),
                Json(HttpStatusCode.OK, $"{{\"mode\":\"orchestrator\",\"orchestrator\":{{\"id\":\"{Orchestrator}\"}}}}"),
                Json(HttpStatusCode.Accepted, Accepted()),
                Json(HttpStatusCode.OK, """{"run":{"status":"completed","result":{"aggregate":{"answer":"done"}}}}"""));
            await Build(backend, EnabledFor(tenant), new QueueHandler(Json(HttpStatusCode.Accepted, "{}")))
                .RunAsync("same user-visible message", conversation, null,
                    Identity(user, conversation), attempt);
            return backend.Requests[4].IdempotencyKey!;
        }
    }

    [Fact]
    public async Task LogicalAttemptRetry_ReplaysTerminalRunWithoutAllocatingOrResuming()
    {
        var completed = Active(Orchestrator, "completed", """{"aggregate":{"answer":"replayed"}}""");
        var backend = new QueueHandler(
            Json(HttpStatusCode.OK, completed),
            Json(HttpStatusCode.OK, completed));
        var workflow = new QueueHandler();

        var result = await Build(backend, Enabled(), workflow).RunAsync(
            "same turn", "c1", null, Identity(), "retry-token");

        Assert.Equal("replayed", result!.Text);
        Assert.Equal(["/api/chat-runs/replay", $"/api/chat-runs/{Run:D}"], backend.Requests.Select(x => x.Path));
        Assert.Empty(workflow.Requests);
    }

    [Fact]
    public async Task LogicalAttemptRetry_ReplaysWaitingRunWithoutCreatingAnotherResume()
    {
        var waiting = Active(Orchestrator, "waiting_input", """{"clarification":["Which region?"]}""");
        var backend = new QueueHandler(
            Json(HttpStatusCode.OK, waiting),
            Json(HttpStatusCode.OK, waiting));

        var result = await Build(backend, Enabled()).RunAsync(
            "same turn", "c1", null, Identity(), "retry-token");

        Assert.Equal("Which region?", result!.Text);
        Assert.Equal(["/api/chat-runs/replay", $"/api/chat-runs/{Run:D}"], backend.Requests.Select(x => x.Path));
    }

    [Fact]
    public async Task LogicalAttemptRetry_RekicksOnlyItsMatchedQueuedCommand()
    {
        var replayCommand = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var queued = Active(Orchestrator, "queued", "{}")
            .Replace(Command.ToString(), replayCommand.ToString(), StringComparison.OrdinalIgnoreCase);
        var backend = new QueueHandler(
            Json(HttpStatusCode.OK, queued),
            Json(HttpStatusCode.OK, Active(Orchestrator, "completed", """{"aggregate":{"answer":"done"}}""")));
        var workflow = new QueueHandler(Json(HttpStatusCode.Accepted, "{}"));

        var result = await Build(backend, Enabled(), workflow).RunAsync(
            "same turn", "c1", null, Identity(), "retry-token");

        Assert.Equal("done", result!.Text);
        Assert.Single(workflow.Requests);
        Assert.Contains(replayCommand.ToString(), workflow.Requests[0].Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LogicalAttemptMismatch_ConflictsWithoutActiveLookupOrAllocation()
    {
        var backend = new QueueHandler(Json(HttpStatusCode.Conflict,
            """{"message":"Chat logical attempt does not match this request"}"""));

        await Assert.ThrowsAsync<DownstreamConflictException>(() => Build(backend, Enabled()).RunAsync(
            "changed message", "c1", Orchestrator, Identity(), "same-attempt"));

        var request = Assert.Single(backend.Requests);
        Assert.Equal("/api/chat-runs/replay", request.Path);
        Assert.Contains("\"message\":\"changed message\"", request.Body);
    }

    [Fact]
    public async Task LogicalAttemptReplay_CanonicalizesSurroundingWhitespaceBeforeFingerprinting()
    {
        var completed = Active(Orchestrator, "completed", """{"aggregate":{"answer":"replayed"}}""");
        var backend = new QueueHandler(Json(HttpStatusCode.OK, completed), Json(HttpStatusCode.OK, completed));

        var result = await Build(backend, Enabled()).RunAsync(
            "  details  ", "  c1  ", null, Identity(), "retry-token");

        Assert.Equal("replayed", result!.Text);
        Assert.Equal("/api/chat-runs/replay", backend.Requests[0].Path);
        Assert.Contains("\"conversation_id\":\"c1\"", backend.Requests[0].Body);
        Assert.Contains("\"message\":\"details\"", backend.Requests[0].Body);
    }

    // ---- 終端狀態 render(PollAsync 的 switch 與 RenderResult/RenderClarification 的每個分支)----

    // 非成功終端狀態與未知狀態:一律拋 WorkflowInvocationException,不得把它當成可回給使用者的答案。
    [Theory]
    [InlineData("failed")]
    [InlineData("cancelled")]
    [InlineData("timed_out")]
    [InlineData("not-a-real-status")]
    public async Task Poll_NonSuccessTerminalStatus_Throws(string status)
    {
        var backend = new QueueHandler(
            Json(HttpStatusCode.OK, Active(Orchestrator, status, "{}")),
            Json(HttpStatusCode.OK, Active(Orchestrator, status, "{}")));

        await Assert.ThrowsAsync<WorkflowInvocationException>(
            () => Build(backend, Enabled()).RunAsync("hello", "c1", null, Identity()));
    }

    // completed 但 result 不是 {aggregate:{answer:string}} 的形狀:如實回傳原始 JSON,不得吞成空字串。
    [Theory]
    [InlineData("""{"aggregate":"plain text"}""", "\"plain text\"")]          // aggregate 非 object
    [InlineData("""{"answer":"no aggregate"}""", """{"answer":"no aggregate"}""")]  // 完全沒有 aggregate
    public async Task Poll_CompletedWithoutAggregateAnswer_RendersRawResult(string result, string expected)
    {
        var backend = new QueueHandler(
            Json(HttpStatusCode.OK, Active(Orchestrator, "completed", result)),
            Json(HttpStatusCode.OK, Active(Orchestrator, "completed", result)));

        var response = await Build(backend, Enabled()).RunAsync("hello", "c1", null, Identity());

        Assert.Equal(expected, response!.Text);
    }

    [Fact]
    public async Task Poll_CompletedWithoutResult_Throws()
    {
        var backend = new QueueHandler(
            Json(HttpStatusCode.OK, Active(Orchestrator, "completed", "null")),
            Json(HttpStatusCode.OK, Active(Orchestrator, "completed", "null")));

        await Assert.ThrowsAsync<WorkflowInvocationException>(
            () => Build(backend, Enabled()).RunAsync("hello", "c1", null, Identity()));
    }

    // waiting_input 卻沒有可問的問題(缺 clarification / 全是空白字串):拋例外,不得回一句空話給使用者。
    [Theory]
    [InlineData("{}")]
    [InlineData("""{"clarification":[]}""")]
    [InlineData("""{"clarification":["  ","\t"]}""")]
    public async Task Poll_WaitingWithoutClarification_Throws(string result)
    {
        var waiting = Active(Orchestrator, "waiting_input", result);
        var backend = new QueueHandler(
            Json(HttpStatusCode.OK, waiting),
            Json(HttpStatusCode.OK, waiting));

        await Assert.ThrowsAsync<WorkflowInvocationException>(
            () => Build(backend, Enabled()).RunAsync("same turn", "c1", null, Identity(), "retry-token"));
    }

    // 呼叫端(瀏覽器)斷線:停止輪詢並原樣傳播取消,但絕不對 durable run 送出 cancel 命令
    // ——契約明文「client disconnect detaches from polling and does not cancel the durable run」。
    [Fact]
    public async Task CallerCancellation_StopsPolling_WithoutCancellingDurableRun()
    {
        using var cancellation = new CancellationTokenSource();
        var running = Active(Orchestrator, "running", "{}");
        var backend = new QueueHandler(
            Json(HttpStatusCode.OK, running),   // active 查詢
            Json(HttpStatusCode.OK, running),   // 第一次輪詢 → 呼叫端此時斷線
            Json(HttpStatusCode.OK, running))   // 若還繼續輪詢才會用到(不該用到)
        {
            OnRequest = request =>
            {
                if (request.Path == $"/api/chat-runs/{Run:D}")
                    cancellation.Cancel();
            },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Build(backend, Enabled())
                .RunAsync("hello", "c1", null, Identity(), null, cancellation.Token));

        Assert.Equal(
            ["/api/chat-runs/active", $"/api/chat-runs/{Run:D}"],
            backend.Requests.Select(x => x.Path));
        Assert.DoesNotContain(backend.Requests, x => x.Path.EndsWith("/cancel", StringComparison.Ordinal));
    }

    private static AgentChatRuntime Build(
        QueueHandler backendHandler, AgentChatOptions options, QueueHandler? workflowHandler = null)
    {
        var backend = new BackendClient(
            new HttpClient(backendHandler),
            new BackendOptions { BaseUrl = "http://backend", InternalToken = "token" });
        return new AgentChatRuntime(
            backend,
            new HttpClient(workflowHandler ?? new QueueHandler()),
            new WorkflowOptions { BaseUrl = "http://workflow", InternalToken = "token" },
            options,
            NullLogger<AgentChatRuntime>.Instance);
    }

    private static AgentChatOptions Enabled() => new()
    {
        Enabled = true,
        TenantAllowlist = new HashSet<string>(["tenant-a"], StringComparer.Ordinal),
    };

    /// <summary>只有旗標關閉這一個否決條件成立(租戶仍在 allowlist),用來單獨釘住 Enabled 那一半。</summary>
    private static AgentChatOptions DisabledButAllowlisted() => new()
    {
        Enabled = false,
        TenantAllowlist = new HashSet<string>(["tenant-a"], StringComparer.Ordinal),
    };

    private static AgentChatOptions EnabledFor(string tenant) => new()
    {
        Enabled = true,
        TenantAllowlist = new HashSet<string>([tenant], StringComparer.Ordinal),
    };

    private static FakeChatIdentityAccessor Identity(UserContext? user = null, string conversation = "c1")
    {
        var identity = new FakeChatIdentityAccessor();
        identity.SetRequestKeys(null, conversation, user ?? User);
        return identity;
    }

    /// <summary>匿名呼叫者:CurrentUser 為 null(沒有可授權、可歸屬的身分)。</summary>
    private static FakeChatIdentityAccessor AnonymousIdentity()
    {
        var identity = new FakeChatIdentityAccessor();
        identity.SetRequestKeys(null, "c1", null);
        return identity;
    }

    private static void AssertOpaqueKey(
        string? key, string operation, params string[] callerValues)
    {
        Assert.NotNull(key);
        Assert.InRange(key!.Length, 1, 128);
        Assert.Matches($"^{operation}:sha256:[0-9a-f]{{64}}$", key);
        foreach (var value in callerValues)
            Assert.DoesNotContain(value, key, StringComparison.Ordinal);
    }

    private static string Accepted(Guid? orchestrator = null) =>
        $"{{\"mode\":\"orchestrator\",\"run\":{{\"id\":\"{Run}\",\"orchestrator_id\":\"{orchestrator ?? Orchestrator}\",\"orchestrator_revision\":2,\"workflow_id\":\"44444444-4444-4444-4444-444444444444\",\"workflow_revision\":3}},\"command_id\":\"{Command}\"}}";

    private static string Active(Guid orchestrator, string status, string result) =>
        $"{{\"mode\":\"orchestrator\",\"run\":{{\"id\":\"{Run}\",\"orchestrator_id\":\"{orchestrator}\",\"orchestrator_revision\":2,\"workflow_id\":\"44444444-4444-4444-4444-444444444444\",\"workflow_revision\":3,\"status\":\"{status}\",\"result\":{result}}},\"command_id\":\"{Command}\"}}";

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class QueueHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        public List<CapturedRequest> Requests { get; } = [];

        /// <summary>每個請求記錄後執行,供「請求進行中呼叫端斷線」這類時序測試觸發外部事件。</summary>
        public Action<CapturedRequest>? OnRequest { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var captured = new CapturedRequest(
                request.RequestUri!.AbsolutePath,
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken),
                request.Headers.TryGetValues("Idempotency-Key", out var values)
                    ? values.Single()
                    : null);
            Requests.Add(captured);
            OnRequest?.Invoke(captured);
            if (_responses.Count == 0)
                throw new InvalidOperationException("Unexpected request");
            return _responses.Dequeue();
        }
    }

    private sealed record CapturedRequest(string Path, string Body, string? IdempotencyKey);
}
