import { expect, test } from 'vitest'
import { normalizeMetrics, recordRegression, sumOrUnknown } from '../src/api/operations'

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

const EXPECTED = {
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
})
