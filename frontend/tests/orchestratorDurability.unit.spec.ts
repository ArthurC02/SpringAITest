import { expect, test } from 'vitest'
import { mergeRunEvents } from '../src/agentRunDisplay'
import {
  clearOrchestratorRunState,
  clearOrchestratorRunStorage,
  messageFingerprint,
  orchestratorRunStorageKey,
  readOrchestratorRunState,
  writeOrchestratorRunState,
} from '../src/orchestratorRunState'
import { safeOrchestratorBudget, toOrchestratorTraceEvent } from '../src/orchestratorTrace'
import { normalizeAgentRunEventPage } from '../src/api/agentRuns'
import type { AgentRun } from '../src/types'

class MemoryStorage implements Storage {
  private readonly values = new Map<string, string>()
  get length(): number { return this.values.size }
  clear(): void { this.values.clear() }
  getItem(key: string): string | null { return this.values.get(key) ?? null }
  key(index: number): string | null { return [...this.values.keys()][index] ?? null }
  removeItem(key: string): void { this.values.delete(key) }
  setItem(key: string, value: string): void { this.values.set(key, value) }
}

test('D5 orchestrator state keeps the logical key, conversation, cursor and active run across reload', () => {
  const storage = new MemoryStorage()
  const record = {
    version: 1 as const, orchestratorId: 'root-1', runId: null, conversationId: 'conversation-1',
    startKey: 'logical-start-key', messageFingerprint: messageFingerprint('do not persist this message'),
    eventCursor: 2, cancelKey: null, cancelAccepted: false,
  }
  writeOrchestratorRunState('tenant:user', record, storage)
  const afterReload = readOrchestratorRunState('tenant:user', 'root-1', storage)
  expect(afterReload).toEqual(record)
  expect(storage.getItem('springai-orchestrator-runs:active:tenant%3Auser:root-1')).not.toContain('do not persist this message')

  writeOrchestratorRunState('tenant:user', { ...record, runId: 'run-1', eventCursor: 7, cancelKey: 'cancel-key', cancelAccepted: true }, storage)
  expect(readOrchestratorRunState('tenant:user', 'root-1', storage)).toEqual(expect.objectContaining({ runId: 'run-1', eventCursor: 7, cancelKey: 'cancel-key', cancelAccepted: true }))

  // Finishing one root drops only that Orchestrator's key; a second root's active run must survive
  // until the bulk logout wipe takes the whole prefix.
  const sibling = { ...record, orchestratorId: 'root-2' }
  writeOrchestratorRunState('tenant:user', sibling, storage)
  clearOrchestratorRunState('tenant:user', 'root-1', storage)
  expect(readOrchestratorRunState('tenant:user', 'root-1', storage)).toBeNull()
  expect(readOrchestratorRunState('tenant:user', 'root-2', storage)).toEqual(sibling)
  clearOrchestratorRunStorage(storage)
  expect(readOrchestratorRunState('tenant:user', 'root-2', storage)).toBeNull()
})

test('D5 orchestrator run state fails closed for a foreign scope, orchestrator, or invalid cursor', () => {
  const record = {
    version: 1 as const, orchestratorId: 'root-1', runId: 'run-1', conversationId: 'conversation-1',
    startKey: 'logical-start-key', messageFingerprint: messageFingerprint('message'),
    eventCursor: 2, cancelKey: null, cancelAccepted: false,
  }

  // A different tenant/user scope never reads another identity's active run (D6 account switch).
  const scoped = new MemoryStorage()
  writeOrchestratorRunState('tenant:user', record, scoped)
  expect(readOrchestratorRunState('other:user', 'root-1', scoped)).toBeNull()
  expect(readOrchestratorRunState('tenant:user', 'root-1', scoped)).toEqual(record)

  // A record whose payload names a different Orchestrator than its key is rejected and purged,
  // so a tampered or stale entry can never resume the wrong root.
  const mismatchedKey = orchestratorRunStorageKey('tenant:user', 'root-2')
  scoped.setItem(mismatchedKey, JSON.stringify({ ...record, orchestratorId: 'root-1' }))
  expect(readOrchestratorRunState('tenant:user', 'root-2', scoped)).toBeNull()
  expect(scoped.getItem(mismatchedKey)).toBeNull()

  const rejected: Array<[string, unknown]> = [
    ['negative cursor', { ...record, eventCursor: -1 }],
    ['fractional cursor', { ...record, eventCursor: 1.5 }],
    ['unsafe cursor', { ...record, eventCursor: Number.MAX_SAFE_INTEGER + 1 }],
    ['future schema version', { ...record, version: 2 }],
    ['blank conversation', { ...record, conversationId: '' }],
    ['missing start key', { ...record, startKey: '' }],
    ['non-boolean cancel flag', { ...record, cancelAccepted: 'yes' }],
    ['malformed json', '{'],
  ]
  const key = orchestratorRunStorageKey('tenant:user', 'root-1')
  for (const [label, value] of rejected) {
    const storage = new MemoryStorage()
    storage.setItem(key, typeof value === 'string' ? value : JSON.stringify(value))
    expect(readOrchestratorRunState('tenant:user', 'root-1', storage), label).toBeNull()
  }

  // The valid cursor boundaries themselves still load: a run that has seen no event yet (0) and the
  // largest safe integer both resume, so the fail-closed guard is not fail-always.
  for (const eventCursor of [0, Number.MAX_SAFE_INTEGER]) {
    const storage = new MemoryStorage()
    storage.setItem(key, JSON.stringify({ ...record, eventCursor }))
    expect(readOrchestratorRunState('tenant:user', 'root-1', storage), `cursor ${eventCursor}`).toEqual({ ...record, eventCursor })
  }
})

test('D5 event pages use their exclusive next_sequence cursor and dedupe replayed sequence numbers', () => {
  const first = normalizeAgentRunEventPage({ events: [{ sequence: 3, event_type: 'child_created' }], next_sequence: 3 })
  const empty = normalizeAgentRunEventPage({ events: [], next_sequence: 3 })
  const replayed = normalizeAgentRunEventPage({ events: [{ sequence: 3, event_type: 'child_created' }, { sequence: 4, event_type: 'child_terminal' }], next_sequence: 4 })
  expect(first.latestEventSequence).toBe(3)
  expect(empty.latestEventSequence).toBe(3)
  expect(mergeRunEvents(first.events, replayed.events).map((event) => event.sequence)).toEqual([3, 4])

  // A page that carries no cursor field at all — a bare array, or an envelope without next_sequence —
  // falls back to the newest sequence it did deliver, so polling never restarts from 0.
  const bareArray = normalizeAgentRunEventPage([{ sequence: 5, event_type: 'child_terminal' }])
  const withoutCursor = normalizeAgentRunEventPage({ events: [{ sequence: 5, event_type: 'child_terminal' }] })
  expect(bareArray.events.map((event) => event.sequence)).toEqual([5])
  expect(bareArray.latestEventSequence).toBe(5)
  expect(withoutCursor.latestEventSequence).toBe(5)
})

test('D5 trace projection exposes stable root/child fields while excluding raw context and secrets', () => {
  const run: AgentRun = { runId: 'root-run', status: 'running', stateVersion: null, checkpointVersion: null, latestEventSequence: 2, pinnedAgentRevision: null, pinnedWorkflowRevision: 5, pinnedSkills: [], budget: { maxTasks: 3, token_budget: 2000, authorization: 'never display', nested: null }, pendingInputMessage: null, output: { secret: 'do not display' }, error: null, createdAt: null, updatedAt: null }
  expect(safeOrchestratorBudget(run)).toEqual([['maxTasks', 3], ['token_budget', 2000]])
  // Being on the allowlist is not a free pass: a non-scalar value under an allowlisted key is dropped
  // as well, so an object/array smuggled into budget can never reach the UI.
  expect(safeOrchestratorBudget({ ...run, budget: { maxTasks: { nested: 1 }, token_budget: [2000], tokensUsed: 12 } as unknown as AgentRun['budget'] })).toEqual([['tokensUsed', 12]])
  expect(toOrchestratorTraceEvent({ sequence: 2, eventType: 'child_terminal', createdAt: null, payload: { child_run_id: 'child-1', task_id: 'task-1', attempt: 2, run_kind: 'verifier', agent_id: 'agent-1', agent_revision: 4, status: 'completed', verdict: 'approved', citations: [{ id: 'doc-1', title: 'Safe citation', url: 'https://not-rendered' }], context: { secret: 'not-rendered' }, output: 'not-rendered' } })).toEqual({ sequence: 2, eventType: 'child_terminal', rootStatus: null, child: { childId: 'child-1', taskId: 'task-1', attempt: 2, kind: 'verifier', agentId: 'agent-1', agentRevision: 4, status: 'completed', verdict: 'approved', citations: [{ id: 'doc-1', title: 'Safe citation' }] } })
})

test('D5 trace projection reports root terminal status without a child and caps citation exposure', () => {
  // Only the two root-level event types read `status`, and a root event has no child section at all.
  expect(toOrchestratorTraceEvent({ sequence: 1, eventType: 'root_terminal', createdAt: null, payload: { status: 'completed', context: { secret: 'not-rendered' } } })).toEqual({ sequence: 1, eventType: 'root_terminal', rootStatus: 'completed', child: null })
  expect(toOrchestratorTraceEvent({ sequence: 2, eventType: 'run_cancelled', createdAt: null, payload: { status: 'cancelled' } })).toEqual({ sequence: 2, eventType: 'run_cancelled', rootStatus: 'cancelled', child: null })

  const capped = toOrchestratorTraceEvent({ sequence: 3, eventType: 'child_terminal', createdAt: null, payload: { child_run_id: 'child-1', citations: Array.from({ length: 25 }, (_, index) => ({ id: `doc-${index}` })) } })
  expect(capped.child?.citations).toHaveLength(20)
  expect(capped.child?.citations.at(-1)).toEqual({ id: 'doc-19', title: null })
  // A citation without a usable id is dropped instead of being rendered as an anonymous source.
  expect(toOrchestratorTraceEvent({ sequence: 4, eventType: 'child_terminal', createdAt: null, payload: { child_run_id: 'child-1', citations: [{ title: '沒有 id 的引註' }] } }).child?.citations).toEqual([])
})
