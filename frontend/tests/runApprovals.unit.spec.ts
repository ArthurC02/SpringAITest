import { expect, test } from 'vitest'
import { decideRunApproval, normalizeRunApproval, normalizeRunApprovals } from '../src/api/runApprovals'

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
})
