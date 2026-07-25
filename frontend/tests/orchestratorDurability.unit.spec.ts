import { expect, test } from '@playwright/test'
import { mergeRunEvents } from '../src/agentRunDisplay'
import {
  clearOrchestratorRunStorage,
  messageFingerprint,
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
  clearOrchestratorRunStorage(storage)
  expect(readOrchestratorRunState('tenant:user', 'root-1', storage)).toBeNull()
})

test('D5 event pages use their exclusive next_sequence cursor and dedupe replayed sequence numbers', () => {
  const first = normalizeAgentRunEventPage({ events: [{ sequence: 3, event_type: 'child_created' }], next_sequence: 3 })
  const empty = normalizeAgentRunEventPage({ events: [], next_sequence: 3 })
  const replayed = normalizeAgentRunEventPage({ events: [{ sequence: 3, event_type: 'child_created' }, { sequence: 4, event_type: 'child_terminal' }], next_sequence: 4 })
  expect(first.latestEventSequence).toBe(3)
  expect(empty.latestEventSequence).toBe(3)
  expect(mergeRunEvents(first.events, replayed.events).map((event) => event.sequence)).toEqual([3, 4])
})

test('D5 trace projection exposes stable root/child fields while excluding raw context and secrets', () => {
  const run: AgentRun = { runId: 'root-run', status: 'running', stateVersion: null, checkpointVersion: null, latestEventSequence: 2, pinnedAgentRevision: null, pinnedWorkflowRevision: 5, pinnedSkills: [], budget: { maxTasks: 3, token_budget: 2000, authorization: 'never display', nested: null }, pendingInputMessage: null, output: { secret: 'do not display' }, error: null, createdAt: null, updatedAt: null }
  expect(safeOrchestratorBudget(run)).toEqual([['maxTasks', 3], ['token_budget', 2000]])
  expect(toOrchestratorTraceEvent({ sequence: 2, eventType: 'child_terminal', createdAt: null, payload: { child_run_id: 'child-1', task_id: 'task-1', attempt: 2, run_kind: 'verifier', agent_id: 'agent-1', agent_revision: 4, status: 'completed', verdict: 'approved', citations: [{ id: 'doc-1', title: 'Safe citation', url: 'https://not-rendered' }], context: { secret: 'not-rendered' }, output: 'not-rendered' } })).toEqual({ sequence: 2, eventType: 'child_terminal', rootStatus: null, child: { childId: 'child-1', taskId: 'task-1', attempt: 2, kind: 'verifier', agentId: 'agent-1', agentRevision: 4, status: 'completed', verdict: 'approved', citations: [{ id: 'doc-1', title: 'Safe citation' }] } })
})
