using System.Threading;
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

    // O3 §4: "actionable" requires the exact pending waiting state and non-expiry, and this is
    // the one predicate the HTTP-level AgentRunApprovalQueueApiTests cannot exercise (it has no
    // clock control). Also proves queue/decide policy parity for the expired case specifically.
    [Fact]
    public async Task ListQueue_ExpiredApproval_IsVisibleButNeverActionable_AndDecideAgrees()
    {
        var fixture = await FixtureAsync("d7-queue-expired");
        var runId = fixture.Running.Run.Id;
        var approval = (await CreateApprovalAsync(fixture)).Approval!;

        var beforeExpiry = await fixture.Approvals.ListQueueAsync(
            "demo-a", "approver-a", "USER", actionableOnly: true, null, 20, default);
        Assert.Contains(beforeExpiry, item => item.ApprovalId == approval.Id && item.Actionable);

        fixture.Clock.Advance(TimeSpan.FromMinutes(6));

        var afterExpiry = await fixture.Approvals.ListQueueAsync(
            "demo-a", "approver-a", "USER", actionableOnly: true, null, 20, default);
        Assert.DoesNotContain(afterExpiry, item => item.ApprovalId == approval.Id);

        var visibleAfterExpiry = await fixture.Approvals.ListQueueAsync(
            "demo-a", "admin-a", "USER", actionableOnly: false, null, 20, default);
        var visible = Assert.Single(visibleAfterExpiry, item => item.ApprovalId == approval.Id);
        Assert.False(visible.Actionable);

        // Parity: what the queue excluded for expiry, DecideAsync independently rejects the same way.
        Assert.Equal(AgentRunApprovalWriteStatus.Expired,
            (await fixture.Approvals.DecideAsync("demo-a", "approver-a", "USER", runId, approval.Id, true, "expired-parity", null, default)).Status);
    }

    // O3 §4 "exact waiting state": once a pending approval is decided, it is no longer
    // actionable to anyone even though it has not expired and the role still matches.
    [Fact]
    public async Task ListQueue_OnlyExactPendingStatus_IsActionable()
    {
        var fixture = await FixtureAsync("d7-queue-waiting-state");
        var runId = fixture.Running.Run.Id;
        var approval = (await CreateApprovalAsync(fixture)).Approval!;
        Assert.Equal(AgentRunApprovalWriteStatus.Success,
            (await fixture.Approvals.DecideAsync("demo-a", "approver-a", "USER", runId, approval.Id, true, "approve", null, default)).Status);

        var actionable = await fixture.Approvals.ListQueueAsync(
            "demo-a", "approver-a", "USER", actionableOnly: true, null, 20, default);
        Assert.DoesNotContain(actionable, item => item.ApprovalId == approval.Id);

        var visible = await fixture.Approvals.ListQueueAsync(
            "demo-a", "admin-a", "USER", actionableOnly: false, null, 20, default);
        var listed = Assert.Single(visible);
        Assert.Equal("approved", listed.Status);
        Assert.False(listed.Actionable);
    }

    // 決策表的另一半:整份測試只走過 approve=true。拒絕必須把 run 終局化成 failed,而且
    // **不得**留下任何 execute row(被拒絕的寫入永遠不該發生),執行身分也不得外流給 Workflow。
    [Fact]
    public async Task Decide_Reject_FailsTheRunAndQueuesNoWrite()
    {
        var fixture = await FixtureAsync("d7-reject");
        var runId = fixture.Running.Run.Id;
        var approval = (await CreateApprovalAsync(fixture)).Approval!;

        var decided = await fixture.Approvals.DecideAsync(
            "demo-a", "approver-a", "USER", runId, approval.Id, false, "reject", "not authorised", default);

        Assert.Equal(AgentRunApprovalWriteStatus.Success, decided.Status);
        Assert.Equal("rejected", decided.Approval!.Status);
        Assert.Equal("rejected", decided.Approval.Decision);
        Assert.Equal("approver-a", decided.Approval.DecidedBy);
        Assert.Equal("not authorised", decided.Approval.Reason);
        Assert.Equal(AgentRunStatuses.Failed,
            (await fixture.Runs.GetAsync("demo-a", "admin-a", runId, default))!.Status);
        Assert.Null(await fixture.Approvals.ClaimExecuteAsync("demo-a", runId, approval.Id, default));
        Assert.Empty(await fixture.Approvals.ClaimExecuteRecoveryAsync(20, default));
        Assert.Null(await fixture.Approvals.GetExecutionIdentityAsync("demo-a", "approver-a", runId, approval.Id, default));
    }

    // 拒絕不是「比較安全所以可以放寬」的決定:過期 / 已取消 / 自我核准 / 角色不符這四道 fence
    // 對 approve=false 必須給出與 approve=true 完全相同的結果,而且一律不得產生 execute row。
    [Theory]
    [InlineData("expired", AgentRunApprovalWriteStatus.Expired, "expired")]
    [InlineData("cancelled", AgentRunApprovalWriteStatus.InvalidState, "cancelled")]
    [InlineData("self", AgentRunApprovalWriteStatus.Forbidden, "pending")]
    [InlineData("role", AgentRunApprovalWriteStatus.Forbidden, "pending")]
    public async Task Reject_IsFencedByExpiryCancellationAndSeparationOfDuties(
        string scenario, AgentRunApprovalWriteStatus expected, string expectedApprovalStatus)
    {
        var fixture = await FixtureAsync("d7-reject-fences");
        var runId = fixture.Running.Run.Id;
        var approval = (await CreateApprovalAsync(fixture)).Approval!;
        if (scenario == "expired") fixture.Clock.Advance(TimeSpan.FromMinutes(6));
        if (scenario == "cancelled") await fixture.Runs.CancelAsync("demo-a", "admin-a", runId, "stop", "cancel-reject", default);

        var decided = await fixture.Approvals.DecideAsync("demo-a",
            scenario == "self" ? "admin-a" : "approver-a",
            scenario == "role" ? "ADMIN" : "USER",
            runId, approval.Id, false, "reject-" + scenario, null, default);

        Assert.Equal(expected, decided.Status);
        Assert.Equal(expectedApprovalStatus, decided.Approval!.Status);
        Assert.Empty(await fixture.Approvals.ClaimExecuteRecoveryAsync(20, default));
    }

    // DecideAsync 自己的請求上限(idempotency key 128、reason 500)都是含上界:
    // on-point 必須放行、off-point 必須擋下,否則數字打錯沒有任何測試會紅。
    [Theory]
    [InlineData(0, 0, AgentRunApprovalWriteStatus.InvalidState)]      // 空白 idempotency key
    [InlineData(129, 0, AgentRunApprovalWriteStatus.InvalidState)]    // key 上限 128,129 即拒
    [InlineData(1, 501, AgentRunApprovalWriteStatus.InvalidState)]    // reason 上限 500,501 即拒
    [InlineData(128, 500, AgentRunApprovalWriteStatus.Success)]       // 兩個上界剛好都合法
    public async Task Decide_IdempotencyKeyAndReasonLengthBoundaries(
        int keyLength, int reasonLength, AgentRunApprovalWriteStatus expected)
    {
        var fixture = await FixtureAsync("d7-decide-limits");
        var runId = fixture.Running.Run.Id;
        var approval = (await CreateApprovalAsync(fixture)).Approval!;
        var reason = reasonLength == 0 ? null : new string('r', reasonLength);

        var decided = await fixture.Approvals.DecideAsync("demo-a", "approver-a", "USER", runId, approval.Id,
            true, new string('k', keyLength), reason, default);

        Assert.Equal(expected, decided.Status);
        if (expected == AgentRunApprovalWriteStatus.Success)
        {
            Assert.Equal(reason, decided.Approval!.Reason);
            return;
        }
        Assert.Equal("invalid decision request", decided.Message);
        // 壞請求必須擋在狀態機之外:同一個 approval 仍要能被正常決定。
        Assert.Equal(AgentRunApprovalWriteStatus.Success,
            (await fixture.Approvals.DecideAsync("demo-a", "approver-a", "USER", runId, approval.Id,
                true, "retry", null, default)).Status);
    }

    // 建立 approval 前的 durable gate:錯的 lease token / 舊 generation / 舊 expected_version 都必須擋在
    // 「產生任何 effect 之前」。這是 waiting_approval 這道門的入口 fence,漏掉會讓過期 worker 開出授權。
    [Theory]
    [InlineData("stale-token", 0, 0)]   // 錯 lease token
    [InlineData(null, 1, 0)]            // generation +1(不是目前這把 lease)
    [InlineData(null, 0, -1)]           // expected_version 落後一版
    [InlineData("stale-token", 1, -1)]  // 三道 fence 同時錯:組合起來也不得互相抵銷成放行
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

    // 同一道 guard 的另一半:fingerprint 格式合法,但 lease 身分根本沒帶(generation 下界 1、token 空白)。
    // 這是壞請求(InvalidState)而不是壞 lease(Conflict)—— 對 Workflow 的意義不同:改請求 vs 換 lease。
    [Theory]
    [InlineData(null, 0)]   // lease_generation 下界 1,0 即拒
    [InlineData("  ", 1)]   // lease_token 全空白
    public async Task Consume_MissingLeaseIdentity_IsInvalidStateNotConflict(string? leaseToken, long leaseGeneration)
    {
        var fixture = await FixtureAsync("d7-consume-lease-identity");
        var runId = fixture.Running.Run.Id;
        var fingerprint = new string('a', 64);
        var approval = (await CreateApprovalAsync(fixture, fingerprint)).Approval!;
        Assert.Equal(AgentRunApprovalWriteStatus.Success,
            (await fixture.Approvals.DecideAsync("demo-a", "approver-a", "USER", runId, approval.Id, true, "approve", null, default)).Status);

        var result = await fixture.Approvals.ConsumeAsync("demo-a", runId, approval.Id,
            new AgentRunApprovalConsumeRequest(fingerprint, leaseToken ?? fixture.Running.Token, leaseGeneration), default);

        Assert.Equal(AgentRunApprovalWriteStatus.InvalidState, result.Status);
        Assert.Equal("invalid consume request", result.Message);
        // 壞請求不得保留 effect:換上有效 lease 後同一 approval 仍可正常 consume。
        var write = await ResumeRunningAsync(fixture);
        Assert.Equal(AgentRunApprovalWriteStatus.Success,
            (await fixture.Approvals.ConsumeAsync("demo-a", runId, approval.Id,
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

    public static TheoryData<string, string?> RejectedWriteEvidenceRequests => new()
    {
        { "   ", "approved" },                  // record_id 全空白(Trim 後為空)
        { new string('r', 129), "approved" },   // record_id 上限 128,129 即拒
        { "refund-1", null },                   // value 必填:null 不合法(空字串才是合法的空值)
        { "refund-1", new string('v', 4_001) }, // value 上限 4000,4001 即拒
    };

    // WriteEvidenceAsync 自己的入口驗證:壞的 record_id/value 必須在碰到已保留的 effect 之前就擋下,
    // 否則一次打錯的寫入會把 once-only 的 effect 燒掉。上界內(128/4000)的寫入則必須放行。
    [Theory]
    [MemberData(nameof(RejectedWriteEvidenceRequests))]
    public async Task WriteEvidence_RejectsRecordIdAndValueOutOfRange_WithoutBurningTheEffect(
        string recordId, string? value)
    {
        var fixture = await FixtureAsync("d7-evidence-validation");
        var runId = fixture.Running.Run.Id;
        var fingerprint = new string('a', 64);
        var approval = (await CreateApprovalAsync(fixture, fingerprint)).Approval!;
        Assert.Equal(AgentRunApprovalWriteStatus.Success,
            (await fixture.Approvals.DecideAsync("demo-a", "approver-a", "USER", runId, approval.Id, true, "approve", null, default)).Status);
        var write = await ResumeRunningAsync(fixture);
        var effect = await fixture.Approvals.ConsumeAsync("demo-a", runId, approval.Id,
            new AgentRunApprovalConsumeRequest(fingerprint, write.Token, write.Generation), default);

        var rejected = await fixture.Approvals.WriteEvidenceAsync("demo-a", runId, effect.Response!.EffectId,
            new AgentRunWriteEvidenceRequest(recordId, value), default);

        Assert.Equal(AgentRunApprovalWriteStatus.InvalidState, rejected.Status);
        Assert.Equal("invalid write evidence", rejected.Message);
        var accepted = await fixture.Approvals.WriteEvidenceAsync("demo-a", runId, effect.Response.EffectId,
            new AgentRunWriteEvidenceRequest(new string('r', 128), new string('v', 4_000)), default);
        Assert.Equal(AgentRunApprovalWriteStatus.Success, accepted.Status);
        Assert.Equal("written", accepted.Response!.Outcome);
    }

    // F2 回歸測試:WriteEvidenceAsync 的 preflight 快照(liveRun.CancelRequested)與實際落地寫入之間
    // 原本是 check-then-act —— 中間沒有任何鎖護住。用實作 IAgentRunCancellationFence 的測試 fake,
    // 讓鎖內的原子重新檢查回報「已取消」,而 preflight 仍讀到真實、未取消的狀態,精準命中這個窗口
    // ——不靠 sleep/執行緒競速,也不需要生產碼後門。
    [Fact]
    public async Task WriteEvidence_RejectsWrite_WhenCancelCommitsAfterThePreflightSnapshot()
    {
        var (fixture, fence) = await FenceFixtureAsync("d7-evidence-race");
        var runId = fixture.Running.Run.Id;
        var fingerprint = new string('a', 64);
        var approval = (await CreateApprovalAsync(fixture, fingerprint)).Approval!;
        Assert.Equal(AgentRunApprovalWriteStatus.Success,
            (await fixture.Approvals.DecideAsync("demo-a", "approver-a", "USER", runId, approval.Id, true, "approve", null, default)).Status);
        var write = await ResumeRunningAsync(fixture);
        var effect = await fixture.Approvals.ConsumeAsync("demo-a", runId, approval.Id,
            new AgentRunApprovalConsumeRequest(fingerprint, write.Token, write.Generation), default);
        Assert.Equal(AgentRunApprovalWriteStatus.Success, effect.Status);

        // preflight 的 liveRun.CancelRequested 讀的是真實、未取消的 run;只有鎖內原子重新檢查
        // (fence.IsCancelRequested)被 fake 蓋成「已取消」,精準模擬 F2 修復前完全沒有鎖護住的那個窗口。
        fence.ForceCancelRequested = true;

        var result = await fixture.Approvals.WriteEvidenceAsync("demo-a", runId, effect.Response!.EffectId,
            new AgentRunWriteEvidenceRequest("must-not-write", "raced"), default);

        Assert.Equal(AgentRunApprovalWriteStatus.InvalidState, result.Status);
        Assert.Equal("run cancellation was requested", result.Message);

        // 沒有留下半截寫入:若 effect 真的被寫成 completed,同一 effect 用不同內容重試會回
        // Conflict("write effect payload changed");這裡預期仍是 InvalidState,證明 effect 沒被動過。
        var retry = await fixture.Approvals.WriteEvidenceAsync("demo-a", runId, effect.Response!.EffectId,
            new AgentRunWriteEvidenceRequest("different-record", "different-value"), default);
        Assert.Equal(AgentRunApprovalWriteStatus.InvalidState, retry.Status);
    }

    // ABBA 回歸測試:ConsumeAsync 的既有鎖序是 Approval._gate(外)→ AgentRun gate(內,
    // HasActiveApprovalLease 自鎖)。WriteEvidenceAsync 的鎖序必須與它一致,否則兩個併發請求
    // (即使是不同 run)會互卡且 lock 不吃 CancellationToken,永久掛住並耗盡 thread pool。
    // 用測試持有 AgentRun gate 逼 WriteEvidenceAsync 先卡在它的第一把鎖、空手等待,再讓
    // ConsumeAsync 拿到 Approval._gate 之後也卡在同一把 AgentRun gate 上,兩者同時互等時放手——
    // 鎖序一致就只是排隊,鎖序相反就是死循環等待。
    [Fact]
    public async Task ConsumeAndWriteEvidence_UnderLockContention_CompleteWithoutDeadlock()
    {
        var (fixture, fence) = await FenceFixtureAsync("d7-lock-order");
        var runId = fixture.Running.Run.Id;

        // Effect #1:已 reserved,交給併發的 WriteEvidenceAsync。
        var fingerprint1 = new string('a', 64);
        var approval1 = (await CreateApprovalAsync(fixture, fingerprint1)).Approval!;
        Assert.Equal(AgentRunApprovalWriteStatus.Success,
            (await fixture.Approvals.DecideAsync("demo-a", "approver-a", "USER", runId, approval1.Id, true, "approve-1", null, default)).Status);
        var write1 = await ResumeRunningAsync(fixture);
        var effect1 = await fixture.Approvals.ConsumeAsync("demo-a", runId, approval1.Id,
            new AgentRunApprovalConsumeRequest(fingerprint1, write1.Token, write1.Generation), default);
        Assert.Equal(AgentRunApprovalWriteStatus.Success, effect1.Status);

        // Approval #2:已核准但尚未 consume,帶著自己的新 write lease,交給併發的 ConsumeAsync。
        var fingerprint2 = new string('b', 64);
        // checkpoint_version ratchets monotonically for the whole run (not per lease generation):
        // approval #1's create already promoted it to 1, so approval #2 must advance to 2.
        var afterConsume = await fixture.Runs.GetAsync("demo-a", "admin-a", runId, default);
        var approval2Result = await fixture.Approvals.CreateAsync("demo-a", "admin-a", runId,
            new AgentRunApprovalCreateRequest(afterConsume!.StateVersion, write1.Token, write1.Generation,
                Checkpoint(write1.Generation), 2, "USER", fingerprint2, fixture.Clock.GetUtcNow().UtcDateTime.AddMinutes(5)), default);
        Assert.True(approval2Result.Status == AgentRunApprovalWriteStatus.Success, $"approval2 create failed: {approval2Result.Status} {approval2Result.Message}");
        var approval2 = approval2Result.Approval!;
        Assert.Equal(AgentRunApprovalWriteStatus.Success,
            (await fixture.Approvals.DecideAsync("demo-a", "approver-a", "USER", runId, approval2.Id, true, "approve-2", null, default)).Status);
        var write2 = await ResumeRunningAsync(fixture);

        var agentRunGate = fixture.Runs.ReferenceSyncRoot;
        var approvalGate = fixture.Approvals.ReferenceSyncRoot;

        // fence.SyncRoot 的求值(見 FakeCancellationFence)恰好落在 WriteEvidenceAsync 已取得
        // Approval._gate、尚未嘗試取得 AgentRun gate 的那一刻。用兩段式交握把它卡在那裡,讓測試能在
        // WriteEvidenceAsync 真正搶 AgentRun gate 之前先取得它 —— 更早取得會連 preflight 的
        // GetAsync(同一把 AgentRun gate,瞬間持有)都卡住,交握訊號永遠不會發出。
        var writeEvidenceReady = new ManualResetEventSlim();
        var writeEvidenceGo = new ManualResetEventSlim();
        fence.BeforeSyncRootAcquired = () =>
        {
            writeEvidenceReady.Set();
            writeEvidenceGo.Wait(TimeSpan.FromSeconds(5));
        };

        var writeTask = Task.Run(() => fixture.Approvals.WriteEvidenceAsync(
            "demo-a", runId, effect1.Response!.EffectId, new AgentRunWriteEvidenceRequest("record-1", "value-1"), default));
        Assert.True(writeEvidenceReady.Wait(TimeSpan.FromSeconds(5)), "WriteEvidenceAsync never reached its critical section");

        Task<(AgentRunApprovalWriteStatus Status, AgentRunApprovalConsumeResponse? Response, string? Message)>? consumeTask = null;

        // 這整段(Enter 到 Exit)刻意不含任何 await:Lock.Exit 必須在同一條
        // thread 上呼叫,await 之後的續行可能換一條 threadpool thread,會直接讓 Exit 拋例外。
        agentRunGate.Enter();
        try
        {
            // Release WriteEvidenceAsync; it will now take its first lock (Approval's own _gate --
            // uncontested) then try the AgentRun gate this test still holds, and block there.
            writeEvidenceGo.Set();
            // 鉤子放行之後,給那條路徑時間真的執行到第一道 lock 敘述式,把呼叫端排進
            // agentRunGate 的等待佇列(這條 thread 目前持有它)。
            Thread.Sleep(100);

            consumeTask = Task.Run(() => fixture.Approvals.ConsumeAsync("demo-a", runId, approval2.Id,
                new AgentRunApprovalConsumeRequest(fingerprint2, write2.Token, write2.Generation), default));
            // ConsumeAsync 立刻拿到 Approval._gate(沒人跟它搶),再卡進 HasActiveApprovalLease
            // 的 agentRunGate;輪詢直到能確認 Approval._gate 目前確實被別人持有。
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (approvalGate.TryEnter())
            {
                approvalGate.Exit();
                Assert.True(DateTime.UtcNow < deadline, "ConsumeAsync never acquired the Approval gate");
                Thread.Sleep(5);
            }
        }
        finally
        {
            agentRunGate.Exit();
        }

        var all = Task.WhenAll(writeTask!, consumeTask!);
        var winner = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(5)));
        fence.BeforeSyncRootAcquired = null;

        Assert.True(ReferenceEquals(winner, all), "ConsumeAsync/WriteEvidenceAsync deadlocked under concurrent lock contention");
        var writeResult = await writeTask!;
        var consumeResult = await consumeTask!;
        Assert.Equal(AgentRunApprovalWriteStatus.Success, writeResult.Status);
        Assert.Equal(AgentRunApprovalWriteStatus.Success, consumeResult.Status);
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

    // recovery 掃描的 limit 是 Math.Clamp(limit, 1, 100):上界決定一輪最多回收幾筆,
    // 下界 1 更關鍵 —— 少了它,limit=0 會讓 Take(0) 永遠掃不到任何待回收的已核准寫入。
    [Fact]
    public async Task ExecuteRecovery_ClampsLimitBetweenOneAndOneHundred()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 25, 0, 0, 0, TimeSpan.Zero));
        var (agents, skills, agent) = await PublishedAgentAsync("d7-recovery-limit");
        var runs = new InMemoryAgentRunRepository(agents, skills, clock);
        var approvals = new InMemoryAgentRunApprovalRepository(runs, clock);
        for (var i = 0; i < 101; i++)
        {
            var running = await StartRunningAsync(runs, agent.Id, "limit-" + i);
            var created = await approvals.CreateAsync("demo-a", "admin-a", running.Run.Id,
                new AgentRunApprovalCreateRequest(running.Run.StateVersion, running.Token, running.Generation,
                    Checkpoint(running.Generation), 1, "USER", new string('a', 64),
                    clock.GetUtcNow().UtcDateTime.AddMinutes(5)), default);
            Assert.Equal(AgentRunApprovalWriteStatus.Success,
                (await approvals.DecideAsync("demo-a", "approver-a", "USER", running.Run.Id,
                    created.Approval!.Id, true, "approve-" + i, null, default)).Status);
        }

        Assert.Equal(100, (await approvals.ClaimExecuteRecoveryAsync(101, default)).Count);
        // 那 100 筆的 claim 仍在有效期內,剩下的 1 筆必須被 limit=0 撿到(clamp 成 1),而不是 0 筆。
        Assert.Single(await approvals.ClaimExecuteRecoveryAsync(0, default));
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

    /// <summary>
    /// 裝飾一個真正的 <see cref="InMemoryAgentRunRepository"/>:除了 <see cref="IAgentRunCancellationFence"/>
    /// 這一個接縫可由測試控制,其餘一律轉發給底層真實倉儲,讓 setup(建立 run、lease、核准、
    /// consume...)行為與生產碼一致。取代已移除的生產碼測試後門鉤子。
    /// </summary>
    private sealed class FakeCancellationFence(InMemoryAgentRunRepository inner)
        : IAgentRunRepository, IAgentRunApprovalDecisionTransition, IAgentRunApprovalLeaseVerifier, IAgentRunCancellationFence
    {
        private readonly IAgentRunCancellationFence _fence = inner;

        /// <summary>鎖內原子重新檢查一律回報「已取消」,不理會底層真實狀態。</summary>
        public bool ForceCancelRequested;

        /// <summary>求值 <see cref="SyncRoot"/>(取得 AgentRun gate 之前)時呼叫一次,供測試卡時機用。</summary>
        public Action? BeforeSyncRootAcquired;

        public Lock SyncRoot
        {
            get
            {
                BeforeSyncRootAcquired?.Invoke();
                return _fence.SyncRoot;
            }
        }

        public bool IsCancelRequested(string tenantId, string userId, Guid runId)
            => ForceCancelRequested || _fence.IsCancelRequested(tenantId, userId, runId);

        public Task<AgentRunWriteResult> CreateDirectAsync(string a, string b, string c, IReadOnlyCollection<string> d, IReadOnlyCollection<string> e, Guid f, string g, string h, CancellationToken i) => inner.CreateDirectAsync(a, b, c, d, e, f, g, h, i);
        public Task<AgentRunResponse?> GetAsync(string a, string b, Guid c, CancellationToken d) => inner.GetAsync(a, b, c, d);
        public Task<string?> GetExecutionArtifactAsync(string a, string b, Guid c, CancellationToken d) => inner.GetExecutionArtifactAsync(a, b, c, d);
        public Task<AgentRunEventsResponse?> GetEventsAsync(string a, string b, Guid c, long d, int e, CancellationToken f) => inner.GetEventsAsync(a, b, c, d, e, f);
        public Task<AgentRunWriteResult> ResumeAsync(string a, string b, Guid c, string d, long e, string f, CancellationToken g) => inner.ResumeAsync(a, b, c, d, e, f, g);
        public Task<AgentRunWriteResult> CancelAsync(string a, string b, Guid c, string? d, string e, CancellationToken f) => inner.CancelAsync(a, b, c, d, e, f);
        public Task<AgentRunWriteResult> TransitionAsync(string a, string b, Guid c, AgentRunTransitionRequest d, CancellationToken e) => inner.TransitionAsync(a, b, c, d, e);
        public Task<AgentRunWriteResult> AppendEventsAsync(string a, string b, Guid c, AgentRunEventsAppendRequest d, CancellationToken e) => inner.AppendEventsAsync(a, b, c, d, e);
        public Task<AgentRunLeaseResult> ClaimLeaseAsync(string a, string b, Guid c, AgentRunLeaseRequest d, CancellationToken e) => inner.ClaimLeaseAsync(a, b, c, d, e);
        public Task<AgentRunCommandClaimResult> ClaimCommandAsync(string a, string b, Guid c, Guid d, AgentRunCommandClaimRequest e, CancellationToken f) => inner.ClaimCommandAsync(a, b, c, d, e, f);
        public Task<AgentRunDispatchCompleteStatus> CompleteDispatchAsync(string a, string b, Guid c, Guid d, string e, CancellationToken f) => inner.CompleteDispatchAsync(a, b, c, d, e, f);
        public Task<AgentRunRecoveryClaimResponse> ClaimRecoveryAsync(AgentRunRecoveryClaimRequest a, CancellationToken b) => inner.ClaimRecoveryAsync(a, b);
        public Task<AgentRunWriteResult> ResolveApprovalAsync(string a, string b, Guid c, long d, bool e, CancellationToken f) => inner.ResolveApprovalAsync(a, b, c, d, e, f);
        public Task FailApprovalRunAsync(string a, string b, Guid c, AgentRunApprovalFailure d, CancellationToken e) => inner.FailApprovalRunAsync(a, b, c, d, e);
        public bool HasActiveApprovalLease(string a, string b, Guid c, string d, long e) => inner.HasActiveApprovalLease(a, b, c, d, e);
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

    /// <summary>
    /// 同 <see cref="FixtureAsync"/>,但 Approvals 的 <c>IAgentRunRepository</c> 是
    /// <see cref="FakeCancellationFence"/>,讓測試能控制 D7 write-evidence 的取消重新檢查時機,
    /// 不需要生產碼測試後門。
    /// </summary>
    private static async Task<(ApprovalFixture Fixture, FakeCancellationFence Fence)> FenceFixtureAsync(string slug)
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 25, 0, 0, 0, TimeSpan.Zero));
        var (agents, skills, agent) = await PublishedAgentAsync(slug + "-" + Guid.NewGuid().ToString("N"));
        var runs = new InMemoryAgentRunRepository(agents, skills, clock);
        var fence = new FakeCancellationFence(runs);
        var approvals = new InMemoryAgentRunApprovalRepository(fence, clock);
        var fixture = new ApprovalFixture(runs, approvals, await StartRunningAsync(runs, agent.Id), clock);
        return (fixture, fence);
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
