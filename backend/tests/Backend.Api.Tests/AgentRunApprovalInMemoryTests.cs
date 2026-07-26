using Backend.Api.Agents;
using Backend.Api.AgentRuns;
using Backend.Api.Data.InMemory;
using Backend.Api.Skills;

namespace Backend.Api.Tests;

/// <summary>Parity coverage for the lite D7 approval/effect state machine.</summary>
public sealed class AgentRunApprovalInMemoryTests
{
    [Fact]
    public async Task Approval_EnforcesSoDLeaseClaimExpiryAndEvidenceIdempotency()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 25, 0, 0, 0, TimeSpan.Zero));
        var (agents, skills, agent) = await PublishedAgentAsync("d7-inmemory");
        var runs = new InMemoryAgentRunRepository(agents, skills, clock);
        var approvals = new InMemoryAgentRunApprovalRepository(runs, clock);
        var running = await StartRunningAsync(runs, agent.Id);
        var fingerprint = new string('b', 64);
        var created = await approvals.CreateAsync("demo-a", "admin-a", running.Run!.Id,
            new AgentRunApprovalCreateRequest(running.Run.StateVersion, running.Token, running.Generation,
                Checkpoint(running.Generation), 1, "USER", fingerprint, clock.GetUtcNow().UtcDateTime.AddMinutes(5), SelfApprovalForbidden: false), default);
        Assert.True(created.Status == AgentRunApprovalWriteStatus.Success, created.Message);
        Assert.True(created.Approval!.SelfApprovalForbidden);
        Assert.Equal(AgentRunApprovalWriteStatus.Forbidden,
            (await approvals.DecideAsync("demo-a", "admin-a", "USER", running.Run.Id, created.Approval.Id, true, "self", null, default)).Status);
        Assert.Equal(AgentRunApprovalWriteStatus.NotFound,
            (await approvals.DecideAsync("demo-b", "approver-b", "USER", running.Run.Id, created.Approval.Id, true, "cross", null, default)).Status);
        Assert.Equal(AgentRunApprovalWriteStatus.Success,
            (await approvals.DecideAsync("demo-a", "approver-a", "USER", running.Run.Id, created.Approval.Id, true, "approve", null, default)).Status);

        var firstClaim = await approvals.ClaimExecuteAsync("demo-a", running.Run.Id, created.Approval.Id, default);
        Assert.NotNull(firstClaim);
        clock.Advance(TimeSpan.FromSeconds(61));
        var reclaimed = await approvals.ClaimExecuteAsync("demo-a", running.Run.Id, created.Approval.Id, default);
        Assert.NotNull(reclaimed);
        Assert.NotEqual(firstClaim!.ClaimToken, reclaimed!.ClaimToken);
        Assert.Equal(AgentRunApprovalWriteStatus.Conflict,
            await approvals.CompleteExecuteAsync(created.Approval.Id, firstClaim.ClaimToken, false, default));

        var queued = await runs.GetAsync("demo-a", "admin-a", running.Run.Id, default);
        var writeLease = await runs.ClaimLeaseAsync("demo-a", "admin-a", running.Run.Id,
            new AgentRunLeaseRequest(queued!.StateVersion, "write-worker", 120), default);
        var writeRunning = await runs.TransitionAsync("demo-a", "admin-a", running.Run.Id,
            new AgentRunTransitionRequest(writeLease.Lease!.Run.StateVersion, AgentRunStatuses.Running,
                writeLease.Lease.LeaseToken, writeLease.Lease.LeaseGeneration, writeLease.Lease.EventAckCursor), default);
        Assert.Equal(AgentRunWriteStatus.Success, writeRunning.Status);
        Assert.Equal(AgentRunApprovalWriteStatus.Conflict,
            (await approvals.ConsumeAsync("demo-a", running.Run.Id, created.Approval.Id,
                new AgentRunApprovalConsumeRequest(fingerprint, "stale", running.Generation), default)).Status);
        var effect = await approvals.ConsumeAsync("demo-a", running.Run.Id, created.Approval.Id,
            new AgentRunApprovalConsumeRequest(fingerprint, writeLease.Lease.LeaseToken, writeLease.Lease.LeaseGeneration), default);
        Assert.Equal(AgentRunApprovalWriteStatus.Success, effect.Status);
        Assert.Equal(AgentRunApprovalWriteStatus.Success,
            (await approvals.WriteEvidenceAsync("demo-a", running.Run.Id, effect.Response!.EffectId,
                new AgentRunWriteEvidenceRequest("refund-1", "approved"), default)).Status);
        Assert.Equal(AgentRunApprovalWriteStatus.Replay,
            (await approvals.WriteEvidenceAsync("demo-a", running.Run.Id, effect.Response.EffectId,
                new AgentRunWriteEvidenceRequest("refund-1", "approved"), default)).Status);
        Assert.Equal(AgentRunApprovalWriteStatus.Conflict,
            (await approvals.WriteEvidenceAsync("demo-a", running.Run.Id, effect.Response.EffectId,
                new AgentRunWriteEvidenceRequest("refund-1", "changed"), default)).Status);
        Assert.Equal(AgentRunApprovalWriteStatus.Success,
            await approvals.CompleteExecuteAsync(created.Approval.Id, reclaimed.ClaimToken, false, default));

        // A pending expired approval is terminalized, rather than leaving a
        // permanently waiting_approval run with no recovery command.
        var expiredRun = await StartRunningAsync(runs, agent.Id, "expired");
        var expired = await approvals.CreateAsync("demo-a", "admin-a", expiredRun.Run!.Id,
            new AgentRunApprovalCreateRequest(expiredRun.Run.StateVersion, expiredRun.Token, expiredRun.Generation,
                Checkpoint(expiredRun.Generation), 1, "USER", new string('c', 64), clock.GetUtcNow().UtcDateTime.AddSeconds(1)), default);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(AgentRunApprovalWriteStatus.Expired,
            (await approvals.DecideAsync("demo-a", "approver-a", "USER", expiredRun.Run.Id, expired.Approval!.Id, true, "expired", null, default)).Status);
        Assert.Equal(AgentRunStatuses.Failed, (await runs.GetAsync("demo-a", "admin-a", expiredRun.Run.Id, default))!.Status);

        // A waiting approval is active work for cancellation recovery even
        // though generic resume is forbidden.  If the initial Workflow kick is
        // lost, the durable cancel command must still be claimable.
        var cancellable = await StartRunningAsync(runs, agent.Id, "cancel-waiting");
        var waiting = await approvals.CreateAsync("demo-a", "admin-a", cancellable.Run!.Id,
            new AgentRunApprovalCreateRequest(cancellable.Run.StateVersion, cancellable.Token, cancellable.Generation,
                Checkpoint(cancellable.Generation), 1, "USER", new string('d', 64), clock.GetUtcNow().UtcDateTime.AddMinutes(5)), default);
        Assert.Equal(AgentRunApprovalWriteStatus.Success, waiting.Status);
        var cancelled = await runs.CancelAsync("demo-a", "admin-a", cancellable.Run.Id, "cancel approval", "cancel-waiting", default);
        Assert.Equal(AgentRunStatuses.WaitingApproval, cancelled.Run!.Status);
        clock.Advance(TimeSpan.FromSeconds(31)); // simulate a lost initial Workflow kick
        var recovery = await runs.ClaimRecoveryAsync(new AgentRunRecoveryClaimRequest("recovery", 20, 30), default);
        Assert.Contains(recovery.Items, item => item.RunId == cancellable.Run.Id && item.CommandType == "cancel");
        var cancelWins = await approvals.DecideAsync("demo-a", "approver-a", "USER", cancellable.Run.Id,
            waiting.Approval!.Id, true, "approve-after-cancel", null, default);
        Assert.Equal(AgentRunApprovalWriteStatus.InvalidState, cancelWins.Status);
        Assert.Equal("cancelled", cancelWins.Approval!.Status);
        Assert.Null(await approvals.ClaimExecuteAsync("demo-a", cancellable.Run.Id, waiting.Approval.Id, default));
        Assert.Equal(AgentRunApprovalWriteStatus.Conflict,
            (await approvals.ConsumeAsync("demo-a", cancellable.Run.Id, waiting.Approval.Id,
                new AgentRunApprovalConsumeRequest(new string('d', 64), cancellable.Token, cancellable.Generation), default)).Status);

        // Once a write effect is reserved, cancellation still fences the
        // durable evidence/outbox transaction before it can make the write.
        var reserveThenCancel = await StartRunningAsync(runs, agent.Id, "reserve-then-cancel");
        var reserveApproval = await approvals.CreateAsync("demo-a", "admin-a", reserveThenCancel.Run!.Id,
            new AgentRunApprovalCreateRequest(reserveThenCancel.Run.StateVersion, reserveThenCancel.Token, reserveThenCancel.Generation,
                Checkpoint(reserveThenCancel.Generation), 1, "USER", new string('e', 64), clock.GetUtcNow().UtcDateTime.AddMinutes(5)), default);
        Assert.Equal(AgentRunApprovalWriteStatus.Success,
            (await approvals.DecideAsync("demo-a", "approver-a", "USER", reserveThenCancel.Run.Id, reserveApproval.Approval!.Id, true, "reserve-approve", null, default)).Status);
        var reserveQueued = await runs.GetAsync("demo-a", "admin-a", reserveThenCancel.Run.Id, default);
        var reserveLease = await runs.ClaimLeaseAsync("demo-a", "admin-a", reserveThenCancel.Run.Id,
            new AgentRunLeaseRequest(reserveQueued!.StateVersion, "write-worker", 120), default);
        Assert.Equal(AgentRunWriteStatus.Success,
            (await runs.TransitionAsync("demo-a", "admin-a", reserveThenCancel.Run.Id,
                new AgentRunTransitionRequest(reserveLease.Lease!.Run.StateVersion, AgentRunStatuses.Running, reserveLease.Lease.LeaseToken,
                    reserveLease.Lease.LeaseGeneration, reserveLease.Lease.EventAckCursor), default)).Status);
        var reserved = await approvals.ConsumeAsync("demo-a", reserveThenCancel.Run.Id, reserveApproval.Approval.Id,
            new AgentRunApprovalConsumeRequest(new string('e', 64), reserveLease.Lease.LeaseToken, reserveLease.Lease.LeaseGeneration), default);
        Assert.Equal(AgentRunApprovalWriteStatus.Success, reserved.Status);
        await runs.CancelAsync("demo-a", "admin-a", reserveThenCancel.Run.Id, "cancel reserved write", "reserve-cancel", default);
        Assert.Equal(AgentRunApprovalWriteStatus.InvalidState,
            (await approvals.WriteEvidenceAsync("demo-a", reserveThenCancel.Run.Id, reserved.Response!.EffectId,
                new AgentRunWriteEvidenceRequest("must-not-write", "cancelled"), default)).Status);
    }

    /// <summary>
    /// 過期 × 角色不符:兩個 authority 必須同結果。Dapper(AgentRunApprovalRepository.cs:62)先判過期,
    /// 因為過期是終局決定 —— 若讓角色不符的 Forbidden 先返回,run 會永遠停在 waiting_approval
    /// 且沒有 execute row 可回收(正是 Dapper 註解要避免的 strand)。lite 模式是生產碼,不得有第二種語意。
    /// </summary>
    [Fact]
    public async Task Decide_ExpiredApproval_WithMismatchedRole_ExpiresAndFailsRun()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 25, 0, 0, 0, TimeSpan.Zero));
        var (agents, skills, agent) = await PublishedAgentAsync("d7-inmemory-expired-role");
        var runs = new InMemoryAgentRunRepository(agents, skills, clock);
        var approvals = new InMemoryAgentRunApprovalRepository(runs, clock);
        var running = await StartRunningAsync(runs, agent.Id, "expired-wrong-role");
        var created = await approvals.CreateAsync("demo-a", "admin-a", running.Run!.Id,
            new AgentRunApprovalCreateRequest(running.Run.StateVersion, running.Token, running.Generation,
                Checkpoint(running.Generation), 1, "USER", new string('f', 64),
                clock.GetUtcNow().UtcDateTime.AddSeconds(1)), default);
        Assert.Equal(AgentRunApprovalWriteStatus.Success, created.Status);

        clock.Advance(TimeSpan.FromSeconds(2));
        var decided = await approvals.DecideAsync("demo-a", "approver-a", "ADMIN", running.Run.Id,
            created.Approval!.Id, true, "expired-wrong-role", null, default);

        Assert.Equal(AgentRunApprovalWriteStatus.Expired, decided.Status);
        Assert.Equal("expired", decided.Approval!.Status);
        Assert.Equal(AgentRunStatuses.Failed, (await runs.GetAsync("demo-a", "admin-a", running.Run.Id, default))!.Status);
    }

    // 建立 approval 前的 durable gate:錯的 lease token / 舊 generation / 舊 expected_version 都必須擋在
    // 「產生任何 effect 之前」。這是 waiting_approval 這道門的入口 fence,漏掉會讓過期 worker 開出授權。
    [Theory]
    [InlineData("stale-token", 0, 0)]   // 錯 lease token
    [InlineData(null, 1, 0)]            // generation +1(不是目前這把 lease)
    [InlineData(null, 0, -1)]           // expected_version 落後一版
    public async Task CreateApproval_StaleLeaseOrGeneration_IsConflict(
        string? token, long generationDelta, long versionDelta)
    {
        var fixture = await FixtureAsync("d7-create-fence");
        var running = fixture.Running;

        var result = await fixture.Approvals.CreateAsync("demo-a", "admin-a", running.Run.Id,
            new AgentRunApprovalCreateRequest(
                running.Run.StateVersion + versionDelta,
                token ?? running.Token,
                running.Generation + generationDelta,
                Checkpoint(running.Generation), 1, "USER", new string('a', 64),
                fixture.Clock.GetUtcNow().UtcDateTime.AddMinutes(5)), default);

        Assert.Equal(AgentRunApprovalWriteStatus.Conflict, result.Status);
        // 失敗必須不留下 approval:後續合法建立仍要成功。
        Assert.Equal(AgentRunApprovalWriteStatus.Success, (await CreateApprovalAsync(fixture)).Status);
    }

    public static TheoryData<string, string, int> RejectedApprovalRequests => new()
    {
        { "user", new string('a', 64), 300 },              // required_role 必須全大寫開頭
        { new string('A', 65), new string('a', 64), 300 }, // required_role 上限 64,65 即拒
        { "USER", new string('A', 64), 300 },              // fingerprint 必須小寫 hex
        { "USER", new string('a', 63), 300 },              // fingerprint 長度 -1
        { "USER", new string('a', 65), 300 },              // fingerprint 長度 +1
        { "USER", new string('a', 64), 0 },                // expires_at == now(非嚴格大於)
        { "USER", new string('a', 64), 86_401 },           // now + 1 天 + 1 秒,超出上限
    };

    [Theory]
    [MemberData(nameof(RejectedApprovalRequests))]
    public async Task CreateApproval_RejectsRoleFingerprintAndExpiryOutOfRange(
        string role, string fingerprint, int expiresInSeconds)
    {
        var fixture = await FixtureAsync("d7-create-validation");

        var result = await fixture.Approvals.CreateAsync("demo-a", "admin-a", fixture.Running.Run.Id,
            new AgentRunApprovalCreateRequest(
                fixture.Running.Run.StateVersion, fixture.Running.Token, fixture.Running.Generation,
                Checkpoint(fixture.Running.Generation), 1, role, fingerprint,
                fixture.Clock.GetUtcNow().UtcDateTime.AddSeconds(expiresInSeconds)), default);

        Assert.Equal(AgentRunApprovalWriteStatus.InvalidState, result.Status);
    }

    // On-point 對照:剛好 64 字元角色 + 剛好 now+1 天的到期,兩個上限都必須放行。
    [Fact]
    public async Task CreateApproval_AcceptsRoleAndExpiryAtInclusiveBoundary()
    {
        var fixture = await FixtureAsync("d7-create-boundary");

        var result = await fixture.Approvals.CreateAsync("demo-a", "admin-a", fixture.Running.Run.Id,
            new AgentRunApprovalCreateRequest(
                fixture.Running.Run.StateVersion, fixture.Running.Token, fixture.Running.Generation,
                Checkpoint(fixture.Running.Generation), 1, new string('A', 64), new string('a', 64),
                fixture.Clock.GetUtcNow().UtcDateTime.AddDays(1)), default);

        Assert.Equal(AgentRunApprovalWriteStatus.Success, result.Status);
    }

    // root AGENTS.md 明列的 recheck:consume 必須比對 action fingerprint。格式非法與「格式合法但換了動作」
    // 是兩個不同結果(InvalidState vs Conflict),兩者都得在**碰到 lease 之前**就擋下。
    [Fact]
    public async Task Consume_MalformedOrMismatchedActionFingerprint_IsRejectedBeforeLeaseCheck()
    {
        var fixture = await FixtureAsync("d7-consume-fingerprint");
        var fingerprint = new string('a', 64);
        var approval = (await CreateApprovalAsync(fixture, fingerprint)).Approval!;
        Assert.Equal(AgentRunApprovalWriteStatus.Success,
            (await fixture.Approvals.DecideAsync("demo-a", "approver-a", "USER", fixture.Running.Run.Id,
                approval.Id, true, "approve", null, default)).Status);

        var malformed = await fixture.Approvals.ConsumeAsync("demo-a", fixture.Running.Run.Id, approval.Id,
            new AgentRunApprovalConsumeRequest(new string('A', 64), fixture.Running.Token, fixture.Running.Generation), default);
        Assert.Equal(AgentRunApprovalWriteStatus.InvalidState, malformed.Status);

        var mismatched = await fixture.Approvals.ConsumeAsync("demo-a", fixture.Running.Run.Id, approval.Id,
            new AgentRunApprovalConsumeRequest(new string('b', 64), fixture.Running.Token, fixture.Running.Generation), default);
        // 訊息本身就是「fingerprint 比對排在 lease 檢查之前」的證據:這把 lease 在核准後已作廢,
        // 若順序反了會先回 "stale write lease"。
        Assert.Equal(AgentRunApprovalWriteStatus.Conflict, mismatched.Status);
        Assert.Equal("approval action changed", mismatched.Message);

        // 兩次拒絕都不得保留 effect:換上有效 lease 後同一 approval 仍可正常 consume。
        var write = await ResumeRunningAsync(fixture);
        Assert.Equal(AgentRunApprovalWriteStatus.Success,
            (await fixture.Approvals.ConsumeAsync("demo-a", fixture.Running.Run.Id, approval.Id,
                new AgentRunApprovalConsumeRequest(fingerprint, write.Token, write.Generation), default)).Status);
    }

    // 執行身分只能給「做出這個決定的人」,而且只在 approved/consumed 期間有效;跨租戶一律 null。
    // 它是 Workflow 拿來冒充 approver 執行寫入的唯一憑據,洩漏等同越權。
    [Fact]
    public async Task ExecutionIdentity_IsDeciderAndTenantScoped()
    {
        var fixture = await FixtureAsync("d7-execution-identity");
        var runId = fixture.Running.Run.Id;
        var approval = (await CreateApprovalAsync(fixture)).Approval!;

        // pending 期間任何人都拿不到。
        Assert.Null(await fixture.Approvals.GetExecutionIdentityAsync("demo-a", "approver-a", runId, approval.Id, default));

        Assert.Equal(AgentRunApprovalWriteStatus.Success,
            (await fixture.Approvals.DecideAsync("demo-a", "approver-a", "USER", runId, approval.Id, true, "approve", null, default)).Status);

        var identity = await fixture.Approvals.GetExecutionIdentityAsync("demo-a", "approver-a", runId, approval.Id, default);
        Assert.NotNull(identity);
        Assert.Equal("admin-a", identity!.UserId);
        Assert.Equal("USER", identity.Role);
        Assert.Null(await fixture.Approvals.GetExecutionIdentityAsync("demo-a", "someone-else", runId, approval.Id, default));
        Assert.Null(await fixture.Approvals.GetExecutionIdentityAsync("demo-b", "approver-a", runId, approval.Id, default));
    }

    // effect 完成是 once-only 的:同結果重送要冪等成功,換結果或非 reserved 一律 Conflict,跨租戶 NotFound。
    [Fact]
    public async Task CompleteEffect_IsIdempotent_AndFencesNonReservedOrCrossTenant()
    {
        var fixture = await FixtureAsync("d7-complete-effect");
        var runId = fixture.Running.Run.Id;
        var fingerprint = new string('a', 64);
        var approval = (await CreateApprovalAsync(fixture, fingerprint)).Approval!;
        Assert.Equal(AgentRunApprovalWriteStatus.Success,
            (await fixture.Approvals.DecideAsync("demo-a", "approver-a", "USER", runId, approval.Id, true, "approve", null, default)).Status);
        var write = await ResumeRunningAsync(fixture);
        var effect = await fixture.Approvals.ConsumeAsync("demo-a", runId, approval.Id,
            new AgentRunApprovalConsumeRequest(fingerprint, write.Token, write.Generation), default);
        var effectId = effect.Response!.EffectId;

        Assert.Equal(AgentRunApprovalWriteStatus.NotFound,
            await fixture.Approvals.CompleteEffectAsync("demo-b", runId, effectId, true, default));
        Assert.Equal(AgentRunApprovalWriteStatus.NotFound,
            await fixture.Approvals.CompleteEffectAsync("demo-a", runId, Guid.NewGuid(), true, default));
        Assert.Equal(AgentRunApprovalWriteStatus.Success,
            await fixture.Approvals.CompleteEffectAsync("demo-a", runId, effectId, true, default));
        Assert.Equal(AgentRunApprovalWriteStatus.Success,
            await fixture.Approvals.CompleteEffectAsync("demo-a", runId, effectId, true, default));
        Assert.Equal(AgentRunApprovalWriteStatus.Conflict,
            await fixture.Approvals.CompleteEffectAsync("demo-a", runId, effectId, false, default));
    }

    // 被遺棄的 execute claim 必須被回收(否則已核准的寫入永遠不會發生);而一旦 run 被要求取消,
    // 同一個 execution 要轉 dead_letter 且**不再**被任何 recovery 撿起來。
    [Fact]
    public async Task ExecuteRecovery_ReclaimsAbandonedClaim_ThenDeadLettersCancelledRun()
    {
        var fixture = await FixtureAsync("d7-execute-recovery");
        var runId = fixture.Running.Run.Id;
        var approval = (await CreateApprovalAsync(fixture)).Approval!;
        Assert.Equal(AgentRunApprovalWriteStatus.Success,
            (await fixture.Approvals.DecideAsync("demo-a", "approver-a", "USER", runId, approval.Id, true, "approve", null, default)).Status);

        var first = Assert.Single(await fixture.Approvals.ClaimExecuteRecoveryAsync(20, default));
        Assert.Equal(approval.Id, first.ApprovalId);
        Assert.Equal("approver-a", first.ApproverId);
        // claim 仍在有效期內 → 不得被第二個 worker 搶走。
        Assert.Empty(await fixture.Approvals.ClaimExecuteRecoveryAsync(20, default));

        fixture.Clock.Advance(TimeSpan.FromSeconds(61));
        var reclaimed = Assert.Single(await fixture.Approvals.ClaimExecuteRecoveryAsync(20, default));
        Assert.NotEqual(first.ClaimToken, reclaimed.ClaimToken);
        Assert.Equal(AgentRunApprovalWriteStatus.Conflict,
            await fixture.Approvals.CompleteExecuteAsync(approval.Id, first.ClaimToken, false, default));

        await fixture.Runs.CancelAsync("demo-a", "admin-a", runId, "stop", "cancel-exec-recovery", default);
        fixture.Clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Empty(await fixture.Approvals.ClaimExecuteRecoveryAsync(20, default));
        Assert.Null(await fixture.Approvals.ClaimExecuteAsync("demo-a", runId, approval.Id, default));
    }

    // dead_letter ACK 代表「已核准的寫入無法安全完成」:該 execution 必須終局化,
    // 不能留在 queued/claimed 讓下一輪 recovery 無限重試。既有測試全部只傳 deadLetter:false。
    // run 本身的終局化由 CompleteExecute_DeadLetter_TerminalizesOnlyAnUncancelledRun 覆蓋。
    [Fact]
    public async Task CompleteExecute_DeadLetter_TerminatesExecutionAndFencesTheClaim()
    {
        var fixture = await FixtureAsync("d7-dead-letter");
        var runId = fixture.Running.Run.Id;
        var approval = (await CreateApprovalAsync(fixture)).Approval!;
        Assert.Equal(AgentRunApprovalWriteStatus.Success,
            (await fixture.Approvals.DecideAsync("demo-a", "approver-a", "USER", runId, approval.Id, true, "approve", null, default)).Status);
        var claim = await fixture.Approvals.ClaimExecuteAsync("demo-a", runId, approval.Id, default);

        Assert.Equal(AgentRunApprovalWriteStatus.Success,
            await fixture.Approvals.CompleteExecuteAsync(approval.Id, claim!.ClaimToken, true, default));

        // 同一個 claim token 不得重複 ACK,dead_letter 之後也不得再被 recovery 撿回來重跑。
        Assert.Equal(AgentRunApprovalWriteStatus.Conflict,
            await fixture.Approvals.CompleteExecuteAsync(approval.Id, claim.ClaimToken, true, default));
        fixture.Clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Empty(await fixture.Approvals.ClaimExecuteRecoveryAsync(20, default));
        Assert.Null(await fixture.Approvals.ClaimExecuteAsync("demo-a", runId, approval.Id, default));
    }

    // dead_letter ACK 的另一半決策表:execution 終局化之後,run 本身也必須終局化。
    // Dapper(AgentRunApprovalRepository.cs:236)直接 UPDATE 成 failed + approved_write_unrecoverable,
    // 且 WHERE 帶 cancel_requested_at IS NULL —— 已要求取消的 run 由 cancel 指令終局化,不得被改寫成 failed。
    [Theory]
    [InlineData(false, AgentRunStatuses.Failed, "approved_write_unrecoverable")]
    [InlineData(true, AgentRunStatuses.Queued, null)]
    public async Task CompleteExecute_DeadLetter_TerminalizesOnlyAnUncancelledRun(
        bool cancelRequested, string expectedStatus, string? expectedErrorCode)
    {
        var fixture = await FixtureAsync("d7-dead-letter-run");
        var runId = fixture.Running.Run.Id;
        var approval = (await CreateApprovalAsync(fixture)).Approval!;
        Assert.Equal(AgentRunApprovalWriteStatus.Success,
            (await fixture.Approvals.DecideAsync("demo-a", "approver-a", "USER", runId, approval.Id, true, "approve", null, default)).Status);
        var claim = await fixture.Approvals.ClaimExecuteAsync("demo-a", runId, approval.Id, default);
        if (cancelRequested)
        {
            await fixture.Runs.CancelAsync("demo-a", "admin-a", runId, "stop", "cancel-dead-letter", default);
        }

        Assert.Equal(AgentRunApprovalWriteStatus.Success,
            await fixture.Approvals.CompleteExecuteAsync(approval.Id, claim!.ClaimToken, true, default));

        var run = await fixture.Runs.GetAsync("demo-a", "admin-a", runId, default);
        Assert.Equal(expectedStatus, run!.Status);
        Assert.Equal(expectedErrorCode, run.ErrorCode);
    }

    // 過期 × 已要求取消:Dapper 的過期 UPDATE(AgentRunApprovalRepository.cs:68)只看
    // status='waiting_approval',不看 cancel_requested_at,所以耐久 run 一定變 failed;
    // lite 若把終局化交給會擋 cancel 的一般轉移,run 會留在 waiting_approval 永遠沒人回收。
    [Fact]
    public async Task Decide_ExpiredApproval_OnCancelRequestedRun_StillFailsTheDurableRun()
    {
        var fixture = await FixtureAsync("d7-expired-cancelled");
        var runId = fixture.Running.Run.Id;
        var approval = (await CreateApprovalAsync(fixture)).Approval!;
        await fixture.Runs.CancelAsync("demo-a", "admin-a", runId, "stop", "cancel-expired", default);
        fixture.Clock.Advance(TimeSpan.FromMinutes(6));

        var decided = await fixture.Approvals.DecideAsync(
            "demo-a", "approver-a", "USER", runId, approval.Id, true, "expired-cancel", null, default);

        Assert.Equal(AgentRunApprovalWriteStatus.Expired, decided.Status);
        Assert.Equal("expired", decided.Approval!.Status);
        var run = await fixture.Runs.GetAsync("demo-a", "admin-a", runId, default);
        Assert.Equal(AgentRunStatuses.Failed, run!.Status);
        Assert.Equal("approval_expired", run.ErrorCode);
    }

    // Dapper 在決定前重讀 run 並要求它仍是 waiting_approval(AgentRunApprovalRepository.cs:86),
    // 回 InvalidState 且附上未變動的 approval;lite 少了這道檢查,只能靠下游轉移回一個沒有內容的 Conflict。
    [Fact]
    public async Task Decide_WhenRunNoLongerWaitsForApproval_IsInvalidStateWithThePendingApproval()
    {
        var fixture = await FixtureAsync("d7-decide-not-waiting");
        var runId = fixture.Running.Run.Id;
        var approval = (await CreateApprovalAsync(fixture)).Approval!;
        var waiting = await fixture.Runs.GetAsync("demo-a", "admin-a", runId, default);
        var lease = await fixture.Runs.ClaimLeaseAsync("demo-a", "admin-a", runId,
            new AgentRunLeaseRequest(waiting!.StateVersion, "other-worker", 120), default);
        Assert.Equal(AgentRunWriteStatus.Success,
            (await fixture.Runs.TransitionAsync("demo-a", "admin-a", runId,
                new AgentRunTransitionRequest(lease.Lease!.Run.StateVersion, AgentRunStatuses.Running,
                    lease.Lease.LeaseToken, lease.Lease.LeaseGeneration, lease.Lease.EventAckCursor), default)).Status);

        var decided = await fixture.Approvals.DecideAsync(
            "demo-a", "approver-a", "USER", runId, approval.Id, true, "not-waiting", null, default);

        Assert.Equal(AgentRunApprovalWriteStatus.InvalidState, decided.Status);
        Assert.Equal(approval.Id, decided.Approval!.Id);
        Assert.Equal("pending", decided.Approval.Status);
        Assert.Equal(AgentRunStatuses.Running,
            (await fixture.Runs.GetAsync("demo-a", "admin-a", runId, default))!.Status);
    }

    // 核准通過後 run 回到 queued,但 Workflow 還沒 claim 到 execute row,approval 就先過期。
    // Dapper 的 ReconcileTerminalExecutionsAsync(AgentRunApprovalRepository.cs:250)是三段 UPDATE:
    // execute→dead_letter、approval→expired、**run→failed/approval_expired**(WHERE r.status IN ('queued','running'))。
    // lite 只做前兩段的話,execute 已無人回收而 run 永遠停在 queued —— 缺陷 A 的第二個出口。
    // 兩條 claim 路徑(直接 claim / recovery 掃描)都必須終局化。
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClaimExecute_WhenApprovalExpiredBeforeTheWrite_DeadLettersExecutionAndFailsTheRun(bool viaRecovery)
    {
        var fixture = await FixtureAsync("d7-expired-before-write");
        var runId = fixture.Running.Run.Id;
        var approval = (await CreateApprovalAsync(fixture)).Approval!;
        Assert.Equal(AgentRunApprovalWriteStatus.Success,
            (await fixture.Approvals.DecideAsync("demo-a", "approver-a", "USER", runId, approval.Id, true, "approve", null, default)).Status);
        Assert.Equal(AgentRunStatuses.Queued, (await fixture.Runs.GetAsync("demo-a", "admin-a", runId, default))!.Status);
        fixture.Clock.Advance(TimeSpan.FromMinutes(6));

        if (viaRecovery)
        {
            Assert.Empty(await fixture.Approvals.ClaimExecuteRecoveryAsync(20, default));
        }
        else
        {
            Assert.Null(await fixture.Approvals.ClaimExecuteAsync("demo-a", runId, approval.Id, default));
        }

        var run = await fixture.Runs.GetAsync("demo-a", "admin-a", runId, default);
        Assert.Equal(AgentRunStatuses.Failed, run!.Status);
        Assert.Equal("approval_expired", run.ErrorCode);
        Assert.Equal("Approval expired before the write could execute.", run.ErrorMessage);
    }

    // 決定轉移是這個狀態機的必要接縫:少了它,核准無法終局化 run,而型別測試沒有 else 時
    // 對外仍是 Success(靜默 fail-open)。改成建構期一次性驗證,讓「未來換一個假 run 倉儲」
    // 直接爆在組裝階段,而不是在某條分支上悄悄失效。
    [Fact]
    public void Constructor_WithoutTheDecisionTransitionSeam_FailsFast()
    {
        var error = Assert.Throws<ArgumentException>(
            () => new InMemoryAgentRunApprovalRepository(new SeamlessAgentRuns()));

        Assert.Equal("agentRuns", error.ParamName);
    }

    /// <summary>只實作 <see cref="IAgentRunRepository"/>、刻意不提供決定轉移接縫的假倉儲。</summary>
    private sealed class SeamlessAgentRuns : IAgentRunRepository
    {
        public Task<AgentRunWriteResult> CreateDirectAsync(string a, string b, string c, IReadOnlyCollection<string> d, IReadOnlyCollection<string> e, Guid f, string g, string h, CancellationToken i) => throw new NotSupportedException();
        public Task<AgentRunResponse?> GetAsync(string a, string b, Guid c, CancellationToken d) => throw new NotSupportedException();
        public Task<string?> GetExecutionArtifactAsync(string a, string b, Guid c, CancellationToken d) => throw new NotSupportedException();
        public Task<AgentRunEventsResponse?> GetEventsAsync(string a, string b, Guid c, long d, int e, CancellationToken f) => throw new NotSupportedException();
        public Task<AgentRunWriteResult> ResumeAsync(string a, string b, Guid c, string d, long e, string f, CancellationToken g) => throw new NotSupportedException();
        public Task<AgentRunWriteResult> CancelAsync(string a, string b, Guid c, string? d, string e, CancellationToken f) => throw new NotSupportedException();
        public Task<AgentRunWriteResult> TransitionAsync(string a, string b, Guid c, AgentRunTransitionRequest d, CancellationToken e) => throw new NotSupportedException();
        public Task<AgentRunWriteResult> AppendEventsAsync(string a, string b, Guid c, AgentRunEventsAppendRequest d, CancellationToken e) => throw new NotSupportedException();
        public Task<AgentRunLeaseResult> ClaimLeaseAsync(string a, string b, Guid c, AgentRunLeaseRequest d, CancellationToken e) => throw new NotSupportedException();
        public Task<AgentRunCommandClaimResult> ClaimCommandAsync(string a, string b, Guid c, Guid d, AgentRunCommandClaimRequest e, CancellationToken f) => throw new NotSupportedException();
        public Task<AgentRunDispatchCompleteStatus> CompleteDispatchAsync(string a, string b, Guid c, Guid d, string e, CancellationToken f) => throw new NotSupportedException();
        public Task<AgentRunRecoveryClaimResponse> ClaimRecoveryAsync(AgentRunRecoveryClaimRequest a, CancellationToken b) => throw new NotSupportedException();
    }

    private sealed record ApprovalFixture(
        InMemoryAgentRunRepository Runs,
        InMemoryAgentRunApprovalRepository Approvals,
        (AgentRunResponse Run, string Token, long Generation) Running,
        ManualTimeProvider Clock);

    private static async Task<ApprovalFixture> FixtureAsync(string slug)
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 25, 0, 0, 0, TimeSpan.Zero));
        var (agents, skills, agent) = await PublishedAgentAsync(slug + "-" + Guid.NewGuid().ToString("N"));
        var runs = new InMemoryAgentRunRepository(agents, skills, clock);
        var approvals = new InMemoryAgentRunApprovalRepository(runs, clock);
        return new ApprovalFixture(runs, approvals, await StartRunningAsync(runs, agent.Id), clock);
    }

    /// <summary>核准後 run 回到 queued 且原 lease 已作廢:重新取得寫入 lease 並轉回 running(consume 的前置)。</summary>
    private static async Task<(string Token, long Generation)> ResumeRunningAsync(ApprovalFixture fixture)
    {
        var runId = fixture.Running.Run.Id;
        var queued = await fixture.Runs.GetAsync("demo-a", "admin-a", runId, default);
        var lease = await fixture.Runs.ClaimLeaseAsync("demo-a", "admin-a", runId,
            new AgentRunLeaseRequest(queued!.StateVersion, "write-worker", 120), default);
        var running = await fixture.Runs.TransitionAsync("demo-a", "admin-a", runId,
            new AgentRunTransitionRequest(lease.Lease!.Run.StateVersion, AgentRunStatuses.Running,
                lease.Lease.LeaseToken, lease.Lease.LeaseGeneration, lease.Lease.EventAckCursor), default);
        Assert.Equal(AgentRunWriteStatus.Success, running.Status);
        return (lease.Lease.LeaseToken, lease.Lease.LeaseGeneration);
    }

    private static Task<AgentRunApprovalWriteResult> CreateApprovalAsync(
        ApprovalFixture fixture, string? fingerprint = null)
        => fixture.Approvals.CreateAsync("demo-a", "admin-a", fixture.Running.Run.Id,
            new AgentRunApprovalCreateRequest(
                fixture.Running.Run.StateVersion, fixture.Running.Token, fixture.Running.Generation,
                Checkpoint(fixture.Running.Generation), 1, "USER", fingerprint ?? new string('a', 64),
                fixture.Clock.GetUtcNow().UtcDateTime.AddMinutes(5)), default);

    private static async Task<(AgentRunResponse Run, string Token, long Generation)> StartRunningAsync(InMemoryAgentRunRepository runs, Guid agentId, string key = "start")
    {
        var started = await runs.CreateDirectAsync("demo-a", "admin-a", "ADMIN", agentId, "write", key + Guid.NewGuid().ToString("N"), default);
        var lease = await runs.ClaimLeaseAsync("demo-a", "admin-a", started.Run!.Id,
            new AgentRunLeaseRequest(started.Run.StateVersion, "workflow", 120), default);
        var running = await runs.TransitionAsync("demo-a", "admin-a", started.Run.Id,
            new AgentRunTransitionRequest(lease.Lease!.Run.StateVersion, AgentRunStatuses.Running,
                lease.Lease.LeaseToken, lease.Lease.LeaseGeneration, lease.Lease.EventAckCursor), default);
        Assert.Equal(AgentRunWriteStatus.Success, running.Status);
        return (running.Run!, lease.Lease.LeaseToken, lease.Lease.LeaseGeneration);
    }

    private static async Task<(InMemoryAgentRepository Agents, InMemorySkillRepository Skills, Agent Agent)> PublishedAgentAsync(string slug)
    {
        var skills = new InMemorySkillRepository();
        var agents = new InMemoryAgentRepository(skills);
        var definition = AgentCanonicalizer.Canonicalize(new AgentUpsert(
            Slug: null, Name: null, Description: null, SystemPrompt: "test", ExecutionRoles: new[] { "worker" },
            Capabilities: null, OutputContract: null, Audience: new[] { "ADMIN" }, AllowedTools: Array.Empty<string>(),
            SkillBindings: null, KnowledgeSources: Array.Empty<string>(), BusinessRules: null,
            RuntimeLimits: new AgentRuntimeLimits(TimeoutSeconds: 3600, StepBudget: 4),
            RuntimeWorkflow: new AgentWorkflowRef(AgentDefaults.RuntimeWorkflowId, AgentDefaults.RuntimeWorkflowRevision)));
        var hash = SkillHash.Sha256(definition);
        var agent = await agents.CreateAsync("demo-a", slug, "D7", "test", definition, hash, "admin-a", default);
        Assert.NotNull(agent);
        Assert.True(await agents.MarkValidatedAsync("demo-a", agent!.Id, agent.DraftVersion, definition, hash, default));
        Assert.Equal(AgentWriteStatus.Success,
            (await agents.PublishAsync("demo-a", agent.Id, agent.DraftVersion, definition, hash, "admin-a", default)).Status);
        return (agents, skills, agent);
    }

    private static string Checkpoint(long generation) => $"v2:{generation}:{new string('d', 64)}:{Guid.NewGuid():D}";

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }
}
