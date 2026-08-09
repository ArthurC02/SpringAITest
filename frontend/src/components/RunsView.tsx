import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { listRuns } from '../api/runs'
import { listAgents } from '../api/agents'
import { listOrchestrators } from '../api/orchestrators'
import type { RunSummaryItem } from '../types'
import { fmtDate } from '../format'
import { useResource } from '../hooks/useResource'
import {
  childProgressSummary,
  fmtElapsed,
  kindLabel,
  runIdentity,
  statusChipClass,
  statusLabel,
} from '../runDiscoveryDisplay'
import Skeleton from './Skeleton'

const KIND_OPTIONS = ['direct-agent', 'worker', 'verifier', 'orchestrator']
const STATUS_OPTIONS = [
  'queued', 'running', 'waiting_input', 'waiting_approval', 'completed', 'failed', 'cancelled', 'timed_out',
]

/** 展開區「其餘欄位」的 key-value 攤平:budget_summary 形狀不固定,逐鍵顯示,不整包 JSON dump。 */
function flattenEntries(value: unknown): [string, string][] {
  if (value === null || value === undefined) return []
  if (typeof value !== 'object') return [['值', String(value)]]
  if (Array.isArray(value)) {
    return value.map((item, i): [string, string] => [
      `[${i}]`,
      item !== null && typeof item === 'object' ? JSON.stringify(item) : String(item),
    ])
  }
  return Object.entries(value as Record<string, unknown>).map(([k, v]): [string, string] => [
    k,
    v !== null && typeof v === 'object' ? JSON.stringify(v) : String(v),
  ])
}

/** id → 顯示名稱的兩張反查表(目錄 API 取不到時為空 Map,不影響任何渲染)。 */
type NameMaps = { agents: Map<string, string>; orchestrators: Map<string, string> }

/**
 * 名稱是純裝飾:查得到就在既有的短 GUID 前面補人類可讀名稱,查不到原樣退回 runIdentity()。
 * 比照 TriggersView 的 targetLabel(),client-side 反查已在畫面上的目錄清單,零後端契約變更。
 */
function displayIdentity(run: RunSummaryItem, names: NameMaps): string {
  const identity = runIdentity(run)
  const id = run.kind === 'orchestrator' ? run.orchestratorId : run.agentId
  if (!id) return identity
  const name = (run.kind === 'orchestrator' ? names.orchestrators : names.agents).get(id)
  return name ? `${name} · ${identity}` : identity
}

function RunRow({ run, names }: { run: RunSummaryItem; names: NameMaps }) {
  const childSummary = childProgressSummary(run)
  const budgetEntries = flattenEntries(run.budgetSummary)
  const identity = displayIdentity(run, names)
  return (
    <article className="agent-block">
      <div className="agent-test-console__actions">
        <span className="badge badge--user">{kindLabel(run.kind)}</span>
        <span className={`chip ${statusChipClass(run.status)}`}>{statusLabel(run.status)}</span>
        {run.pendingApproval && (
          <span className="chip chip--warn">待核准 · 請至「Run 核准」處理</span>
        )}
        {run.needsRecovery && <span className="chip chip--failed">需要復原</span>}
      </div>
      <dl className="agent-test-console__summary">
        <div><dt>身分</dt><dd>{identity}</dd></div>
        <div><dt>建立時間</dt><dd>{fmtDate(run.createdAt)}</dd></div>
        <div><dt>完成時間</dt><dd>{run.completedAt ? fmtDate(run.completedAt) : '—'}</dd></div>
        <div><dt>耗時</dt><dd>{fmtElapsed(run.elapsedSeconds)}</dd></div>
        {childSummary && <div><dt>子任務</dt><dd>{childSummary}</dd></div>}
      </dl>
      <details>
        <summary>展開其餘欄位</summary>
        <dl className="agent-test-console__summary">
          <div><dt>ID</dt><dd>{run.id}</dd></div>
          <div><dt>Task ID</dt><dd>{run.taskId ?? '—'}</dd></div>
          <div><dt>協作 Root Run ID</dt><dd>{run.orchestratorRootRunId ?? '—'}</dd></div>
          <div><dt>Workflow</dt><dd>{run.workflowId ? `${run.workflowId.slice(0, 8)} · r${run.workflowRevision}` : '—'}</dd></div>
          <div><dt>取消要求</dt><dd>{run.cancelRequested ? '是' : '否'}</dd></div>
          <div><dt>最後事件</dt><dd>{run.lastEventType ?? '—'}{run.lastEventAt ? `（${fmtDate(run.lastEventAt)}）` : ''}</dd></div>
          <div><dt>錯誤類別</dt><dd>{run.errorClass ?? '—'}</dd></div>
          <div><dt>開始時間</dt><dd>{run.startedAt ? fmtDate(run.startedAt) : '—'}</dd></div>
          <div><dt>更新時間</dt><dd>{fmtDate(run.updatedAt)}</dd></div>
        </dl>
        {budgetEntries.length > 0 && (
          <>
            <p className="muted">預算摘要</p>
            <dl className="agent-test-console__summary">
              {budgetEntries.map(([key, val]) => <div key={key}><dt>{key}</dt><dd>{val}</dd></div>)}
            </dl>
          </>
        )}
      </details>
    </article>
  )
}

/**
 * O2「執行總覽」(04-operations-trigger-plan.md §3):唯讀、owner-scoped 的跨 Agent/協作 run
 * 統一清單。閘門(runDiscoveryEnabled && workflow.manage)由 AppShell 側欄與掛載共同把關,
 * 這裡只管呈現與過濾——不提供任意 state edit,cancel/resume/approve 動作留在既有主控台/Approvals。
 */
export default function RunsView() {
  const [status, setStatus] = useState('')
  const [kind, setKind] = useState('')
  const [items, setItems] = useState<RunSummaryItem[]>([])
  const [cursor, setCursor] = useState<string | null>(null)
  const [hasMore, setHasMore] = useState(false)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  // 世代守衛:避免快速切換過濾條件或連續「載入更多」時,較舊的回應晚到覆寫較新的畫面狀態。
  const generationRef = useRef(0)
  // 名稱反查用的目錄:兩支 API 各自有獨立的 flag + 權限(listAgents 需 agentBuilderEnabled &&
  // ADMIN,listOrchestrators 需 workflowDesignerEnabled && workflow.manage),對本畫面的合法
  // 使用者很可能回 404/403。因此完全 fail-open:error/loading 一律忽略,不顯示訊息、不擋主清單。
  const agentsRes = useResource(listAgents)
  const orchestratorsRes = useResource(listOrchestrators)
  const names = useMemo<NameMaps>(() => ({
    agents: new Map((agentsRes.data ?? []).map((a) => [a.id, a.name])),
    orchestrators: new Map((orchestratorsRes.data ?? []).map((o) => [o.id, o.name])),
  }), [agentsRes.data, orchestratorsRes.data])

  const load = useCallback(async (cursorArg: string | null) => {
    const generation = ++generationRef.current
    setLoading(true)
    setError(null)
    try {
      const page = await listRuns({
        status: status || undefined,
        kind: kind || undefined,
        cursor: cursorArg ?? undefined,
      })
      if (generation !== generationRef.current) return
      setItems((prev) => (cursorArg ? [...prev, ...page.items] : page.items))
      setCursor(page.cursor)
      setHasMore(page.hasMore)
    } catch (e) {
      if (generation === generationRef.current) setError((e as Error).message)
    } finally {
      if (generation === generationRef.current) setLoading(false)
    }
  }, [status, kind])

  useEffect(() => { void load(null) }, [load])

  return (
    <section className="agent-block" aria-busy={loading}>
      <h2>執行總覽</h2>
      <p className="muted">
        你自己可查看的 Direct/Worker/Verifier 與協作 root 執行紀錄;不提供任意狀態編輯,
        取消/恢復/核准請至各自主控台或「Run 核准」進行。
      </p>

      <div className="field-row">
        <div className="field">
          <label htmlFor="runs-status-filter">狀態</label>
          <select id="runs-status-filter" value={status} onChange={(e) => setStatus(e.target.value)}>
            <option value="">全部</option>
            {STATUS_OPTIONS.map((s) => <option key={s} value={s}>{statusLabel(s)}</option>)}
          </select>
        </div>
        <div className="field">
          <label htmlFor="runs-kind-filter">種類</label>
          <select id="runs-kind-filter" value={kind} onChange={(e) => setKind(e.target.value)}>
            <option value="">全部</option>
            {KIND_OPTIONS.map((k) => <option key={k} value={k}>{kindLabel(k)}</option>)}
          </select>
        </div>
      </div>

      {error && <div className="agent-errors" role="alert">
        載入失敗：{error}
        <button className="btn" type="button" onClick={() => void load(null)}>重新載入</button>
      </div>}

      {loading && items.length === 0 && !error ? <Skeleton rows={4} /> : null}

      {!loading && !error && items.length === 0 && <p className="muted">目前沒有可顯示的執行紀錄。</p>}

      {items.map((run) => <RunRow key={run.id} run={run} names={names} />)}

      {hasMore && <button className="btn" type="button" disabled={loading} onClick={() => void load(cursor)}>載入更多</button>}
    </section>
  )
}
