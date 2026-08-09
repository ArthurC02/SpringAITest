import { expect, test } from 'vitest'
import { listRuns, normalizeRunDiscoveryPage, normalizeRunSummaryItem } from '../src/api/runs'
import {
  childProgressSummary,
  fmtElapsed,
  kindLabel,
  runIdentity,
  statusChipClass,
  statusLabel,
} from '../src/runDiscoveryDisplay'
import type { RunSummaryItem } from '../src/types'

const WIRE_ROOT = {
  id: 'run-1', kind: 'orchestrator', status: 'running',
  orchestrator_root_run_id: null, task_id: null,
  agent_id: null, agent_revision: null,
  orchestrator_id: 'orch-1', orchestrator_revision: 2,
  workflow_id: 'wf-1', workflow_revision: 1,
  cancel_requested: false,
  budget_summary: { token_budget: 1000, step_budget: 20 },
  last_event_type: 'child.dispatched', last_event_at: '2026-01-01T00:00:00Z',
  child_progress: { total: 3, queued: 1, running: 1, completed: 1, failed: 0, cancelled: 0 },
  error_class: null,
  pending_approval: true, needs_recovery: false,
  started_at: '2026-01-01T00:00:00Z', created_at: '2026-01-01T00:00:00Z',
  updated_at: '2026-01-01T00:05:00Z', completed_at: null,
  elapsed_seconds: 305,
}

test.describe('O2 run summary projection', () => {
  test('allowlists the safe summary fields and drops unlisted ones', () => {
    const run = normalizeRunSummaryItem({
      ...WIRE_ROOT,
      prompt: 'secret prompt', tool_args: { secret: 'x' }, checkpoint_ref: 'secret', lease_token: 'x',
    })
    expect(run).toEqual({
      id: 'run-1', kind: 'orchestrator', status: 'running',
      orchestratorRootRunId: null, taskId: null,
      agentId: null, agentRevision: null,
      orchestratorId: 'orch-1', orchestratorRevision: 2,
      workflowId: 'wf-1', workflowRevision: 1,
      cancelRequested: false,
      budgetSummary: { token_budget: 1000, step_budget: 20 },
      lastEventType: 'child.dispatched', lastEventAt: '2026-01-01T00:00:00Z',
      childProgress: { total: 3, queued: 1, running: 1, completed: 1, failed: 0, cancelled: 0 },
      errorClass: null,
      pendingApproval: true, needsRecovery: false,
      startedAt: '2026-01-01T00:00:00Z', createdAt: '2026-01-01T00:00:00Z',
      updatedAt: '2026-01-01T00:05:00Z', completedAt: null,
      elapsedSeconds: 305,
    })
    expect(JSON.stringify(run)).not.toContain('secret')
  })

  test('a non-root row has a null child_progress even though the key is present as JSON null', () => {
    const run = normalizeRunSummaryItem({
      ...WIRE_ROOT, id: 'run-2', kind: 'direct-agent', agent_id: 'agent-1', agent_revision: 5,
      orchestrator_id: null, orchestrator_revision: null, child_progress: null,
    })
    expect(run?.childProgress).toBeNull()
    expect(run?.kind).toBe('direct-agent')
  })

  test('a row without an id is dropped; a page accepts a bare array or falls back to an empty page', () => {
    expect(normalizeRunSummaryItem({ kind: 'worker' })).toBeNull()
    expect(normalizeRunDiscoveryPage([WIRE_ROOT]).items).toHaveLength(1)
    expect(normalizeRunDiscoveryPage(null)).toEqual({ items: [], hasMore: false, cursor: null })
    expect(normalizeRunDiscoveryPage({ items: [WIRE_ROOT], has_more: true, next_cursor: 'c1' })).toEqual({
      items: [normalizeRunSummaryItem(WIRE_ROOT)], hasMore: true, cursor: 'c1',
    })
  })

  test('listRuns forwards only kind/status/cursor/limit as query params, omitting unset ones', async () => {
    const originalFetch = globalThis.fetch
    let seen: string | null = null
    globalThis.fetch = async (input) => {
      seen = String(input)
      return new Response('{"items":[],"has_more":false,"next_cursor":null}', {
        status: 200, headers: { 'Content-Type': 'application/json' },
      })
    }
    try {
      await listRuns({ kind: 'worker', status: 'running', cursor: 'c1', limit: 10 })
      expect(seen).toBe('/api/runs?kind=worker&status=running&cursor=c1&limit=10')
      await listRuns()
      expect(seen).toBe('/api/runs')
    } finally { globalThis.fetch = originalFetch }
  })
})

test.describe('O2 run display helpers', () => {
  test('kind/status labels are Chinese for known values and pass through unknown ones verbatim', () => {
    expect(kindLabel('direct-agent')).toBe('Direct')
    expect(kindLabel('worker')).toBe('Worker')
    expect(kindLabel('verifier')).toBe('Verifier')
    expect(kindLabel('orchestrator')).toBe('協作 root')
    expect(kindLabel('future-kind')).toBe('future-kind')
    expect(statusLabel('completed')).toBe('已完成')
    expect(statusLabel('timed_out')).toBe('已逾時')
    expect(statusLabel('future-status')).toBe('future-status')
    expect(statusChipClass('completed')).toBe('chip--ready')
    expect(statusChipClass('failed')).toBe('chip--failed')
    expect(statusChipClass('future-status')).toBe('chip--skip')
  })

  test('fmtElapsed humanizes seconds into the largest one or two units', () => {
    expect(fmtElapsed(45)).toBe('45 秒')
    expect(fmtElapsed(305)).toBe('5 分鐘')
    expect(fmtElapsed(3661)).toBe('1 小時 1 分')
    expect(fmtElapsed(3600)).toBe('1 小時')
    expect(fmtElapsed(90000)).toBe('1 天 1 小時')
    expect(fmtElapsed(Number.NaN)).toBe('—')
    expect(fmtElapsed(-1)).toBe('—')
  })

  test('runIdentity truncates the id relevant to the row kind and appends the revision', () => {
    const orchestratorRun = normalizeRunSummaryItem(WIRE_ROOT) as RunSummaryItem
    expect(runIdentity(orchestratorRun)).toBe('orch-1 · r2')
    const agentRun = normalizeRunSummaryItem({
      ...WIRE_ROOT, id: 'run-3', kind: 'worker', agent_id: '12345678-aaaa-bbbb-cccc-dddddddddddd', agent_revision: 4,
      orchestrator_id: null, orchestrator_revision: null,
    }) as RunSummaryItem
    expect(runIdentity(agentRun)).toBe('12345678 · r4')
    const missingIdentity = normalizeRunSummaryItem({
      ...WIRE_ROOT, id: 'run-4', kind: 'worker', agent_id: null, orchestrator_id: null,
    }) as RunSummaryItem
    expect(runIdentity(missingIdentity)).toBe('—')
  })

  test('childProgressSummary is only present for rows carrying child_progress', () => {
    const root = normalizeRunSummaryItem(WIRE_ROOT) as RunSummaryItem
    expect(childProgressSummary(root)).toBe('共 3(排隊 1‧執行中 1‧完成 1‧失敗 0‧取消 0)')
    const worker = normalizeRunSummaryItem({ ...WIRE_ROOT, id: 'run-5', kind: 'worker', child_progress: null }) as RunSummaryItem
    expect(childProgressSummary(worker)).toBeNull()
  })
})
