import { apiFetch } from './http'
import { object, pick, text } from '../wire'
import type { RunApproval } from '../types'

/** Accept only the allowlisted response projection; unknown fields are never rendered. */
export function normalizeRunApproval(value: unknown): RunApproval | null {
  const source = object(value)
  const id = text(pick(source, 'id', 'approvalId', 'approval_id'))
  if (!id) return null
  return {
    id,
    runId: text(pick(source, 'runId', 'run_id')),
    status: text(pick(source, 'status')) ?? 'unknown',
    requiredRole: text(pick(source, 'requiredRole', 'required_role')),
    expiresAt: text(pick(source, 'expiresAt', 'expires_at')),
    decision: text(pick(source, 'decision')),
    decidedBy: text(pick(source, 'decidedBy', 'decided_by')),
    decidedAt: text(pick(source, 'decidedAt', 'decided_at')),
  }
}

export function normalizeRunApprovals(value: unknown): RunApproval[] {
  const raw = Array.isArray(value) ? value : (object(value).approvals as unknown[] | undefined) ?? []
  return raw.flatMap((item) => {
    const approval = normalizeRunApproval(item)
    return approval ? [approval] : []
  })
}

export async function listRunApprovals(runId: string): Promise<RunApproval[]> {
  return normalizeRunApprovals(await apiFetch<unknown>(`/api/runs/${encodeURIComponent(runId)}/approvals`))
}

export async function decideRunApproval(runId: string, approvalId: string, decision: 'approve' | 'reject', reason: string, idempotencyKey: string): Promise<RunApproval | null> {
  return normalizeRunApproval(await apiFetch<unknown>(
    `/api/runs/${encodeURIComponent(runId)}/approvals/${encodeURIComponent(approvalId)}/${decision}`,
    { method: 'POST', headers: { 'Idempotency-Key': idempotencyKey }, body: JSON.stringify({ reason: reason.trim() || undefined }) },
  ))
}
