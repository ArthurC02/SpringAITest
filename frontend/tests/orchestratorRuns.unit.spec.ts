import { expect, test } from 'vitest'
import { cancelOrchestratorRun, getOrchestratorRunEvents, startOrchestratorRun } from '../src/api/orchestratorRuns'

interface RecordedCall {
  url: string
  method: string
  idempotencyKey: string | null
  body: string | null
}

test('D5 root run API uses immutable command key and cursor polling', async () => {
  const original = globalThis.fetch
  const calls: RecordedCall[] = []
  const responses: unknown[] = [
    { id: 'root-run', status: 'queued', workflow_revision: 3, budgets: { maxTasks: 2 } },
    { run_id: 'root-run', events: [{ sequence: 2, event_type: 'worker_completed' }], next_sequence: 2 },
    { id: 'root-run', status: 'running', cancel_requested_at: '2026-07-25T00:00:00Z' },
  ]
  globalThis.fetch = (async (url: string, init?: RequestInit) => {
    calls.push({
      url,
      method: init?.method ?? 'GET',
      idempotencyKey: new Headers(init?.headers).get('Idempotency-Key'),
      body: init?.body == null ? null : String(init.body),
    })
    return new Response(JSON.stringify(responses.shift()), { headers: { 'content-type': 'application/json' } })
  }) as typeof fetch

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
    globalThis.fetch = original
  }
})
