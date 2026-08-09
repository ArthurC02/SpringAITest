import { expect, test } from 'vitest'
import {
  decideRunApproval,
  listApprovalQueue,
  listRunApprovals,
  normalizeApprovalQueuePage,
  normalizeRunApproval,
  normalizeRunApprovals,
} from '../src/api/runApprovals'

test.describe('D7 approval public projection', () => {
  test('allowlists redacted fields and drops prompt, tool and anti-replay data', () => {
    const approval = normalizeRunApproval({
      id: 'approval-1', run_id: 'run-1', status: 'pending', required_role: 'USER',
      action_fingerprint: 'safe-reference', expires_at: '2026-01-01T00:00:00Z',
      prompt: 'secret prompt', tool_args: { secret: 'x' }, tool_result: 'x',
      internal_token: 'x', idempotency_key: 'x', anti_replay_token: 'x',
    })
    expect(approval).toEqual({
      id: 'approval-1', runId: 'run-1', status: 'pending', requiredRole: 'USER',
      expiresAt: '2026-01-01T00:00:00Z',
      decision: null, decidedBy: null, decidedAt: null,
    })
    expect(JSON.stringify(approval)).not.toContain('secret')
    expect(normalizeRunApprovals({ approvals: [{ id: 'approval-2', status: 'pending' }, {}] })).toHaveLength(1)
  })

  test('accepts a bare array payload, a decided approval and a missing payload', () => {
    // The list route may answer with a raw array instead of the {approvals} envelope.
    expect(normalizeRunApprovals([{ id: 'approval-3', status: 'pending' }])).toEqual([
      {
        id: 'approval-3', runId: null, status: 'pending', requiredRole: null,
        expiresAt: null, decision: null, decidedBy: null, decidedAt: null,
      },
    ])
    // Neither array nor object (null/undefined body) degrades to an empty inbox, never a throw.
    expect(normalizeRunApprovals(null)).toEqual([])
    expect(normalizeRunApprovals(undefined)).toEqual([])
    // An already-decided approval keeps its outcome trio while still dropping tool payloads.
    expect(normalizeRunApproval({
      id: 'approval-4', run_id: 'run-1', status: 'approved', required_role: 'ADMIN',
      expires_at: '2026-01-01T00:00:00Z',
      decision: 'approved', decided_by: 'alice', decided_at: '2026-01-02T00:00:00Z',
      tool_args: { secret: 'x' },
    })).toEqual({
      id: 'approval-4', runId: 'run-1', status: 'approved', requiredRole: 'ADMIN',
      expiresAt: '2026-01-01T00:00:00Z',
      decision: 'approved', decidedBy: 'alice', decidedAt: '2026-01-02T00:00:00Z',
    })
  })

  test('listing an inbox is a plain GET on the encoded run id and skips unidentifiable entries', async () => {
    const originalFetch = globalThis.fetch
    let seen: { url: string; method: string; key: string | null } | null = null
    globalThis.fetch = async (input, init) => {
      seen = { url: String(input), method: init?.method ?? 'GET', key: new Headers(init?.headers).get('Idempotency-Key') }
      return new Response('{"approvals":[{"id":"approval-5","status":"pending"},{"status":"pending"}]}', { status: 200, headers: { 'Content-Type': 'application/json' } })
    }
    try {
      // A read carries no idempotency key: only the decision commands are replay-protected.
      expect((await listRunApprovals('run/1')).map((approval) => approval.id)).toEqual(['approval-5'])
    } finally { globalThis.fetch = originalFetch }
    expect(seen).toEqual({ url: '/api/runs/run%2F1/approvals', method: 'GET', key: null })
  })

  test('decision sends a caller-generated idempotency key but never requires an anti-replay token', async () => {
    const originalFetch = globalThis.fetch
    let seen: { method: string; key: string | null; body: unknown } | null = null
    globalThis.fetch = async (input, init) => {
      seen = { method: init?.method ?? 'GET', key: new Headers(init?.headers).get('Idempotency-Key'), body: JSON.parse(String(init?.body)) }
      expect(String(input)).toBe('/api/runs/run-1/approvals/approval-1/approve')
      return new Response('{"id":"approval-1","status":"approved"}', { status: 200, headers: { 'Content-Type': 'application/json' } })
    }
    try { await decideRunApproval('run-1', 'approval-1', 'approve', 'reviewed', 'logical-attempt-1') }
    finally { globalThis.fetch = originalFetch }
    expect(seen).toEqual({ method: 'POST', key: 'logical-attempt-1', body: { reason: 'reviewed' } })
  })

  test('rejection posts the reject route and a whitespace-only reason is dropped from the body', async () => {
    const originalFetch = globalThis.fetch
    let seen: { url: string; key: string | null; body: unknown } | null = null
    globalThis.fetch = async (input, init) => {
      seen = { url: String(input), key: new Headers(init?.headers).get('Idempotency-Key'), body: JSON.parse(String(init?.body)) }
      return new Response('{"id":"approval-1","status":"rejected","decision":"rejected","decided_by":"alice"}', { status: 200, headers: { 'Content-Type': 'application/json' } })
    }
    try {
      const decided = await decideRunApproval('run-1', 'approval-1', 'reject', '   ', 'logical-attempt-2')
      expect(decided).toMatchObject({ status: 'rejected', decision: 'rejected', decidedBy: 'alice' })
    } finally { globalThis.fetch = originalFetch }
    // Reject is a distinct route segment, and a blank reason is omitted rather than sent as ''.
    expect(seen).toEqual({ url: '/api/runs/run-1/approvals/approval-1/reject', key: 'logical-attempt-2', body: {} })
  })
})

test.describe('O3 discoverable approval queue projection', () => {
  test('allowlists cross-run identity fields and drops unidentifiable rows', () => {
    const page = normalizeApprovalQueuePage({
      items: [
        {
          approval_id: 'approval-1', run_id: 'run-1', agent_id: 'agent-1', agent_revision: 3,
          status: 'pending', required_role: 'USER', action_summary: 'runtime.write_evidence',
          created_at: '2026-01-01T00:00:00Z', expires_at: '2026-01-02T00:00:00Z', actionable: true,
          action_fingerprint: 'secret', checkpoint_ref: 'secret', lease_token: 'secret',
        },
        { status: 'pending' },
      ],
      next_cursor: 'opaque-cursor', has_more: true,
    })
    expect(page).toEqual({
      items: [{
        approvalId: 'approval-1', runId: 'run-1', agentId: 'agent-1', agentRevision: 3,
        status: 'pending', requiredRole: 'USER', actionSummary: 'runtime.write_evidence',
        createdAt: '2026-01-01T00:00:00Z', expiresAt: '2026-01-02T00:00:00Z', actionable: true,
      }],
      hasMore: true, cursor: 'opaque-cursor',
    })
    expect(JSON.stringify(page)).not.toContain('secret')
  })

  test('accepts a bare array payload and a missing payload', () => {
    expect(normalizeApprovalQueuePage([{ approval_id: 'approval-2', status: 'pending', actionable: false }])).toEqual({
      items: [{
        approvalId: 'approval-2', runId: null, agentId: null, agentRevision: null,
        status: 'pending', requiredRole: null, actionSummary: null,
        createdAt: null, expiresAt: null, actionable: false,
      }],
      hasMore: false, cursor: null,
    })
    expect(normalizeApprovalQueuePage(null)).toEqual({ items: [], hasMore: false, cursor: null })
    expect(normalizeApprovalQueuePage(undefined)).toEqual({ items: [], hasMore: false, cursor: null })
  })

  test('lists a scoped page and forwards limit/cursor as query params', async () => {
    const originalFetch = globalThis.fetch
    let seen: { url: string; method: string } | null = null
    globalThis.fetch = async (input, init) => {
      seen = { url: String(input), method: init?.method ?? 'GET' }
      return new Response(
        '{"items":[{"approval_id":"approval-3","status":"pending","actionable":true}],"has_more":false,"next_cursor":null}',
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      )
    }
    try {
      const page = await listApprovalQueue('actionable', { limit: 10, cursor: 'next-page' })
      expect(page.items.map((entry) => entry.approvalId)).toEqual(['approval-3'])
    } finally { globalThis.fetch = originalFetch }
    expect(seen).toEqual({
      url: '/api/runs/approvals?scope=actionable&limit=10&cursor=next-page',
      method: 'GET',
    })
  })
})
