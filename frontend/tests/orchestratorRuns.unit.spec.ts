import { expect, test } from 'vitest'
import {
  cancelOrchestratorRun,
  getOrchestratorRun,
  getOrchestratorRunEvents,
  startOrchestratorRun,
} from '../src/api/orchestratorRuns'

interface RecordedCall {
  url: string
  method: string
  idempotencyKey: string | null
  body: string | null
}

/** Records every call and replays `responses` in order; `restore()` belongs in a finally. */
function stubFetch(responses: unknown[]): { calls: RecordedCall[]; restore: () => void } {
  const original = globalThis.fetch
  const calls: RecordedCall[] = []
  globalThis.fetch = (async (url: string, init?: RequestInit) => {
    calls.push({
      url,
      method: init?.method ?? 'GET',
      idempotencyKey: new Headers(init?.headers).get('Idempotency-Key'),
      body: init?.body == null ? null : String(init.body),
    })
    return new Response(JSON.stringify(responses.shift()), { headers: { 'content-type': 'application/json' } })
  }) as typeof fetch
  return { calls, restore: () => { globalThis.fetch = original } }
}

test('D5 root run API uses immutable command key and cursor polling', async () => {
  const { calls, restore } = stubFetch([
    { id: 'root-run', status: 'queued', workflow_revision: 3, budgets: { maxTasks: 2 } },
    { run_id: 'root-run', events: [{ sequence: 2, event_type: 'worker_completed' }], next_sequence: 2 },
    { id: 'root-run', status: 'running', cancel_requested_at: '2026-07-25T00:00:00Z' },
  ])

  try {
    // Ids are URL-encoded, never concatenated raw: the start route is the workflow.manage-only
    // SYSTEM_ADMIN path and a wrong/injectable path must fail here, not in production.
    const started = await startOrchestratorRun('root/1', 'hello', 'conversation/1', 'key-1')
    // A negative cursor is clamped to the exclusive origin instead of leaking into the query.
    const page = await getOrchestratorRunEvents('run/1', -5)
    const cancelled = await cancelOrchestratorRun('run/1', 'key-2')

    expect(calls).toEqual([
      {
        url: '/api/admin/orchestrators/root%2F1/runs',
        method: 'POST',
        idempotencyKey: 'key-1',
        body: JSON.stringify({ message: 'hello', conversationId: 'conversation/1' }),
      },
      {
        url: '/api/orchestrator-runs/run%2F1/events?afterSequence=0&limit=100',
        method: 'GET',
        idempotencyKey: null,
        body: null,
      },
      // Cancel is a pure command: the idempotency key carries the identity, the body stays empty.
      {
        url: '/api/orchestrator-runs/run%2F1/cancel',
        method: 'POST',
        idempotencyKey: 'key-2',
        body: null,
      },
    ])

    expect(started).toEqual(
      expect.objectContaining({ runId: 'root-run', status: 'queued', pinnedWorkflowRevision: 3, budget: { maxTasks: 2 } }),
    )
    expect(page.events).toEqual([
      { sequence: 2, eventType: 'worker_completed', createdAt: null, payload: undefined },
    ])
    expect(page.latestEventSequence).toBe(2)
    // An accepted-but-nonterminal cancel is projected as `cancelling`, not still `running`.
    expect(cancelled.status).toBe('cancelling')
  } finally {
    restore()
  }
})

test('D5 single run fetch encodes the id and projects the snake_case detail envelope', async () => {
  const { calls, restore } = stubFetch([
    {
      id: 'root-run',
      status: 'waiting_input',
      state_version: 4,
      checkpoint_version: 8,
      latest_event_sequence: 12,
      execution_snapshot: { agent_revision: 6, workflow_revision: 2 },
      skill_bindings: [{ skill: 'review', skill_revision: 3 }],
      runtime_limits: { token_budget: 4000, nested: { hidden: true } },
      pending_input: { question: '請提供案號' },
      created_at: '2026-07-25T00:00:00Z',
    },
  ])

  try {
    const run = await getOrchestratorRun('run/1')

    // A plain detail read: same encoded run path as the commands, but no key and no body.
    expect(calls).toEqual([
      { url: '/api/orchestrator-runs/run%2F1', method: 'GET', idempotencyKey: null, body: null },
    ])
    expect(run).toEqual({
      runId: 'root-run',
      status: 'waiting_input',
      stateVersion: 4,
      checkpointVersion: 8,
      latestEventSequence: 12,
      // Pinned revisions fall back to the execution snapshot when the run body omits them.
      pinnedAgentRevision: 6,
      pinnedWorkflowRevision: 2,
      pinnedSkills: [{ name: 'review', revision: 3 }],
      // Only primitive budget entries survive the projection; nested objects are dropped.
      budget: { token_budget: 4000 },
      pendingInputMessage: '請提供案號',
      output: undefined,
      error: null,
      createdAt: '2026-07-25T00:00:00Z',
      updatedAt: null,
    })
  } finally {
    restore()
  }
})

test('D5 cancel keeps an already-terminal status instead of forcing cancelling', async () => {
  const { calls, restore } = stubFetch([
    { id: 'root-run', status: 'completed', cancel_requested_at: '2026-07-25T00:00:00Z' },
  ])

  try {
    const cancelled = await cancelOrchestratorRun('run/1', 'key-3')

    // The server reached a terminal state before the cancel landed: `cancel_requested_at` must
    // not repaint a finished run as `cancelling`, or the console would poll a dead run forever.
    expect(cancelled.status).toBe('completed')
    expect(calls).toEqual([
      {
        url: '/api/orchestrator-runs/run%2F1/cancel',
        method: 'POST',
        idempotencyKey: 'key-3',
        body: null,
      },
    ])
  } finally {
    restore()
  }
})

test('D5 event cursor passes 0 and positive sequences through unchanged', async () => {
  const { calls, restore } = stubFetch([
    { run_id: 'root-run', events: [], next_sequence: 0 },
    { run_id: 'root-run', events: [], next_sequence: 7 },
  ])

  try {
    await getOrchestratorRunEvents('run/1', 0)
    await getOrchestratorRunEvents('run/1', 7)

    // 0 is the valid lower bound, not something the clamp produced, and a live cursor must be
    // sent verbatim — only negatives are clamped, so re-polling never rewinds to the origin.
    expect(calls.map((call) => call.url)).toEqual([
      '/api/orchestrator-runs/run%2F1/events?afterSequence=0&limit=100',
      '/api/orchestrator-runs/run%2F1/events?afterSequence=7&limit=100',
    ])
  } finally {
    restore()
  }
})
