import { expect, test } from '@playwright/test'
import { cancelOrchestratorRun, getOrchestratorRunEvents, startOrchestratorRun } from '../src/api/orchestratorRuns'

test('D5 root run API uses immutable command key and cursor polling', async () => {
  const original = globalThis.fetch; const calls: Array<{ url: string; init?: RequestInit }> = []
  globalThis.fetch = (async (url: string, init?: RequestInit) => { calls.push({ url, init }); return new Response(JSON.stringify(url.includes('/events') ? { run_id: 'r', events: [{ sequence: 2, event_type: 'worker_completed' }] } : { id: 'r', status: 'queued', workflow_revision: 3, budgets: {} }), { headers: { 'content-type': 'application/json' } }) }) as typeof fetch
  try { await startOrchestratorRun('o', 'hello', 'c', 'key-1'); await getOrchestratorRunEvents('r', 2); await cancelOrchestratorRun('r', 'key-2'); expect(calls[0].url).toContain('/api/admin/orchestrators/o/runs'); expect(new Headers(calls[0].init?.headers).get('Idempotency-Key')).toBe('key-1'); expect(JSON.parse(String(calls[0].init?.body))).toEqual({ message: 'hello', conversationId: 'c' }); expect(calls[1].url).toContain('afterSequence=2'); expect(new Headers(calls[2].init?.headers).get('Idempotency-Key')).toBe('key-2') } finally { globalThis.fetch = original }
})
