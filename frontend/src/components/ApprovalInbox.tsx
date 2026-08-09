import { useCallback, useEffect, useRef, useState } from 'react'
import { decideRunApproval, listApprovalQueue, listRunApprovals } from '../api/runApprovals'
import { newIdempotencyKey } from '../api/agentRuns'
import {
  getSessionStorage,
  LogicalAttemptKey,
  RUN_APPROVAL_ATTEMPT_STORAGE_PREFIX,
} from '../logicalAttemptKey'
import type { ApprovalQueueEntry, ApprovalQueueScope, RunApproval } from '../types'
import { fmtDate } from '../format'
import { runWithToast, useToast } from './Toast'

const ATTEMPT_KEY = `${RUN_APPROVAL_ATTEMPT_STORAGE_PREFIX}idempotency`

/** 待核准項目狀態 / 決定結果的顯示字。伺服器可能回傳未列舉的值,原樣顯示（不擋新狀態）。 */
const STATUS_LABEL: Record<string, string> = {
  pending: '待處理',
  approved: '已核准',
  rejected: '已駁回',
  unknown: '狀態未知',
}
function statusLabel(status: string): string {
  return STATUS_LABEL[status] ?? status
}

/** D7 目前只有一個寫入工具；佇列 action_summary 是伺服器固定字串，未列舉值原樣顯示。 */
const ACTION_SUMMARY_LABEL: Record<string, string> = {
  'runtime.write_evidence': '寫入執行證據',
}
function actionSummaryLabel(summary: string | null): string {
  if (!summary) return '（無動作摘要）'
  return ACTION_SUMMARY_LABEL[summary] ?? summary
}

const SCOPE_LABEL: Record<ApprovalQueueScope, string> = {
  actionable: '可核准',
  visible: '全部可見',
}

function agentIdentity(entry: ApprovalQueueEntry): string {
  const short = entry.agentId ? entry.agentId.slice(0, 8) : '—'
  return entry.agentRevision !== null ? `${short} · r${entry.agentRevision}` : short
}

function isExpired(entry: ApprovalQueueEntry): boolean {
  if (!entry.expiresAt) return false
  const t = new Date(entry.expiresAt).getTime()
  return !Number.isNaN(t) && t < Date.now()
}

/**
 * 已知的伺服器錯誤訊息（見 `AgentRunApprovalController`）逐句翻成業務人話；
 * 未列舉的訊息原樣顯示，不假裝知道原因。
 */
const APPROVAL_ERROR_LABEL: Record<string, string> = {
  'run not found': '找不到這個 Run，或你沒有查看權限。',
  'approval not found': '找不到這筆待核准項目，可能已被處理或你沒有查看權限。',
  'approval decision is not authorized': '你目前沒有核准這個 Run 的權限。',
  'approval expired': '這筆待核准項目已經過期。',
  'approval was already decided': '這筆待核准項目已經被處理過了。',
  'approval state changed': '這筆待核准項目的狀態已改變，請重新查詢。',
}
function approvalErrorMessage(error: unknown): string {
  const message = (error as Error).message
  return APPROVAL_ERROR_LABEL[message] ?? message
}

/** A USER-safe approval surface. Backend is the authorization and redaction authority. */
export default function ApprovalInbox() {
  const toast = useToast()
  const [runId, setRunId] = useState('')
  const [approvals, setApprovals] = useState<RunApproval[]>([])
  const [reason, setReason] = useState('')
  const [loading, setLoading] = useState(false)
  // 懶初始化:useRef(new X()) 每次 render 都會建構(並讀 sessionStorage),只有第一顆會被留下。
  const attemptRef = useRef<LogicalAttemptKey | null>(null)
  const attempts = (attemptRef.current ??= new LogicalAttemptKey(
    newIdempotencyKey,
    getSessionStorage(),
    ATTEMPT_KEY,
  ))

  // ── O3 佇列:進頁自動載入,預設「可核准」,可切「全部可見」;keyset 分頁靠 opaque cursor。──
  const [scope, setScope] = useState<ApprovalQueueScope>('actionable')
  const [queue, setQueue] = useState<ApprovalQueueEntry[]>([])
  const [queueCursor, setQueueCursor] = useState<string | null>(null)
  const [queueHasMore, setQueueHasMore] = useState(false)
  const [queueLoading, setQueueLoading] = useState(false)
  const [queueError, setQueueError] = useState<string | null>(null)
  // 世代守衛：避免快速切換 scope 或連續「載入更多」時，較舊的回應晚到覆寫較新的畫面狀態
  // （與 useResource 的 requestGenerationRef 同一個手法）。
  const queueGenerationRef = useRef(0)

  const loadQueue = useCallback(async (cursor: string | null) => {
    const generation = ++queueGenerationRef.current
    setQueueLoading(true)
    setQueueError(null)
    try {
      const page = await listApprovalQueue(scope, { cursor: cursor ?? undefined })
      if (generation !== queueGenerationRef.current) return
      setQueue((prev) => (cursor ? [...prev, ...page.items] : page.items))
      setQueueCursor(page.cursor)
      setQueueHasMore(page.hasMore)
    } catch (error) {
      // 非 404 的佇列載入失敗:錯誤顯示 + 手輸 Run ID 動線仍可用（fail-open）。
      // 404（功能未開）與其餘失敗走同一條路徑，不做特別解釋（404 偽裝契約)。
      if (generation === queueGenerationRef.current) setQueueError((error as Error).message)
    } finally {
      if (generation === queueGenerationRef.current) setQueueLoading(false)
    }
  }, [scope])

  useEffect(() => { void loadQueue(null) }, [loadQueue])

  async function load(target = runId.trim()) {
    if (!target || loading) return
    setLoading(true)
    // runWithToast 沒有錯誤轉譯掛鉤點，故在 fn 內先攔截、重丟人話訊息（見 approvalErrorMessage）。
    await runWithToast(
      toast,
      () => listRunApprovals(target).catch((error) => { throw new Error(approvalErrorMessage(error)) }),
      { onSuccess: setApprovals },
    )
    setLoading(false)
  }

  /** 點佇列項＝自動代填 Run ID 並沿用既有的單一 run 查詢，不重寫決策 UI。 */
  async function selectQueueEntry(entry: ApprovalQueueEntry) {
    if (!entry.runId || loading) return
    setRunId(entry.runId)
    await load(entry.runId)
  }

  async function decide(approval: RunApproval, decision: 'approve' | 'reject') {
    const target = runId.trim()
    if (!target || loading || approval.status !== 'pending') return
    const identity = [target, approval.id, decision, reason.trim()] as const
    const key = attempts.keyFor(identity)
    setLoading(true)
    try {
      await decideRunApproval(target, approval.id, decision, reason, key)
      attempts.consume(identity, key)
      setApprovals(await listRunApprovals(target))
      // 決策完成後對應的佇列項移除，不必整頁重新拉取。
      setQueue((prev) => prev.filter((entry) => entry.approvalId !== approval.id))
      toast(decision === 'approve' ? '已核准。' : '已駁回。', 'success')
    } catch (error) {
      toast(approvalErrorMessage(error), 'error')
    } finally { setLoading(false) }
  }

  return <section className="agent-block" aria-busy={loading || queueLoading}>
    <h2>Run 核准</h2>
    <p className="muted">
      這裡列出你可以核准的 Run。畫面只顯示必要的識別資訊與到期時間;
      原始輸入內容、工具參數、機密資料與防重放驗證碼（避免重複核准的安全機制）一律不會顯示。
    </p>

    <div className="seg config-tabs" role="group" aria-label="佇列範圍">
      {(['actionable', 'visible'] as const).map((s) => (
        <button
          key={s}
          type="button"
          className="btn"
          aria-pressed={scope === s}
          disabled={queueLoading && scope === s}
          onClick={() => setScope(s)}
        >
          {SCOPE_LABEL[s]}
        </button>
      ))}
    </div>

    {queueError && <div className="agent-errors" role="alert">
      佇列載入失敗：{queueError}
      <button className="btn" type="button" onClick={() => void loadQueue(null)}>重新載入</button>
    </div>}

    {!queueLoading && !queueError && queue.length === 0 && <p className="muted">目前沒有等待你核准的項目。</p>}

    {queue.map((entry) => {
      const expired = isExpired(entry)
      return <article
        key={entry.approvalId}
        className={`agent-block${!entry.actionable || expired ? ' approval-queue-item--muted' : ''}`}
      >
        <button
          type="button"
          className="btn btn--info"
          disabled={loading || !entry.runId}
          onClick={() => void selectQueueEntry(entry)}
        >
          {actionSummaryLabel(entry.actionSummary)}
        </button>
        <dl className="agent-test-console__summary">
          <div><dt>Agent</dt><dd>{agentIdentity(entry)}</dd></div>
          <div><dt>狀態</dt><dd>{statusLabel(entry.status)}</dd></div>
          <div><dt>需要核准的角色</dt><dd>{entry.requiredRole ?? '—'}</dd></div>
          <div><dt>建立時間</dt><dd>{entry.createdAt ? fmtDate(entry.createdAt) : '—'}</dd></div>
          <div><dt>到期時間</dt><dd>{entry.expiresAt ? fmtDate(entry.expiresAt) : '—'}</dd></div>
        </dl>
        {expired && <span className="chip chip--failed">已過期</span>}
        {!entry.actionable && !expired && <span className="chip chip--processing">你目前不可核准</span>}
      </article>
    })}

    {queueHasMore && <button className="btn" type="button" disabled={queueLoading} onClick={() => void loadQueue(queueCursor)}>載入更多</button>}

    <details className="agent-block">
      <summary>已知 Run ID 時可直接查詢（進階）</summary>
      <div className="field"><label htmlFor="approval-run-id">Run ID</label><input id="approval-run-id" value={runId} onChange={(event) => setRunId(event.target.value)} /></div>
      <button className="btn btn--info" type="button" disabled={loading || !runId.trim()} onClick={() => void load()}>查詢待核准項目</button>
    </details>
    {approvals.length > 0 && <div className="field"><label htmlFor="approval-reason">決定原因（選填）</label><input id="approval-reason" value={reason} onChange={(event) => setReason(event.target.value)} /></div>}
    {approvals.map((approval) => <article className="agent-block" key={approval.id}>
      <strong>{statusLabel(approval.status)}</strong>
      <dl className="agent-test-console__summary">
        <div><dt>需要核准的角色</dt><dd>{approval.requiredRole ?? '—'}</dd></div>
        <div><dt>到期時間</dt><dd>{approval.expiresAt ? fmtDate(approval.expiresAt) : '—'}</dd></div>
        <div><dt>決定結果</dt><dd>{approval.decision ? statusLabel(approval.decision) : '尚未決定'}</dd></div>
      </dl>
      {approval.status === 'pending' && <div className="agent-test-console__actions">
        <button className="btn btn--info" type="button" disabled={loading} onClick={() => void decide(approval, 'approve')}>核准</button>
        <button className="btn btn--danger" type="button" disabled={loading} onClick={() => void decide(approval, 'reject')}>駁回</button>
      </div>}
    </article>)}
    {!loading && runId.trim() && approvals.length === 0 && <p className="muted">此 Run 目前沒有你可查看的待核准項目。</p>}
  </section>
}
