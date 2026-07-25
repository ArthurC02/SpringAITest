import { apiFetch } from './http'
import type { RunApproval } from '../types'

type Obj = Record<string, unknown>

function obj(value: unknown): Obj {
  return value !== null && typeof value === 'object' && !Array.isArray(value) ? value as Obj : {}
}

function text(value: unknown): string | null { return typeof value === 'string' && value.length > 0 ? value : null }

function pick(source: Obj, ...keys: string[]): unknown {
  for (const key of keys) if (Object.hasOwn(source, key)) return source[key]
  return undefined
}

/** Accept only the allowlisted response projection; unknown fields are never rendered. */
export function normalizeRunApproval(value: unknown): RunApproval | null {
  const source = obj(value)
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
  const raw = Array.isArray(value) ? value : (obj(value).approvals as unknown[] | undefined) ?? []
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
