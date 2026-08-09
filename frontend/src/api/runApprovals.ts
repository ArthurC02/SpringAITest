import { apiFetch } from './http'
import { integer, object, pick, text } from '../wire'
import type { ApprovalQueueEntry, ApprovalQueuePage, ApprovalQueueScope, RunApproval } from '../types'

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

/** O3 discoverable approval queue: allowlists the cross-run projection, same redaction rule as {@link normalizeRunApproval}. */
function normalizeApprovalQueueEntry(value: unknown): ApprovalQueueEntry | null {
  const source = object(value)
  const approvalId = text(pick(source, 'approvalId', 'approval_id'))
  if (!approvalId) return null
  return {
    approvalId,
    runId: text(pick(source, 'runId', 'run_id')),
    agentId: text(pick(source, 'agentId', 'agent_id')),
    agentRevision: integer(pick(source, 'agentRevision', 'agent_revision')),
    status: text(pick(source, 'status')) ?? 'unknown',
    requiredRole: text(pick(source, 'requiredRole', 'required_role')),
    actionSummary: text(pick(source, 'actionSummary', 'action_summary')),
    createdAt: text(pick(source, 'createdAt', 'created_at')),
    expiresAt: text(pick(source, 'expiresAt', 'expires_at')),
    actionable: pick(source, 'actionable') === true,
  }
}

export function normalizeApprovalQueuePage(value: unknown): ApprovalQueuePage {
  const source = object(value)
  const raw = Array.isArray(value) ? value : (source.items as unknown[] | undefined) ?? []
  const items = raw.flatMap((item) => {
    const entry = normalizeApprovalQueueEntry(item)
    return entry ? [entry] : []
  })
  return {
    items,
    hasMore: pick(source, 'hasMore', 'has_more') === true,
    cursor: text(pick(source, 'cursor', 'nextCursor', 'next_cursor')),
  }
}

export async function listApprovalQueue(
  scope: ApprovalQueueScope,
  options: { limit?: number; cursor?: string | null } = {},
): Promise<ApprovalQueuePage> {
  const query = new URLSearchParams({ scope })
  if (options.limit) query.set('limit', String(options.limit))
  if (options.cursor) query.set('cursor', options.cursor)
  return normalizeApprovalQueuePage(await apiFetch<unknown>(`/api/runs/approvals?${query}`))
}
