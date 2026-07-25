import { useRef, useState } from 'react'
import { decideRunApproval, listRunApprovals } from '../api/runApprovals'
import { newIdempotencyKey } from '../api/agentRuns'
import { getSessionStorage, LogicalAttemptKey } from '../logicalAttemptKey'
import type { RunApproval } from '../types'
import { fmtDate } from '../format'
import { useToast } from './Toast'

const ATTEMPT_KEY = 'springai-run-approvals:idempotency'

/** A USER-safe approval surface. Backend is the authorization and redaction authority. */
export default function ApprovalInbox() {
  const toast = useToast()
  const [runId, setRunId] = useState('')
  const [approvals, setApprovals] = useState<RunApproval[]>([])
  const [reason, setReason] = useState('')
  const [loading, setLoading] = useState(false)
  const attempts = useRef(new LogicalAttemptKey(newIdempotencyKey, getSessionStorage(), ATTEMPT_KEY))

  async function load() {
    const target = runId.trim()
    if (!target || loading) return
    setLoading(true)
    try { setApprovals(await listRunApprovals(target)) }
    catch (error) { toast((error as Error).message, 'error') }
    finally { setLoading(false) }
  }

  async function decide(approval: RunApproval, decision: 'approve' | 'reject') {
    const target = runId.trim()
    if (!target || loading || approval.status !== 'pending') return
    const identity = [target, approval.id, decision, reason.trim()] as const
    const key = attempts.current.keyFor(identity)
    setLoading(true)
    try {
      await decideRunApproval(target, approval.id, decision, reason, key)
      attempts.current.consume(identity, key)
      setApprovals(await listRunApprovals(target))
      toast(decision === 'approve' ? 'Approval recorded.' : 'Rejection recorded.', 'success')
    } catch (error) {
      toast((error as Error).message, 'error')
    } finally { setLoading(false) }
  }

  return <section className="agent-block" aria-busy={loading}>
    <h2>Run approvals</h2>
    <p className="muted">Enter a run ID you are allowed to review. Sensitive inputs, tool arguments, secrets, and anti-replay tokens are never displayed.</p>
    <div className="field"><label htmlFor="approval-run-id">Run ID</label><input id="approval-run-id" value={runId} onChange={(event) => setRunId(event.target.value)} /></div>
    <button className="btn btn--info" type="button" disabled={loading || !runId.trim()} onClick={() => void load()}>Load approvals</button>
    {approvals.length > 0 && <div className="field"><label htmlFor="approval-reason">Decision reason (optional)</label><input id="approval-reason" value={reason} onChange={(event) => setReason(event.target.value)} /></div>}
    {approvals.map((approval) => <article className="agent-block" key={approval.id}>
      <strong>{approval.status}</strong>
      <dl className="agent-test-console__summary">
        <div><dt>Required role</dt><dd>{approval.requiredRole ?? '—'}</dd></div>
        <div><dt>Expires</dt><dd>{approval.expiresAt ? fmtDate(approval.expiresAt) : '—'}</dd></div>
        <div><dt>Decision</dt><dd>{approval.decision ?? 'pending'}</dd></div>
      </dl>
      {approval.status === 'pending' && <div className="agent-test-console__actions">
        <button className="btn btn--info" type="button" disabled={loading} onClick={() => void decide(approval, 'approve')}>Approve</button>
        <button className="btn btn--danger" type="button" disabled={loading} onClick={() => void decide(approval, 'reject')}>Reject</button>
      </div>}
    </article>)}
    {!loading && runId.trim() && approvals.length === 0 && <p className="muted">No visible approvals for this run.</p>}
  </section>
}
