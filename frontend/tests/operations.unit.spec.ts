import { expect, test } from 'vitest'
import {
  createEvalRun,
  diffEvalRuns,
  getVersionComparison,
  normalizeEvalRun,
  normalizeEvalSuiteDetail,
  normalizeMetrics,
  recordRegression,
  sumOrUnknown,
} from '../src/api/operations'
import { ApiError, isNotFound } from '../src/api/http'
import type { EvalRun } from '../src/types'

const CAMEL_CASE_PAYLOAD = {
  releaseGate: { regressionPassed: true, overrideActive: false, auditEntries: 3 },
  multiAgent: {
    rolloutEvents: 2,
    rootRuns: 5,
    childRuns: 10,
    childSuccess: 9,
    verifierReject: 1,
    repairRounds: 2,
    writeEffects: 4,
    agents: [
      {
        agentId: 'agent-1', revision: 2, runs: 10, completed: 8, failed: 2,
        averageLatencyMs: 120, reservedBudgetUnits: 50,
        observedUsageUnits: 30, observedCostUnits: 1.5, observedLatencyMs: 110,
      },
    ],
    skills: [
      {
        name: 'skill-1', revision: 1, runs: 4,
        observedLatencyMs: 50, observedUsageUnits: 12, observedCostUnits: 0.5,
        reservedBudgetUnits: 20,
      },
    ],
    tools: [
      {
        kind: 'http', count: 6,
        observedLatencyMs: 30, observedUsageUnits: 6, observedCostUnits: 0.2,
        reservedBudgetUnits: 10,
      },
    ],
    nodes: [{ nodeId: 'node-1', executions: 7, averageLatencyMs: 40, maxLatencyMs: 90 }],
    aggregation: { completed: 4, partialOrFailed: 1, averageFanOut: 2, averageLatencyMs: 300 },
  },
}

const SNAKE_CASE_PAYLOAD = {
  release_gate: { regression_passed: true, override_active: false, audit_entries: 3 },
  multi_agent: {
    rollout_events: 2,
    root_runs: 5,
    child_runs: 10,
    child_success: 9,
    verifier_reject: 1,
    repair_rounds: 2,
    write_effects: 4,
    agents: [
      {
        agent_id: 'agent-1', revision: 2, runs: 10, completed: 8, failed: 2,
        average_latency_ms: 120, reserved_budget_units: 50,
        observed_usage_units: 30, observed_cost_units: 1.5, observed_latency_ms: 110,
      },
    ],
    skills: [
      {
        name: 'skill-1', revision: 1, runs: 4,
        observed_latency_ms: 50, observed_usage_units: 12, observed_cost_units: 0.5,
        reserved_budget_units: 20,
      },
    ],
    tools: [
      {
        kind: 'http', count: 6,
        observed_latency_ms: 30, observed_usage_units: 6, observed_cost_units: 0.2,
        reserved_budget_units: 10,
      },
    ],
    nodes: [{ node_id: 'node-1', executions: 7, average_latency_ms: 40, max_latency_ms: 90 }],
    aggregation: { completed: 4, partial_or_failed: 1, average_fan_out: 2, average_latency_ms: 300 },
  },
}

// 實際 wire 形狀(混用命名):backend OperationsGovernanceController 的 metrics 匿名物件
// 顯式寫 snake_case 外層鍵(release_gate / multi_agent / rollout_events…),而 agents /
// skills / tools / nodes / aggregation 是直接序列化 C# record,走預設 camelCase。
const MIXED_CASE_PAYLOAD = {
  release_gate: { regression_passed: true, override_active: false, audit_entries: 3 },
  multi_agent: {
    rollout_events: 2,
    root_runs: 5,
    child_runs: 10,
    child_success: 9,
    verifier_reject: 1,
    repair_rounds: 2,
    write_effects: 4,
    agents: [
      {
        agentId: 'agent-1', revision: 2, runs: 10, completed: 8, failed: 2,
        averageLatencyMs: 120, reservedBudgetUnits: 50,
        observedUsageUnits: 30, observedCostUnits: 1.5, observedLatencyMs: 110,
      },
    ],
    skills: [
      {
        name: 'skill-1', revision: 1, runs: 4,
        observedLatencyMs: 50, observedUsageUnits: 12, observedCostUnits: 0.5,
        reservedBudgetUnits: 20,
      },
    ],
    tools: [
      {
        kind: 'http', count: 6,
        observedLatencyMs: 30, observedUsageUnits: 6, observedCostUnits: 0.2,
        reservedBudgetUnits: 10,
      },
    ],
    nodes: [{ nodeId: 'node-1', executions: 7, averageLatencyMs: 40, maxLatencyMs: 90 }],
    aggregation: { completed: 4, partialOrFailed: 1, averageFanOut: 2, averageLatencyMs: 300 },
  },
}

const EXPECTED = {
  // W2-02(e):三份 payload 都沒帶 window_days(尚未部署新後端),正規化後為 null,由畫面退回預設文案。
  windowDays: null,
  releaseGate: { regressionPassed: true, overrideActive: false, auditEntries: 3 },
  rolloutEvents: 2,
  rootRuns: 5,
  childRuns: 10,
  childSuccess: 9,
  verifierReject: 1,
  repairRounds: 2,
  writeEffects: 4,
  agents: [
    {
      agentId: 'agent-1', revision: 2, runs: 10, completed: 8, failed: 2,
      averageLatencyMs: 120, reservedBudgetUnits: 50,
      observedUsageUnits: 30, observedCostUnits: 1.5, observedLatencyMs: 110,
    },
  ],
  skills: [
    {
      name: 'skill-1', revision: 1, runs: 4,
      observedLatencyMs: 50, observedUsageUnits: 12, observedCostUnits: 0.5,
      reservedBudgetUnits: 20,
    },
  ],
  tools: [
    {
      kind: 'http', count: 6,
      observedLatencyMs: 30, observedUsageUnits: 6, observedCostUnits: 0.2,
      reservedBudgetUnits: 10,
    },
  ],
  nodes: [{ nodeId: 'node-1', executions: 7, averageLatencyMs: 40, maxLatencyMs: 90 }],
  aggregation: { completed: 4, partialOrFailed: 1, averageFanOut: 2, averageLatencyMs: 300 },
}

test.describe('O1 operations metrics: unknown never renders as 0', () => {
  test('normalizes a fully camelCase payload', () => {
    expect(normalizeMetrics(CAMEL_CASE_PAYLOAD)).toEqual(EXPECTED)
  })

  test('normalizes an equivalent fully snake_case payload identically', () => {
    expect(normalizeMetrics(SNAKE_CASE_PAYLOAD)).toEqual(EXPECTED)
  })

  test('normalizes the real mixed-case wire shape (snake outer keys + camelCase record keys)', () => {
    expect(normalizeMetrics(MIXED_CASE_PAYLOAD)).toEqual(EXPECTED)
  })

  test('defaults every scalar to 0 and every list to empty for an absent payload', () => {
    const zeroed = {
      windowDays: null,
      releaseGate: { regressionPassed: false, overrideActive: false, auditEntries: 0 },
      rolloutEvents: 0,
      rootRuns: 0,
      childRuns: 0,
      childSuccess: 0,
      verifierReject: 0,
      repairRounds: 0,
      writeEffects: 0,
      agents: [],
      skills: [],
      tools: [],
      nodes: [],
      aggregation: { completed: 0, partialOrFailed: 0, averageFanOut: 0, averageLatencyMs: 0 },
    }
    expect(normalizeMetrics({})).toEqual(zeroed)
    expect(normalizeMetrics(undefined)).toEqual(zeroed)
    // 空集合的和是 0(確定值),與「任一分量未知 → null」是不同語意。
    expect(sumOrUnknown([])).toBe(0)
  })

  // W2-02(e):統計區間必須跟著數字一起回來,兩種命名、兩種擺放位置都要讀得到;
  // 欄位缺席(舊後端)不得壞掉,而是回 null 讓畫面顯示預設區間文案。
  test('reads the W2-02 statistics window from either casing and either nesting level', () => {
    expect(normalizeMetrics({ window_days: 30 }).windowDays).toBe(30)
    expect(normalizeMetrics({ windowDays: 7 }).windowDays).toBe(7)
    expect(normalizeMetrics({ multi_agent: { window_days: 14 } }).windowDays).toBe(14)
    expect(normalizeMetrics({ window_days: 30, multi_agent: { window_days: 14 } }).windowDays).toBe(30)
    expect(normalizeMetrics({ window_days: 'ninety' }).windowDays).toBeNull()
  })

  test('never substitutes a partial sum when any observed value is unknown', () => {
    const metrics = normalizeMetrics({
      multiAgent: {
        agents: [
          { agentId: 'agent-1', observedUsageUnits: 10 },
          { agentId: 'agent-2', observedUsageUnits: null },
        ],
      },
    })
    expect(metrics.agents.map((a) => a.observedUsageUnits)).toEqual([10, null])
    expect(sumOrUnknown(metrics.agents.map((a) => a.observedUsageUnits))).toBeNull()
    expect(sumOrUnknown([10, 5, 3])).toBe(18)
  })

  test('recordRegression only sends eval_run_id (E3 trusted path) when one is given', async () => {
    const originalFetch = globalThis.fetch
    const bodies: unknown[] = []
    globalThis.fetch = async (_input, init) => {
      bodies.push(init?.body ? JSON.parse(String(init.body)) : undefined)
      return new Response(JSON.stringify({}), { status: 200, headers: { 'Content-Type': 'application/json' } })
    }

    try {
      await recordRegression('suite-a', true, 'evidence-a')
      await recordRegression('suite-b', false, 'evidence-b', '3fa85f64-5717-4562-b3fc-2c963f66afa6')
    } finally {
      globalThis.fetch = originalFetch
    }

    expect(bodies).toEqual([
      { suite: 'suite-a', passed: true, evidence_ref: 'evidence-a' },
      {
        suite: 'suite-b',
        passed: false,
        evidence_ref: 'evidence-b',
        eval_run_id: '3fa85f64-5717-4562-b3fc-2c963f66afa6',
      },
    ])
  })

  test('getVersionComparison normalizes both the empty gate and a populated previous-revision delta', async () => {
    const originalFetch = globalThis.fetch
    const payloads: unknown[] = [
      {
        selected_revision: null,
        rollout_events: 0,
        new_roots_only: false,
        active_runs_keep_immutable_snapshot: false,
        revisions: [],
        selected_vs_previous: null,
      },
      {
        // 同樣的混用命名:外層鍵 snake_case,revisions/selected_vs_previous 是 C# record camelCase。
        window_days: 90,
        selected_revision: 3,
        rollout_events: 4,
        new_roots_only: true,
        active_runs_keep_immutable_snapshot: true,
        revisions: [
          {
            revision: 3, runs: 12, completed: 10, failed: 2,
            averageLatencyMs: 210, reservedBudgetUnits: 80, activeRuns: 1,
          },
        ],
        selected_vs_previous: {
          fromRevision: 2, toRevision: 3, runDelta: 5, completedDelta: 4,
          averageLatencyDeltaMs: -30, reservedBudgetDeltaUnits: 10,
        },
      },
    ]
    globalThis.fetch = async () =>
      new Response(JSON.stringify(payloads.shift()), { status: 200, headers: { 'Content-Type': 'application/json' } })

    try {
      expect(await getVersionComparison()).toEqual({
        windowDays: null,
        selectedRevision: null,
        rolloutEvents: 0,
        newRootsOnly: false,
        activeRunsKeepImmutableSnapshot: false,
        revisions: [],
        selectedVsPrevious: null,
      })
      expect(await getVersionComparison()).toEqual({
        windowDays: 90,
        selectedRevision: 3,
        rolloutEvents: 4,
        newRootsOnly: true,
        activeRunsKeepImmutableSnapshot: true,
        revisions: [
          {
            revision: 3, runs: 12, completed: 10, failed: 2,
            averageLatencyMs: 210, reservedBudgetUnits: 80, activeRuns: 1,
          },
        ],
        selectedVsPrevious: {
          fromRevision: 2, toRevision: 3, runDelta: 5, completedDelta: 4,
          averageLatencyDeltaMs: -30, reservedBudgetDeltaUnits: 10,
        },
      })
    } finally {
      globalThis.fetch = originalFetch
    }
  })
})

const EVAL_SUITE_DETAIL_CAMEL = {
  suiteId: 'CSR-EVAL-001',
  currentRevision: 2,
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-02-01T00:00:00Z',
  revisions: [
    { revision: 2, casesSha256: 'abc123def456abc123def456', caseCount: 6, createdBy: 'admin-a', createdAt: '2026-02-01T00:00:00Z' },
    { revision: 1, casesSha256: 'aaa111bbb222aaa111bbb222', caseCount: 5, createdBy: 'admin-a', createdAt: '2026-01-01T00:00:00Z' },
  ],
}

const EVAL_SUITE_DETAIL_SNAKE = {
  suite_id: 'CSR-EVAL-001',
  current_revision: 2,
  created_at: '2026-01-01T00:00:00Z',
  updated_at: '2026-02-01T00:00:00Z',
  revisions: [
    { revision: 2, cases_sha256: 'abc123def456abc123def456', case_count: 6, created_by: 'admin-a', created_at: '2026-02-01T00:00:00Z' },
    { revision: 1, cases_sha256: 'aaa111bbb222aaa111bbb222', case_count: 5, created_by: 'admin-a', created_at: '2026-01-01T00:00:00Z' },
  ],
}

const EVAL_SUITE_DETAIL_EXPECTED = {
  suiteId: 'CSR-EVAL-001',
  currentRevision: 2,
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-02-01T00:00:00Z',
  revisions: [
    { revision: 2, casesSha256: 'abc123def456abc123def456', caseCount: 6, createdBy: 'admin-a', createdAt: '2026-02-01T00:00:00Z' },
    { revision: 1, casesSha256: 'aaa111bbb222aaa111bbb222', caseCount: 5, createdBy: 'admin-a', createdAt: '2026-01-01T00:00:00Z' },
  ],
}

const EVAL_RUN_CAMEL = {
  id: '3fa85f64-5717-4562-b3fc-2c963f66afa6',
  suiteId: 'CSR-EVAL-001',
  suiteRevision: 2,
  candidate: { kind: 'skill', ref: { name: 'kb-query' }, identitySha256: 'deadbeefdeadbeef' },
  runnerVersion: 'e2-runner-1',
  startedAt: '2026-07-30T00:00:00Z',
  completedAt: '2026-07-30T00:00:05Z',
  passCount: 4,
  failCount: 1,
  errorCount: 0,
  cases: [
    { caseId: 'case-1', canonicalIdentity: 'identity-1', verdict: 'PASS', metrics: { latencyMs: 120 }, failureReason: null },
    { caseId: 'case-2', canonicalIdentity: 'identity-2', verdict: 'FAIL', metrics: { latencyMs: 80 }, failureReason: 'mismatch' },
  ],
}

const EVAL_RUN_SNAKE = {
  id: '3fa85f64-5717-4562-b3fc-2c963f66afa6',
  suite_id: 'CSR-EVAL-001',
  suite_revision: 2,
  candidate: { kind: 'skill', ref: { name: 'kb-query' }, identity_sha256: 'deadbeefdeadbeef' },
  runner_version: 'e2-runner-1',
  started_at: '2026-07-30T00:00:00Z',
  completed_at: '2026-07-30T00:00:05Z',
  pass_count: 4,
  fail_count: 1,
  error_count: 0,
  cases: [
    { case_id: 'case-1', canonical_identity: 'identity-1', verdict: 'PASS', metrics: { latency_ms: 120 }, failure_reason: null },
    { case_id: 'case-2', canonical_identity: 'identity-2', verdict: 'FAIL', metrics: { latency_ms: 80 }, failure_reason: 'mismatch' },
  ],
}

const EVAL_RUN_EXPECTED = {
  id: '3fa85f64-5717-4562-b3fc-2c963f66afa6',
  suiteId: 'CSR-EVAL-001',
  suiteRevision: 2,
  candidate: { kind: 'skill', identitySha256: 'deadbeefdeadbeef' },
  runnerVersion: 'e2-runner-1',
  startedAt: '2026-07-30T00:00:00Z',
  completedAt: '2026-07-30T00:00:05Z',
  passCount: 4,
  failCount: 1,
  errorCount: 0,
  cases: [
    { caseId: 'case-1', canonicalIdentity: 'identity-1', verdict: 'PASS', latencyMs: 120, failureReason: null },
    { caseId: 'case-2', canonicalIdentity: 'identity-2', verdict: 'FAIL', latencyMs: 80, failureReason: 'mismatch' },
  ],
}

test.describe('E4 eval suite/run normalizers: camel/snake dual-form parity', () => {
  test('normalizeEvalSuiteDetail normalizes camelCase and snake_case identically', () => {
    expect(normalizeEvalSuiteDetail(EVAL_SUITE_DETAIL_CAMEL)).toEqual(EVAL_SUITE_DETAIL_EXPECTED)
    expect(normalizeEvalSuiteDetail(EVAL_SUITE_DETAIL_SNAKE)).toEqual(EVAL_SUITE_DETAIL_EXPECTED)
  })

  test('normalizeEvalRun normalizes camelCase and snake_case identically, including nested cases', () => {
    expect(normalizeEvalRun(EVAL_RUN_CAMEL)).toEqual(EVAL_RUN_EXPECTED)
    expect(normalizeEvalRun(EVAL_RUN_SNAKE)).toEqual(EVAL_RUN_EXPECTED)
  })

  test('normalizeEvalRun leaves cases null for a list-summary payload (no cases field)', () => {
    const run = normalizeEvalRun({ id: 'r1', suite_id: 's', suite_revision: 1, candidate: { kind: 'skill' } })
    expect(run.cases).toBeNull()
  })

  test('createEvalRun sends Idempotency-Key header and omits budget_ms when not given', async () => {
    const originalFetch = globalThis.fetch
    const requests: Array<{ body: unknown; headers: Record<string, string> }> = []
    globalThis.fetch = async (_input, init) => {
      requests.push({
        body: init?.body ? JSON.parse(String(init.body)) : undefined,
        headers: Object.fromEntries(new Headers(init?.headers).entries()),
      })
      return new Response(JSON.stringify(EVAL_RUN_SNAKE), { status: 200, headers: { 'Content-Type': 'application/json' } })
    }

    try {
      await createEvalRun('CSR-EVAL-001', 2, { kind: 'skill', ref: { name: 'kb-query' } }, 'idem-key-1')
      await createEvalRun('CSR-EVAL-001', 2, { kind: 'skill', ref: { name: 'kb-query' } }, 'idem-key-2', 5000)
    } finally {
      globalThis.fetch = originalFetch
    }

    expect(requests[0].body).toEqual({
      suite_id: 'CSR-EVAL-001',
      revision: 2,
      candidate: { kind: 'skill', ref: { name: 'kb-query' } },
    })
    expect(requests[0].headers['idempotency-key']).toBe('idem-key-1')
    expect(requests[1].body).toEqual({
      suite_id: 'CSR-EVAL-001',
      revision: 2,
      candidate: { kind: 'skill', ref: { name: 'kb-query' } },
      budget_ms: 5000,
    })
  })

  // 邊界:budgetMs 用 truthy 判斷,所以顯式傳 0 和完全不傳送出去的 body 一模一樣
  // (0 被丟掉,不會送 budget_ms: 0)。這是現行行為的定性測試。
  test('createEvalRun drops an explicit budget_ms of 0 exactly like an omitted budget', async () => {
    const originalFetch = globalThis.fetch
    const bodies: unknown[] = []
    globalThis.fetch = async (_input, init) => {
      bodies.push(init?.body ? JSON.parse(String(init.body)) : undefined)
      return new Response(JSON.stringify(EVAL_RUN_SNAKE), { status: 200, headers: { 'Content-Type': 'application/json' } })
    }

    try {
      await createEvalRun('CSR-EVAL-001', 2, { kind: 'skill', ref: { name: 'kb-query' } }, 'idem-key-3', 0)
    } finally {
      globalThis.fetch = originalFetch
    }

    expect(bodies).toEqual([
      { suite_id: 'CSR-EVAL-001', revision: 2, candidate: { kind: 'skill', ref: { name: 'kb-query' } } },
    ])
  })
})

test.describe('E4 eval baseline/candidate delta: pure client-side per-case diff', () => {
  function run(id: string, suiteId: string, cases: EvalRun['cases']): EvalRun {
    return {
      id,
      suiteId,
      suiteRevision: 1,
      candidate: { kind: 'skill', identitySha256: 'x' },
      runnerVersion: 'v1',
      startedAt: null,
      completedAt: null,
      passCount: 0,
      failCount: 0,
      errorCount: 0,
      cases,
    }
  }

  test('flags a PASS→FAIL case as regressed and a FAIL→PASS case as improved', () => {
    const baseline = run('baseline', 'S', [
      { caseId: 'case-1', canonicalIdentity: null, verdict: 'PASS', latencyMs: 10, failureReason: null },
      { caseId: 'case-2', canonicalIdentity: null, verdict: 'FAIL', latencyMs: 10, failureReason: 'x' },
    ])
    const candidate = run('candidate', 'S', [
      { caseId: 'case-1', canonicalIdentity: null, verdict: 'FAIL', latencyMs: 12, failureReason: 'broke' },
      { caseId: 'case-2', canonicalIdentity: null, verdict: 'PASS', latencyMs: 9, failureReason: null },
    ])

    const delta = diffEvalRuns(baseline, candidate)

    expect(delta).toEqual([
      { caseId: 'case-1', baselineVerdict: 'PASS', candidateVerdict: 'FAIL', status: 'regressed' },
      { caseId: 'case-2', baselineVerdict: 'FAIL', candidateVerdict: 'PASS', status: 'improved' },
    ])
  })

  test('flags cases only present on one side as added/removed, and identical verdicts as unchanged', () => {
    const baseline = run('baseline', 'S', [
      { caseId: 'case-1', canonicalIdentity: null, verdict: 'PASS', latencyMs: 10, failureReason: null },
      { caseId: 'case-removed', canonicalIdentity: null, verdict: 'PASS', latencyMs: 10, failureReason: null },
    ])
    const candidate = run('candidate', 'S', [
      { caseId: 'case-1', canonicalIdentity: null, verdict: 'PASS', latencyMs: 11, failureReason: null },
      { caseId: 'case-added', canonicalIdentity: null, verdict: 'PASS', latencyMs: 8, failureReason: null },
    ])

    const delta = diffEvalRuns(baseline, candidate)

    expect(delta).toEqual([
      { caseId: 'case-1', baselineVerdict: 'PASS', candidateVerdict: 'PASS', status: 'unchanged' },
      { caseId: 'case-added', baselineVerdict: null, candidateVerdict: 'PASS', status: 'added' },
      { caseId: 'case-removed', baselineVerdict: 'PASS', candidateVerdict: null, status: 'removed' },
    ])
  })

  // Decision-table tail: only a PASS<->non-PASS flip is regressed/improved; any other verdict
  // change (e.g. a non-PASS ERROR verdict, which the runner uses for infra/timeout failures
  // distinct from an assertion FAIL) is just 'changed'.
  test('flags a FAIL→ERROR case as changed, PASS→ERROR as regressed, and ERROR→PASS as improved', () => {
    const baseline = run('baseline', 'S', [
      { caseId: 'case-fail-error', canonicalIdentity: null, verdict: 'FAIL', latencyMs: 10, failureReason: 'x' },
      { caseId: 'case-pass-error', canonicalIdentity: null, verdict: 'PASS', latencyMs: 10, failureReason: null },
      { caseId: 'case-error-pass', canonicalIdentity: null, verdict: 'ERROR', latencyMs: null, failureReason: 'timeout' },
    ])
    const candidate = run('candidate', 'S', [
      { caseId: 'case-fail-error', canonicalIdentity: null, verdict: 'ERROR', latencyMs: null, failureReason: 'timeout' },
      { caseId: 'case-pass-error', canonicalIdentity: null, verdict: 'ERROR', latencyMs: null, failureReason: 'timeout' },
      { caseId: 'case-error-pass', canonicalIdentity: null, verdict: 'PASS', latencyMs: 9, failureReason: null },
    ])

    const delta = diffEvalRuns(baseline, candidate)

    expect(delta).toEqual([
      { caseId: 'case-error-pass', baselineVerdict: 'ERROR', candidateVerdict: 'PASS', status: 'improved' },
      { caseId: 'case-fail-error', baselineVerdict: 'FAIL', candidateVerdict: 'ERROR', status: 'changed' },
      { caseId: 'case-pass-error', baselineVerdict: 'PASS', candidateVerdict: 'ERROR', status: 'regressed' },
    ])
  })
})

test('isNotFound only matches a 404 ApiError, not other statuses or plain errors', () => {
  expect(isNotFound(new ApiError(404, 'not found'))).toBe(true)
  expect(isNotFound(new ApiError(500, 'boom'))).toBe(false)
  expect(isNotFound(new Error('not found'))).toBe(false)
})
