import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react'
import {
  createOrchestrator, getOrchestrator, listOrchestratorRevisions, listOrchestrators,
  publishOrchestrator, putOrchestratorDraft, restoreOrchestratorRevision, validateOrchestrator,
} from '../api/orchestrators'
import { listAgents, listAgentToolCatalog } from '../api/agents'
import type {
  AgentRun, AgentRunEvent, AgentSummary, AgentToolCatalogEntry, Orchestrator, OrchestratorDraft,
  WorkflowDefinition, WorkflowNodeType, WorkflowRevision, WorkflowTraceEntry, WorkflowUiMetadata,
} from '../types'
import { useResource } from '../hooks/useResource'
import ErrorText from './ErrorText'
import Skeleton from './Skeleton'
import { requireLoaded, runWithToast, useToast } from './Toast'
import RevisionList from './RevisionList'
import CatalogPicker from './CatalogPicker'
import { cancelOrchestratorRun, getOrchestratorRun, getOrchestratorRunEvents, newIdempotencyKey, startOrchestratorRun } from '../api/orchestratorRuns'
import { getSession } from '../api/auth'
import {
  ACTIVE_RUN_STATUSES, isAmbiguousFailure, mergeRunEvents, POLL_MS, TERMINAL_RUN_STATUSES,
  withAcceptedCancelStatus,
} from '../agentRunDisplay'
import {
  clearOrchestratorRunState,
  messageFingerprint,
  readOrchestratorRunState,
  type StoredOrchestratorRun,
  writeOrchestratorRunState,
} from '../orchestratorRunState'
import { safeOrchestratorBudget, toOrchestratorTraceEvent } from '../orchestratorTrace'
import { listWorkflowNodeCatalog, listWorkflowRevisions } from '../api/workflows'
import WorkflowDesigner from '../workflowDesigner/WorkflowDesigner'

const emptyDraft = (): OrchestratorDraft => ({
  name: '',
  description: '',
  instructions: '',
  policy: {
    dispatchMode: 'bounded-parallel',
    joinPolicy: 'fail-fast',
    repairPolicy: 'fail',
    aggregationPolicy: 'verified-only',
    denialPolicy: 'fail-closed',
  },
  workflow: { id: '', revision: 0 },
  workerPool: [],
  workerPolicy: { requiredAudience: [], requiredCapabilities: [], selection: 'pinned-only' },
  context: { readOnly: true, allowedTools: [], knowledgeSources: [] },
  audience: [],
  capabilities: [],
  verifier: {
    agentId: '',
    revision: 0,
    variant: 'read-only',
    outputContract: { type: 'verification-report' },
    independent: true,
  },
  budgets: {
    maxContextRounds: 2,
    maxTasks: 8,
    maxChildRuns: 9,
    maxConcurrency: 4,
    maxRepairRounds: 1,
    tokenBudget: 10000,
    timeoutSeconds: 300,
  },
})

function storageScope(): string {
  const session = getSession()
  return session ? `${session.tenantCode}:${session.username}` : 'anonymous'
}

function runtimeNodeTrace(events: AgentRunEvent[]): WorkflowTraceEntry[] {
  const trace = new Map<string, WorkflowTraceEntry>()
  for (const event of events) {
    const payload = event.payload && typeof event.payload === 'object' && !Array.isArray(event.payload)
      ? event.payload as Record<string, unknown> : {}
    const nodeId = typeof payload.node_id === 'string' ? payload.node_id : typeof payload.nodeId === 'string' ? payload.nodeId : null
    const status = typeof payload.node_status === 'string' ? payload.node_status : typeof payload.status === 'string' ? payload.status : null
    if (nodeId && status) trace.set(nodeId, { node_id: nodeId, status })
  }
  return [...trace.values()]
}

function TraceOverlay({ run, events, definition, metadata, catalog }: {
  run: AgentRun
  events: AgentRunEvent[]
  definition: WorkflowDefinition | null
  metadata: WorkflowUiMetadata | null
  catalog: WorkflowNodeType[]
}) {
  const budget = safeOrchestratorBudget(run)
  return <details className="agent-test-console__trace" open>
    <summary>已淨化的 root／子任務追蹤</summary>
    <dl className="agent-test-console__summary">
      <div><dt>Root 執行</dt><dd><code>{run.runId}</code></dd></div>
      <div><dt>狀態</dt><dd>{run.status}</dd></div>
      <div><dt>Workflow 版本</dt><dd>{run.pinnedWorkflowRevision ?? '?'}</dd></div>
    </dl>
    {budget.length > 0 && (
      <dl className="agent-test-console__summary">
        {budget.map(([key, value]) => <div key={key}><dt>{key}</dt><dd>{String(value ?? '?')}</dd></div>)}
      </dl>
    )}
    <ol className="agent-test-console__events">{events.map((event) => {
      const item = toOrchestratorTraceEvent(event); const child = item.child
      return <li key={item.sequence}>
        <div className="agent-test-console__event-head">
          <code>#{item.sequence}</code>
          <strong>{item.eventType}</strong>
          {item.rootStatus && <span>root: {item.rootStatus}</span>}
        </div>
        {child && (
          <dl className="agent-test-console__summary">
            <div><dt>子任務執行</dt><dd>{child.childId ?? '?'}</dd></div>
            <div><dt>任務／嘗試次數</dt><dd>{child.taskId ?? '?'} / {child.attempt ?? '?'}</dd></div>
            <div><dt>種類／狀態</dt><dd>{child.kind ?? '?'} / {child.status ?? '?'}</dd></div>
            <div><dt>Agent</dt><dd>{child.agentId ?? '?'}{child.agentRevision === null ? '' : ` r${child.agentRevision}`}</dd></div>
            {child.verdict && <div><dt>判定</dt><dd>{child.verdict}</dd></div>}
            {child.citations.length > 0 && (
              <div>
                <dt>引用來源</dt>
                <dd>
                  {child.citations.map((citation) => <span key={citation.id}>{citation.id}{citation.title ? ` (${citation.title})` : ''} </span>)}
                </dd>
              </div>
            )}
          </dl>
        )}
      </li>
    })}</ol>
    {definition && metadata && catalog.length > 0 && (
      <section aria-label="Pinned workflow runtime trace">
        <h5>已鎖定 Workflow 追蹤</h5>
        <WorkflowDesigner
          definition={definition}
          uiMetadata={metadata}
          catalog={catalog}
          validation={null}
          simulation={null}
          runtimeTrace={runtimeNodeTrace(events)}
          disabled
          onChange={() => {}}
        />
      </section>
    )}
  </details>
}

function TestRunConsole({ orchestrator }: { orchestrator: Orchestrator }) {
  const [message, setMessage] = useState('')
  const [run, setRun] = useState<AgentRun | null>(null)
  const [events, setEvents] = useState<AgentRunEvent[]>([])
  const [error, setError] = useState<string | null>(null)
  const [starting, setStarting] = useState(false)
  const [cancelling, setCancelling] = useState(false)
  const [pinnedWorkflow, setPinnedWorkflow] = useState<WorkflowRevision | null>(null)
  const [workflowCatalog, setWorkflowCatalog] = useState<WorkflowNodeType[]>([])
  const recordRef = useRef<StoredOrchestratorRun | null>(null)
  const cursorRef = useRef(0)
  const runIdRef = useRef<string | null>(null)

  const scope = storageScope()
  useEffect(() => {
    let disposed = false
    void Promise.all([listWorkflowRevisions(orchestrator.draft.workflow.id), listWorkflowNodeCatalog()]).then(([revisions, catalog]) => {
      if (!disposed) { setPinnedWorkflow(revisions.find((revision) => revision.revision === orchestrator.draft.workflow.revision) ?? null); setWorkflowCatalog(catalog) }
    }).catch(() => { if (!disposed) { setPinnedWorkflow(null); setWorkflowCatalog([]) } })
    return () => { disposed = true }
  }, [orchestrator.draft.workflow.id, orchestrator.draft.workflow.revision])
  const persist = useCallback((record: StoredOrchestratorRun | null) => {
    recordRef.current = record
    if (record) writeOrchestratorRunState(scope, record)
    else clearOrchestratorRunState(scope, orchestrator.id)
  }, [orchestrator.id, scope])

  const applyPage = useCallback((page: Awaited<ReturnType<typeof getOrchestratorRunEvents>>, runId: string) => {
    if (runIdRef.current !== runId) return
    setEvents((current) => mergeRunEvents(current, page.events))
    cursorRef.current = Math.max(cursorRef.current, page.latestEventSequence, ...page.events.map((event) => event.sequence))
    if (recordRef.current?.runId === runId) persist({ ...recordRef.current, eventCursor: cursorRef.current })
  }, [persist])

  useEffect(() => {
    const record = readOrchestratorRunState(scope, orchestrator.id)
    recordRef.current = record
    // Rebuild the safe, redacted trace from the authoritative event history after a reload.
    // Sequence merging makes this safe even if the persisted cursor was stale.
    cursorRef.current = 0
    if (!record?.runId) return
    const runId = record.runId
    runIdRef.current = runId
    let disposed = false
    void Promise.all([getOrchestratorRun(runId), getOrchestratorRunEvents(runId, cursorRef.current)])
      .then(([restored, page]) => {
        if (disposed || runIdRef.current !== runId) return
        setRun(restored); applyPage(page, runId)
        if (TERMINAL_RUN_STATUSES.has(restored.status)) persist(null)
      })
      .catch((reason) => { if (!disposed) setError(`Unable to restore active run: ${(reason as Error).message}`) })
    return () => { disposed = true }
  }, [applyPage, orchestrator.id, persist, scope])

  const polledRunId = run?.runId
  const polledRunStatus = run?.status
  useEffect(() => {
    if (!polledRunId || !polledRunStatus || !ACTIVE_RUN_STATUSES.has(polledRunStatus)) return
    const runId = polledRunId
    runIdRef.current = runId
    let disposed = false
    let timer: ReturnType<typeof setTimeout> | undefined
    const poll = async () => {
      try {
        const [next, page] = await Promise.all([getOrchestratorRun(runId), getOrchestratorRunEvents(runId, cursorRef.current)])
        if (disposed || runIdRef.current !== runId) return
        setRun(next); applyPage(page, runId); setError(null)
        if (TERMINAL_RUN_STATUSES.has(next.status)) persist(null)
        else timer = setTimeout(() => void poll(), POLL_MS)
      } catch (reason) {
        if (!disposed && runIdRef.current === runId) { setError((reason as Error).message); timer = setTimeout(() => void poll(), POLL_MS) }
      }
    }
    void poll()
    return () => { disposed = true; if (timer) clearTimeout(timer) }
  }, [applyPage, persist, polledRunId, polledRunStatus])

  async function start() {
    const trimmed = message.trim()
    if (!trimmed || starting || (run && !TERMINAL_RUN_STATUSES.has(run.status))) return
    const fingerprint = messageFingerprint(trimmed)
    const reusable = recordRef.current?.runId === null && recordRef.current.messageFingerprint === fingerprint
    const record: StoredOrchestratorRun = reusable && recordRef.current
      ? recordRef.current
      : {
          version: 1,
          orchestratorId: orchestrator.id,
          runId: null,
          conversationId: globalThis.crypto.randomUUID(),
          startKey: newIdempotencyKey(),
          messageFingerprint: fingerprint,
          eventCursor: 0,
          cancelKey: null,
          cancelAccepted: false,
        }
    persist(record); cursorRef.current = 0; runIdRef.current = null; setRun(null); setEvents([]); setStarting(true); setError(null)
    try {
      const started = await startOrchestratorRun(orchestrator.id, trimmed, record.conversationId, record.startKey)
      const accepted = { ...record, runId: started.runId }
      persist(accepted); runIdRef.current = started.runId; setRun(started)
    } catch (reason) {
      if (!isAmbiguousFailure(reason)) persist(null)
      setError((reason as Error).message)
    } finally { setStarting(false) }
  }

  async function cancel() {
    if (!run || cancelling || TERMINAL_RUN_STATUSES.has(run.status)) return
    const record = recordRef.current
    if (!record || record.runId !== run.runId) return
    const cancelKey = record.cancelKey ?? newIdempotencyKey()
    persist({ ...record, cancelKey, cancelAccepted: false }); setCancelling(true); setError(null)
    try {
      const cancelled = await cancelOrchestratorRun(run.runId, cancelKey)
      const accepted = { ...recordRef.current!, cancelKey, cancelAccepted: true }
      persist(accepted); setRun(withAcceptedCancelStatus(cancelled, true))
      if (TERMINAL_RUN_STATUSES.has(cancelled.status)) persist(null)
    } catch (reason) {
      if (!isAmbiguousFailure(reason)) persist({ ...recordRef.current!, cancelKey: null, cancelAccepted: false })
      setError((reason as Error).message)
    } finally { setCancelling(false) }
  }

  if (orchestrator.published_revision == null) return <p className="muted">請先發布一個版本才能測跑。</p>
  const active = !!run && !TERMINAL_RUN_STATUSES.has(run.status)
  return <section className="agent-block agent-test-console">
    <h4>系統管理者測試執行</h4>
    <textarea
      className="input"
      value={message}
      disabled={active || starting || cancelling}
      onChange={(event) => setMessage(event.target.value)}
      placeholder="測試訊息"
    />
    <div className="agent-actions">
      <button
        className="btn btn--primary"
        disabled={!message.trim() || active || starting || cancelling}
        onClick={() => void start()}
      >{starting ? '啟動中…' : '開始'}</button>
      {run && (
        <button className="btn btn--danger" disabled={!active || cancelling} onClick={() => void cancel()}>
          {cancelling || run.status === 'cancelling' ? '取消中…' : '取消'}
        </button>
      )}
    </div>
    {run && <p className="muted"><code>{run.runId}</code> · {run.status} · workflow r{run.pinnedWorkflowRevision ?? '?'}</p>}
    <ErrorText msg={error} />
    {run && (
      <TraceOverlay
        run={run}
        events={events}
        definition={pinnedWorkflow?.definition ?? null}
        metadata={pinnedWorkflow?.ui_metadata ?? null}
        catalog={workflowCatalog}
      />
    )}
  </section>
}

function PolicyEditor({ value, disabled = false, onChange }: { value: OrchestratorDraft['policy']; disabled?: boolean; onChange: (value: OrchestratorDraft['policy']) => void }) {
  return <div className="agent-runtime-grid">
    <div className="field">
      <label>彙整策略
        <select
          className="input"
          disabled={disabled}
          value={value.joinPolicy}
          onChange={(e) => onChange({ ...value, joinPolicy: e.target.value as OrchestratorDraft['policy']['joinPolicy'] })}
        >
          <option value="fail-fast">fail-fast</option>
          <option value="allow-partial">allow-partial</option>
          <option value="repair">repair</option>
        </select>
      </label>
    </div>
    <div className="field">
      <label>修復策略
        <select
          className="input"
          disabled={disabled}
          value={value.repairPolicy}
          onChange={(e) => onChange({ ...value, repairPolicy: e.target.value as OrchestratorDraft['policy']['repairPolicy'] })}
        >
          <option value="fail">fail</option>
          <option value="redispatch">redispatch</option>
        </select>
      </label>
    </div>
    <p className="muted">dispatch=bounded-parallel · aggregation=verified-only · denial=fail-closed</p>
  </div>
}

function WorkerPolicyEditor({ value, disabled = false, onChange }: { value: OrchestratorDraft['workerPolicy']; disabled?: boolean; onChange: (value: OrchestratorDraft['workerPolicy']) => void }) {
  const lines = (text: string) => text.split('\n').map((x) => x.trim()).filter(Boolean)
  return <div className="agent-runtime-grid">
    <div className="field">
      <label>Worker 必要對象
        <textarea
          className="input"
          disabled={disabled}
          value={value.requiredAudience.join('\n')}
          onChange={(e) => onChange({ ...value, requiredAudience: lines(e.target.value) })}
        />
      </label>
    </div>
    <div className="field">
      <label>Worker 必要權限
        <textarea
          className="input"
          disabled={disabled}
          value={value.requiredCapabilities.join('\n')}
          onChange={(e) => onChange({ ...value, requiredCapabilities: lines(e.target.value) })}
        />
      </label>
    </div>
    <p className="muted">selection=pinned-only</p>
  </div>
}

const JSON_FIELD_ERROR = 'JSON 格式錯誤,請修正後才能繼續。'
type JsonKey = 'context' | 'workerPool' | 'verifier' | 'budgets'

/**
 * JSON 欄位:受控 textarea(文字由父層持有),解析失敗時顯示欄位錯誤,並由父層停用
 * create/save/validate/publish——畫面永遠等於使用者輸入,也不會送出上一次的合法值。
 */
function JsonField({ id, label, text, error, disabled, onChange }: { id: string; label: string; text: string; error?: string; disabled?: boolean; onChange: (text: string) => void }) {
  return <div className="field"><label htmlFor={id}>{label}</label>
    <textarea id={id} className="input" disabled={disabled} value={text} aria-invalid={!!error} aria-describedby={error ? `${id}-error` : undefined} onChange={(e) => onChange(e.target.value)} />
    <ErrorText msg={error ?? null} id={`${id}-error`} />
  </div>
}

/** 載入/重新載入後用最新 draft 重新 seed 顯示文字(取代原本靠 key remount 的刷新)。 */
function jsonTexts(draft: OrchestratorDraft): Record<JsonKey, string> {
  return {
    context: JSON.stringify(draft.context, null, 2),
    workerPool: JSON.stringify(draft.workerPool, null, 2),
    verifier: JSON.stringify(draft.verifier, null, 2),
    budgets: JSON.stringify(draft.budgets, null, 2),
  }
}

/**
 * W5(規格 §5):structured/raw 雙模開關——結構化表單是預設,裸 JSON 只作為進階逃生口
 * 保留(defense in depth,見規格 §5.3)。fieldName 讓四個欄位各自的切換按鈕有獨立可辨識文字。
 */
function StructuredOrRaw({
  fieldName,
  advanced,
  onToggleAdvanced,
  structured,
  raw,
}: {
  fieldName: string
  advanced: boolean
  onToggleAdvanced: () => void
  structured: ReactNode
  raw: ReactNode
}) {
  return (
    <>
      <button type="button" className="btn" onClick={onToggleAdvanced}>
        {fieldName} — {advanced ? '切換為結構化編輯' : '切換為進階 JSON 模式'}
      </button>
      {advanced ? raw : structured}
    </>
  )
}

/**
 * §5.5:Verifier/Worker 候選過濾——「已發布」是硬性前提;`published_execution_roles`
 * 若有值,進一步要求包含該角色(避免選到 Worker harness 當 Verifier 之類的錯配)。
 * fail-open:欄位缺席或 legacy null(舊後端未回傳)一律視為「不過濾角色」,絕不能讓下拉
 * 因為缺欄位而整組變空——伺服器 validate/publish 才是最終權威,這裡只是減少誤觸。
 */
function agentPublishedForRole(agent: AgentSummary, role: 'worker' | 'verifier'): boolean {
  if (agent.published_revision == null) return false
  const roles = agent.published_execution_roles
  return roles == null || roles.includes(role)
}

/** 已發布 Agent 的 id + revision 選取列;workerPool/verifier 共用(§5.1:資料源 listAgents)。
 * agentsError/agentsLoading 傳入 CatalogPicker 由它自行決定 fail-open 為手動輸入。 */
function AgentRefRow({
  idPrefix,
  agentLabel,
  revisionLabel,
  value,
  agents,
  agentsError,
  agentsLoading,
  role,
  disabled,
  onChange,
  onRemove,
}: {
  idPrefix: string
  agentLabel: string
  revisionLabel: string
  value: { agentId: string; revision: number }
  agents: AgentSummary[] | null
  agentsError: string | null
  agentsLoading: boolean
  role: 'worker' | 'verifier'
  disabled?: boolean
  onChange: (next: { agentId: string; revision: number }) => void
  onRemove?: () => void
}) {
  return (
    <div className="agent-runtime-grid">
      <CatalogPicker
        id={`${idPrefix}-agent`}
        label={agentLabel}
        value={value.agentId}
        disabled={disabled}
        items={agents ? agents.filter((a) => agentPublishedForRole(a, role)) : null}
        itemsError={agentsError}
        itemsLoading={agentsLoading}
        optionValue={(a) => a.id}
        optionLabel={(a) => `${a.name}${a.published_revision != null ? ` (r${a.published_revision})` : ''}`}
        placeholder="Agent id"
        onChange={(agentId) => {
          const picked = agents?.find((a) => a.id === agentId)
          onChange({ agentId, revision: picked?.published_revision ?? value.revision })
        }}
      />
      <div className="field">
        <label htmlFor={`${idPrefix}-revision`}>
          {revisionLabel}
          <input
            id={`${idPrefix}-revision`}
            className="input"
            type="number"
            min={1}
            disabled={disabled}
            value={value.revision}
            onChange={(e) => onChange({ ...value, revision: Number(e.target.value) })}
          />
        </label>
      </div>
      {onRemove && !disabled && (
        <button type="button" className="btn btn--danger" onClick={onRemove}>
          移除
        </button>
      )}
    </div>
  )
}

function WorkerPoolEditor({
  idPrefix,
  items,
  agents,
  agentsError,
  agentsLoading,
  disabled,
  onChange,
}: {
  idPrefix: string
  items: OrchestratorDraft['workerPool']
  agents: AgentSummary[] | null
  agentsError: string | null
  agentsLoading: boolean
  disabled?: boolean
  onChange: (next: OrchestratorDraft['workerPool']) => void
}) {
  return (
    <div className="field">
      <label>Worker pool(已發布 Agent,明確指定 revision)</label>
      {items.length === 0 && (
        <p className="agent-set__empty" role="note">
          尚未加入任何 Worker——沒有 Worker 無法建立/發布。
        </p>
      )}
      {items.map((it, index) => (
        <AgentRefRow
          key={index}
          idPrefix={`${idPrefix}-${index}`}
          agentLabel="Worker Agent id"
          revisionLabel="Worker revision"
          value={it}
          agents={agents}
          agentsError={agentsError}
          agentsLoading={agentsLoading}
          role="worker"
          disabled={disabled}
          onChange={(next) => onChange(items.map((x, i) => (i === index ? next : x)))}
          onRemove={() => onChange(items.filter((_, i) => i !== index))}
        />
      ))}
      {!disabled && (
        <button
          type="button"
          className="btn"
          onClick={() => onChange([...items, { agentId: '', revision: 0 }])}
        >
          ＋ 加入 Worker
        </button>
      )}
    </div>
  )
}

function VerifierRefEditor({
  idPrefix,
  value,
  agents,
  agentsError,
  agentsLoading,
  disabled,
  onChange,
}: {
  idPrefix: string
  value: { agentId: string; revision: number }
  agents: AgentSummary[] | null
  agentsError: string | null
  agentsLoading: boolean
  disabled?: boolean
  onChange: (next: { agentId: string; revision: number }) => void
}) {
  return (
    <div className="field">
      <label>Verifier(唯讀查核者,已發布 Agent)</label>
      <p className="muted agent-set__hint">
        Verifier 一律固定為唯讀查核 harness,一般 Worker 不會被伺服器接受為 Verifier;此清單僅供選取,實際校驗仍以伺服器 validate/publish 為準。
      </p>
      <AgentRefRow
        idPrefix={idPrefix}
        agentLabel="Verifier Agent id"
        revisionLabel="Verifier revision"
        value={value}
        agents={agents}
        agentsError={agentsError}
        agentsLoading={agentsLoading}
        role="verifier"
        disabled={disabled}
        onChange={onChange}
      />
    </div>
  )
}

const BUDGET_FIELDS: [keyof OrchestratorDraft['budgets'], string][] = [
  ['maxContextRounds', 'Context 輪數上限'],
  ['maxTasks', '任務數上限'],
  ['maxChildRuns', 'Child run 數上限'],
  ['maxConcurrency', '併發數上限'],
  ['maxRepairRounds', '修復輪數上限'],
  ['tokenBudget', 'Token 預算'],
  ['timeoutSeconds', '逾時秒數'],
]

/** 數字輸入格 grid;比照 AgentEditor.tsx 的 AgentRuntimeLimitsSection 既有模式(規格 §5.2 分類 2)。 */
function BudgetsEditor({
  idPrefix,
  value,
  disabled,
  onChange,
}: {
  idPrefix: string
  value: OrchestratorDraft['budgets']
  disabled?: boolean
  onChange: (next: OrchestratorDraft['budgets']) => void
}) {
  return (
    <div className="field">
      <label>Budgets(上線資源上限)</label>
      <div className="agent-runtime-grid">
        {BUDGET_FIELDS.map(([key, label]) => (
          <div className="field" key={key}>
            <label htmlFor={`${idPrefix}-${key}`}>
              {label}
              <input
                id={`${idPrefix}-${key}`}
                className="input"
                type="number"
                min={0}
                disabled={disabled}
                value={value[key]}
                onChange={(e) => {
                  const n = Number(e.target.value)
                  onChange({ ...value, [key]: Number.isFinite(n) && n >= 0 ? n : 0 })
                }}
              />
            </label>
          </div>
        ))}
      </div>
    </div>
  )
}

/** Context 一律唯讀(readOnly:true 是型別上的字面量,無法也不需要編輯);allowedTools 從
 * Tool Catalog 勾選(規格 §5.2 分類 1),knowledgeSources 沿用本檔既有的「每行一項」慣例。 */
function ContextEditor({
  value,
  toolCatalog,
  toolCatalogError,
  disabled,
  onChange,
}: {
  value: OrchestratorDraft['context']
  toolCatalog: AgentToolCatalogEntry[]
  toolCatalogError: string | null
  disabled?: boolean
  onChange: (next: OrchestratorDraft['context']) => void
}) {
  function toggleTool(name: string) {
    onChange({
      ...value,
      allowedTools: value.allowedTools.includes(name)
        ? value.allowedTools.filter((t) => t !== name)
        : [...value.allowedTools, name],
    })
  }
  return (
    <div className="field">
      <label>Context(root run 可用的工具與知識來源,一律唯讀)</label>
      {toolCatalogError && (
        <p className="muted">工具目錄載入失敗,已保留目前設定,可用進階 JSON 模式調整。</p>
      )}
      {toolCatalog.length === 0 && !toolCatalogError ? (
        <p className="muted">目前工具目錄為空。</p>
      ) : (
        <div className="agent-roles">
          {toolCatalog.map((tool) => (
            <label key={tool.name} className="agent-check">
              <input
                type="checkbox"
                checked={value.allowedTools.includes(tool.name)}
                disabled={disabled}
                onChange={() => toggleTool(tool.name)}
              />
              {tool.name}
            </label>
          ))}
        </div>
      )}
      {value.allowedTools
        .filter((name) => !toolCatalog.some((tool) => tool.name === name))
        .map((name) => (
          <p key={name} className="muted">
            {name}(不在目前工具目錄中)
          </p>
        ))}
      <div className="field">
        <label htmlFor="orchestrator-context-knowledge">知識來源(每行一項)</label>
        <textarea
          id="orchestrator-context-knowledge"
          className="input"
          disabled={disabled}
          value={value.knowledgeSources.join('\n')}
          onChange={(e) =>
            onChange({
              ...value,
              knowledgeSources: e.target.value.split('\n').map((x) => x.trim()).filter(Boolean),
            })
          }
        />
      </div>
    </div>
  )
}

function Editor({ id, onClose, multiAgentDispatchEnabled }: { id: string; onClose: () => void; multiAgentDispatchEnabled: boolean }) {
  const toast = useToast(); const [item, setItem] = useState<Orchestrator | null>(null); const [draft, setDraft] = useState<OrchestratorDraft | null>(null)
  const [etag, setEtag] = useState<string | null>(null); const [blocked, setBlocked] = useState(false); const [errors, setErrors] = useState<string[]>([])
  const [texts, setTexts] = useState<Partial<Record<JsonKey, string>>>({}); const [jsonErrors, setJsonErrors] = useState<Partial<Record<JsonKey, string>>>({})
  // W5:四個結構化欄位各自的 structured/raw 顯示模式;預設全部結構化(false)。
  const [advancedFields, setAdvancedFields] = useState<Partial<Record<JsonKey, boolean>>>({})
  const agentsRes = useResource(listAgents)
  const toolsRes = useResource(listAgentToolCatalog)
  const revisions = useResource(useCallback(() => listOrchestratorRevisions(id), [id]))
  // 失敗在 load 本體收斂成可見錯誤：衝突鎖定後「重新載入」是唯一出口，它靜默失敗等於死路。
  const load = useCallback(async () => {
    try {
      const result = await getOrchestrator(id)
      setItem(result.data); setDraft(result.data.draft); setEtag(result.etag); setBlocked(false); setErrors([]); setTexts(jsonTexts(result.data.draft)); setJsonErrors({}); setAdvancedFields({})
    } catch (e) { setErrors([(e as Error).message]) }
  }, [id])
  useEffect(() => { void load() }, [load])
  if (!item || !draft) return <><button className="btn" onClick={onClose}>返回清單</button><Skeleton rows={4} /><ErrorText msg={errors[0] ?? null} /></>
  const jsonInvalid = Object.values(jsonErrors).some(Boolean)
  const disabled = blocked || !etag
  const onConflict = () => setBlocked(true)
  const update = (patch: Partial<OrchestratorDraft>) => { setDraft({ ...draft, ...patch }); setErrors([]) }
  const toggleAdvanced = (key: JsonKey) => setAdvancedFields((prev) => ({ ...prev, [key]: !prev[key] }))
  // 文字永遠寫回顯示;只有解析成功才更新 draft(parsedValue),失敗留下欄位錯誤把寫入動作鎖住。
  const editJson = (key: JsonKey, text: string) => {
    setTexts((prev) => ({ ...prev, [key]: text }))
    let parsed: unknown
    try { parsed = JSON.parse(text) } catch { setJsonErrors((prev) => ({ ...prev, [key]: JSON_FIELD_ERROR })); return }
    setJsonErrors((prev) => ({ ...prev, [key]: undefined }))
    update({ [key]: parsed } as Partial<OrchestratorDraft>)
  }
  // 結構化控制項的寫入路徑:直接更新 draft(typed),同時把 texts 重新序列化,讓「切換為
  // 進階 JSON 模式」看到的永遠是目前結構化值,而不是切換前殘留的舊字串。
  function setStructured<K extends JsonKey>(key: K, value: OrchestratorDraft[K]) {
    update({ [key]: value } as Partial<OrchestratorDraft>)
    setTexts((prev) => ({ ...prev, [key]: JSON.stringify(value, null, 2) }))
    setJsonErrors((prev) => ({ ...prev, [key]: undefined }))
  }
  // 衝突不在這裡吞（吞掉 = runWithToast 看不到失敗 → 假成功 toast），一律往外拋，
  // 由 runWithToast 的 onConflict 統一鎖定編輯器。
  // 守衛不成立 = UI 狀態與寫入前提脫節（disabled 失守），一律拋錯而非靜默返回。
  async function save() { await putOrchestratorDraft(id, requireLoaded(draft, '草稿'), requireLoaded(etag, '草稿版本')); await load() }
  async function validate() { const result = await validateOrchestrator(id, requireLoaded(etag, '草稿版本')); setErrors(result.errors.map((x) => x.message)) }
  return <>
    <div className="view__head"><h2 className="view__title">{item.name}</h2><button className="btn" onClick={onClose}>返回清單</button></div>
    {blocked && <div className="agent-errors" role="alert">草稿已過期，已鎖定所有寫入。<button className="btn" onClick={() => void load()}>重新載入</button></div>}
    <section className="agent-block">
      <div className="field">
        <label htmlFor="orchestrator-name">名稱</label>
        <input id="orchestrator-name" className="input" disabled={disabled} value={draft.name} onChange={(e) => update({ name: e.target.value })} />
      </div>
      <div className="field">
        <label htmlFor="orchestrator-description">說明</label>
        <input id="orchestrator-description" className="input" disabled={disabled} value={draft.description} onChange={(e) => update({ description: e.target.value })} />
      </div>
      <div className="field">
        <label htmlFor="orchestrator-instructions">Root 指示</label>
        <textarea id="orchestrator-instructions" className="input" disabled={disabled} value={draft.instructions} onChange={(e) => update({ instructions: e.target.value })} />
      </div>
      <p className="muted">Read-only Context、pinned Worker pool、Verifier 與 Root Workflow revision 由 server 在 validate/publish 時做 tenant、published 與角色相容性檢查。</p>
      <PolicyEditor value={draft.policy} disabled={disabled} onChange={(policy) => update({ policy })} />
      <div className="agent-runtime-grid">
        <div className="field">
          <label htmlFor="orchestrator-workflow-id">已鎖定的 Workflow id</label>
          <input
            id="orchestrator-workflow-id"
            className="input"
            disabled={disabled}
            value={draft.workflow.id}
            onChange={(e) => update({ workflow: { ...draft.workflow, id: e.target.value } })}
          />
        </div>
        <div className="field">
          <label htmlFor="orchestrator-workflow-revision">Workflow revision</label>
          <input
            id="orchestrator-workflow-revision"
            className="input"
            type="number"
            min={1}
            disabled={disabled}
            value={draft.workflow.revision}
            onChange={(e) => update({ workflow: { ...draft.workflow, revision: Number(e.target.value) } })}
          />
        </div>
      </div>
      <div className="field">
        <label htmlFor="orchestrator-audience">Audience（每行一項）</label>
        <textarea
          id="orchestrator-audience"
          className="input"
          disabled={disabled}
          value={draft.audience.join('\n')}
          onChange={(e) => update({ audience: e.target.value.split('\n').map((x) => x.trim()).filter(Boolean) })}
        />
      </div>
      <div className="field">
        <label htmlFor="orchestrator-capabilities">Capabilities（每行一項）</label>
        <textarea
          id="orchestrator-capabilities"
          className="input"
          disabled={disabled}
          value={draft.capabilities.join('\n')}
          onChange={(e) => update({ capabilities: e.target.value.split('\n').map((x) => x.trim()).filter(Boolean) })}
        />
      </div>
      <StructuredOrRaw
        fieldName="Context"
        advanced={!!advancedFields.context}
        onToggleAdvanced={() => toggleAdvanced('context')}
        structured={
          <ContextEditor
            value={draft.context}
            toolCatalog={toolsRes.data ?? []}
            toolCatalogError={toolsRes.error}
            disabled={disabled}
            onChange={(context) => setStructured('context', context)}
          />
        }
        raw={
          <JsonField id="orchestrator-context" label="Context(JSON 進階模式)" disabled={disabled} text={texts.context ?? ''} error={jsonErrors.context} onChange={(text) => editJson('context', text)} />
        }
      />
      <StructuredOrRaw
        fieldName="Worker pool"
        advanced={!!advancedFields.workerPool}
        onToggleAdvanced={() => toggleAdvanced('workerPool')}
        structured={
          <WorkerPoolEditor
            idPrefix="orchestrator-worker-pool"
            items={draft.workerPool}
            agents={agentsRes.data}
            agentsError={agentsRes.error}
            agentsLoading={agentsRes.loading}
            disabled={disabled}
            onChange={(workerPool) => setStructured('workerPool', workerPool)}
          />
        }
        raw={
          <JsonField
            id="orchestrator-worker-pool"
            label="Worker pool(JSON 進階模式)"
            disabled={disabled}
            text={texts.workerPool ?? ''}
            error={jsonErrors.workerPool}
            onChange={(text) => editJson('workerPool', text)}
          />
        }
      />
      <WorkerPolicyEditor value={draft.workerPolicy} disabled={disabled} onChange={(workerPolicy) => update({ workerPolicy })} />
      <StructuredOrRaw
        fieldName="Verifier"
        advanced={!!advancedFields.verifier}
        onToggleAdvanced={() => toggleAdvanced('verifier')}
        structured={
          <VerifierRefEditor
            idPrefix="orchestrator-verifier"
            value={{ agentId: draft.verifier.agentId, revision: draft.verifier.revision }}
            agents={agentsRes.data}
            agentsError={agentsRes.error}
            agentsLoading={agentsRes.loading}
            disabled={disabled}
            onChange={(ref) => setStructured('verifier', { ...draft.verifier, ...ref })}
          />
        }
        raw={
          <JsonField id="orchestrator-verifier" label="Verifier(JSON 進階模式)" disabled={disabled} text={texts.verifier ?? ''} error={jsonErrors.verifier} onChange={(text) => editJson('verifier', text)} />
        }
      />
      <StructuredOrRaw
        fieldName="Budgets"
        advanced={!!advancedFields.budgets}
        onToggleAdvanced={() => toggleAdvanced('budgets')}
        structured={
          <BudgetsEditor
            idPrefix="orchestrator-budget"
            value={draft.budgets}
            disabled={disabled}
            onChange={(budgets) => setStructured('budgets', budgets)}
          />
        }
        raw={
          <JsonField id="orchestrator-budgets" label="Budgets(JSON 進階模式)" disabled={disabled} text={texts.budgets ?? ''} error={jsonErrors.budgets} onChange={(text) => editJson('budgets', text)} />
        }
      />
    </section>
    <div className="agent-actions">
      <button
        className="btn btn--primary"
        disabled={disabled || jsonInvalid}
        onClick={() => void runWithToast(toast, save, { success: '草稿已儲存', onConflict })}
      >儲存</button>
      <button
        className="btn"
        disabled={disabled || jsonInvalid}
        onClick={() => void runWithToast(toast, validate, { success: '驗證完成', onConflict })}
      >驗證</button>
      <button
        className="btn btn--info"
        disabled={disabled || jsonInvalid || errors.length > 0}
        onClick={() => void runWithToast(
          toast,
          async () => { await publishOrchestrator(id, item.draft_version, requireLoaded(etag, '草稿版本')) },
          { success: '已發布', onSuccess: () => load(), onConflict },
        )}
      >發布</button>
    </div>
    {errors.length > 0 && <ul className="agent-errors">{errors.map((error) => <li key={error}>{error}</li>)}</ul>}
    {multiAgentDispatchEnabled && item.enabled && <TestRunConsole orchestrator={item} />}
    <section className="agent-block">
      <h4>Revision history</h4>
      <RevisionList
        loading={revisions.loading}
        revisions={revisions.data ?? []}
        successMessage="已建立新 revision"
        onRestore={(revision) => restoreOrchestratorRevision(id, revision)}
        onRestored={() => { void load(); void revisions.reload() }}
      />
    </section>
  </>
}

export default function OrchestratorsView({ multiAgentDispatchEnabled = false }: { multiAgentDispatchEnabled?: boolean }) {
  const toast = useToast(); const resource = useResource(listOrchestrators); const [editing, setEditing] = useState<string | null>(null)
  const [create, setCreate] = useState(false); const [draft, setDraft] = useState(emptyDraft)
  const [workerPoolText, setWorkerPoolText] = useState(() => JSON.stringify(emptyDraft().workerPool, null, 2))
  const [workerPoolError, setWorkerPoolError] = useState<string | undefined>(undefined)
  const [workerPoolAdvanced, setWorkerPoolAdvanced] = useState(false)
  const agentsRes = useResource(listAgents)
  if (editing) return <Editor id={editing} multiAgentDispatchEnabled={multiAgentDispatchEnabled} onClose={() => { setEditing(null); void resource.reload() }} />
  const editWorkerPool = (text: string) => {
    setWorkerPoolText(text)
    let parsed: unknown
    try { parsed = JSON.parse(text) } catch { setWorkerPoolError(JSON_FIELD_ERROR); return }
    setWorkerPoolError(undefined)
    setDraft({ ...draft, workerPool: parsed as OrchestratorDraft['workerPool'] })
  }
  const setCreateWorkerPool = (next: OrchestratorDraft['workerPool']) => {
    setDraft({ ...draft, workerPool: next })
    setWorkerPoolText(JSON.stringify(next, null, 2))
    setWorkerPoolError(undefined)
  }
  const refsReady = !!draft.workflow.id && draft.workflow.revision > 0 && !!draft.verifier.agentId && draft.verifier.revision > 0 && draft.workerPool.length > 0
  return <>
    <div className="skills__bar"><button className="btn btn--info" onClick={() => setCreate(!create)}>＋ 建立</button></div>
    {create && (
      <section className="agent-block">
        <div className="field">
          <label>名稱<input className="input" value={draft.name} onChange={(e) => setDraft({ ...draft, name: e.target.value })} /></label>
        </div>
        <div className="field">
          <label>說明<input className="input" value={draft.description} onChange={(e) => setDraft({ ...draft, description: e.target.value })} /></label>
        </div>
        <div className="agent-runtime-grid">
          <div className="field">
            <label>Root Workflow id
              <input
                className="input"
                value={draft.workflow.id}
                onChange={(e) => setDraft({ ...draft, workflow: { ...draft.workflow, id: e.target.value } })}
              />
            </label>
          </div>
          <div className="field">
            <label>Workflow revision
              <input
                className="input"
                type="number"
                min={1}
                value={draft.workflow.revision}
                onChange={(e) => setDraft({ ...draft, workflow: { ...draft.workflow, revision: Number(e.target.value) } })}
              />
            </label>
          </div>
        </div>
        <StructuredOrRaw
          fieldName="Worker pool"
          advanced={workerPoolAdvanced}
          onToggleAdvanced={() => setWorkerPoolAdvanced((a) => !a)}
          structured={
            <WorkerPoolEditor
              idPrefix="orchestrator-create-worker-pool"
              items={draft.workerPool}
              agents={agentsRes.data}
              agentsError={agentsRes.error}
              agentsLoading={agentsRes.loading}
              onChange={setCreateWorkerPool}
            />
          }
          raw={
            <JsonField id="orchestrator-create-worker-pool" label="Worker pool(JSON 進階模式)" text={workerPoolText} error={workerPoolError} onChange={editWorkerPool} />
          }
        />
        <VerifierRefEditor
          idPrefix="orchestrator-create-verifier"
          value={{ agentId: draft.verifier.agentId, revision: draft.verifier.revision }}
          agents={agentsRes.data}
          agentsError={agentsRes.error}
          agentsLoading={agentsRes.loading}
          onChange={(ref) => setDraft({ ...draft, verifier: { ...draft.verifier, ...ref } })}
        />
        <PolicyEditor value={draft.policy} onChange={(policy) => setDraft({ ...draft, policy })} />
        <WorkerPolicyEditor value={draft.workerPolicy} onChange={(workerPolicy) => setDraft({ ...draft, workerPolicy })} />
        <div className="field">
          <label>Audience（每行一項）
            <textarea
              className="input"
              value={draft.audience.join('\n')}
              onChange={(e) => setDraft({ ...draft, audience: e.target.value.split('\n').map((x) => x.trim()).filter(Boolean) })}
            />
          </label>
        </div>
        <div className="field">
          <label>Capabilities（每行一項）
            <textarea
              className="input"
              value={draft.capabilities.join('\n')}
              onChange={(e) => setDraft({ ...draft, capabilities: e.target.value.split('\n').map((x) => x.trim()).filter(Boolean) })}
            />
          </label>
        </div>
        <button
          className="btn btn--primary"
          disabled={!draft.name.trim() || !refsReady || !!workerPoolError}
          onClick={() => void runWithToast(toast, async () => { const item = await createOrchestrator(draft); setEditing(item.id) }, { success: '已建立 Orchestrator 草稿' })}
        >建立</button>
        <p className="muted">建立前需提供 pinned Workflow、至少一個 pinned Worker 與 pinned read-only Verifier；revision 必須明確指定，不使用 latest 或硬編碼。</p>
      </section>
    )}
    <ErrorText msg={resource.error} />
    {resource.loading ? <Skeleton rows={3} /> : (
      <div className="table-wrap">
        <table className="table">
          <thead><tr><th>名稱</th><th>發布</th><th>操作</th></tr></thead>
          <tbody>
            {(resource.data ?? []).map((row) => (
              <tr key={row.id}>
                <td>{row.name}<br /><span className="muted">{row.description}</span></td>
                <td>{row.published_revision == null ? '草稿' : `r${row.published_revision}`}</td>
                <td><button className="btn" onClick={() => setEditing(row.id)}>編輯</button></td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    )}
  </>
}
