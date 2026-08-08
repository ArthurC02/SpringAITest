import { useCallback, useEffect, useRef, useState } from 'react'
import {
  createOrchestrator, getOrchestrator, listOrchestratorRevisions, listOrchestrators,
  publishOrchestrator, putOrchestratorDraft, restoreOrchestratorRevision, validateOrchestrator,
} from '../api/orchestrators'
import type {
  AgentRun, AgentRunEvent, Orchestrator, OrchestratorDraft, WorkflowDefinition, WorkflowNodeType,
  WorkflowRevision, WorkflowTraceEntry, WorkflowUiMetadata,
} from '../types'
import { useResource } from '../hooks/useResource'
import ErrorText from './ErrorText'
import Skeleton from './Skeleton'
import { requireLoaded, runWithToast, useToast } from './Toast'
import RevisionList from './RevisionList'
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
    <summary>Redacted root / child trace</summary>
    <dl className="agent-test-console__summary">
      <div><dt>Root run</dt><dd><code>{run.runId}</code></dd></div>
      <div><dt>Status</dt><dd>{run.status}</dd></div>
      <div><dt>Workflow revision</dt><dd>{run.pinnedWorkflowRevision ?? '?'}</dd></div>
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
            <div><dt>Child run</dt><dd>{child.childId ?? '?'}</dd></div>
            <div><dt>Task / attempt</dt><dd>{child.taskId ?? '?'} / {child.attempt ?? '?'}</dd></div>
            <div><dt>Kind / status</dt><dd>{child.kind ?? '?'} / {child.status ?? '?'}</dd></div>
            <div><dt>Agent</dt><dd>{child.agentId ?? '?'}{child.agentRevision === null ? '' : ` r${child.agentRevision}`}</dd></div>
            {child.verdict && <div><dt>Verdict</dt><dd>{child.verdict}</dd></div>}
            {child.citations.length > 0 && (
              <div>
                <dt>Citations</dt>
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
        <h5>Pinned workflow trace</h5>
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

  if (orchestrator.published_revision == null) return <p className="muted">Publish a revision before test-running.</p>
  const active = !!run && !TERMINAL_RUN_STATUSES.has(run.status)
  return <section className="agent-block agent-test-console">
    <h4>System-admin test run</h4>
    <textarea
      className="input"
      value={message}
      disabled={active || starting || cancelling}
      onChange={(event) => setMessage(event.target.value)}
      placeholder="Test message"
    />
    <div className="agent-actions">
      <button
        className="btn btn--primary"
        disabled={!message.trim() || active || starting || cancelling}
        onClick={() => void start()}
      >{starting ? 'Starting…' : 'Start'}</button>
      {run && (
        <button className="btn btn--danger" disabled={!active || cancelling} onClick={() => void cancel()}>
          {cancelling || run.status === 'cancelling' ? 'Cancelling…' : 'Cancel'}
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
      <label>Join policy
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
      <label>Repair policy
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
      <label>Worker required audience
        <textarea
          className="input"
          disabled={disabled}
          value={value.requiredAudience.join('\n')}
          onChange={(e) => onChange({ ...value, requiredAudience: lines(e.target.value) })}
        />
      </label>
    </div>
    <div className="field">
      <label>Worker required capabilities
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

function Editor({ id, onClose, multiAgentDispatchEnabled }: { id: string; onClose: () => void; multiAgentDispatchEnabled: boolean }) {
  const toast = useToast(); const [item, setItem] = useState<Orchestrator | null>(null); const [draft, setDraft] = useState<OrchestratorDraft | null>(null)
  const [etag, setEtag] = useState<string | null>(null); const [blocked, setBlocked] = useState(false); const [errors, setErrors] = useState<string[]>([])
  const [texts, setTexts] = useState<Partial<Record<JsonKey, string>>>({}); const [jsonErrors, setJsonErrors] = useState<Partial<Record<JsonKey, string>>>({})
  const revisions = useResource(useCallback(() => listOrchestratorRevisions(id), [id]))
  // 失敗在 load 本體收斂成可見錯誤：衝突鎖定後「重新載入」是唯一出口，它靜默失敗等於死路。
  const load = useCallback(async () => {
    try {
      const result = await getOrchestrator(id)
      setItem(result.data); setDraft(result.data.draft); setEtag(result.etag); setBlocked(false); setErrors([]); setTexts(jsonTexts(result.data.draft)); setJsonErrors({})
    } catch (e) { setErrors([(e as Error).message]) }
  }, [id])
  useEffect(() => { void load() }, [load])
  if (!item || !draft) return <><button className="btn" onClick={onClose}>返回清單</button><Skeleton rows={4} /><ErrorText msg={errors[0] ?? null} /></>
  const jsonInvalid = Object.values(jsonErrors).some(Boolean)
  const disabled = blocked || !etag
  const onConflict = () => setBlocked(true)
  const update = (patch: Partial<OrchestratorDraft>) => { setDraft({ ...draft, ...patch }); setErrors([]) }
  // 文字永遠寫回顯示;只有解析成功才更新 draft(parsedValue),失敗留下欄位錯誤把寫入動作鎖住。
  const editJson = (key: JsonKey, text: string) => {
    setTexts((prev) => ({ ...prev, [key]: text }))
    let parsed: unknown
    try { parsed = JSON.parse(text) } catch { setJsonErrors((prev) => ({ ...prev, [key]: JSON_FIELD_ERROR })); return }
    setJsonErrors((prev) => ({ ...prev, [key]: undefined }))
    update({ [key]: parsed } as Partial<OrchestratorDraft>)
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
        <label htmlFor="orchestrator-instructions">Root instructions</label>
        <textarea id="orchestrator-instructions" className="input" disabled={disabled} value={draft.instructions} onChange={(e) => update({ instructions: e.target.value })} />
      </div>
      <p className="muted">Read-only Context、pinned Worker pool、Verifier 與 Root Workflow revision 由 server 在 validate/publish 時做 tenant、published 與角色相容性檢查。</p>
      <PolicyEditor value={draft.policy} disabled={disabled} onChange={(policy) => update({ policy })} />
      <div className="agent-runtime-grid">
        <div className="field">
          <label htmlFor="orchestrator-workflow-id">Pinned Workflow id</label>
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
      <JsonField id="orchestrator-context" label="Context (JSON)" disabled={disabled} text={texts.context ?? ''} error={jsonErrors.context} onChange={(text) => editJson('context', text)} />
      <JsonField
        id="orchestrator-worker-pool"
        label="Worker pool (JSON)"
        disabled={disabled}
        text={texts.workerPool ?? ''}
        error={jsonErrors.workerPool}
        onChange={(text) => editJson('workerPool', text)}
      />
      <WorkerPolicyEditor value={draft.workerPolicy} disabled={disabled} onChange={(workerPolicy) => update({ workerPolicy })} />
      <JsonField id="orchestrator-verifier" label="Verifier (JSON)" disabled={disabled} text={texts.verifier ?? ''} error={jsonErrors.verifier} onChange={(text) => editJson('verifier', text)} />
      <JsonField id="orchestrator-budgets" label="Budgets (JSON)" disabled={disabled} text={texts.budgets ?? ''} error={jsonErrors.budgets} onChange={(text) => editJson('budgets', text)} />
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
  if (editing) return <Editor id={editing} multiAgentDispatchEnabled={multiAgentDispatchEnabled} onClose={() => { setEditing(null); void resource.reload() }} />
  const editWorkerPool = (text: string) => {
    setWorkerPoolText(text)
    let parsed: unknown
    try { parsed = JSON.parse(text) } catch { setWorkerPoolError(JSON_FIELD_ERROR); return }
    setWorkerPoolError(undefined)
    setDraft({ ...draft, workerPool: parsed as OrchestratorDraft['workerPool'] })
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
          <div className="field">
            <label>Verifier Agent id
              <input
                className="input"
                value={draft.verifier.agentId}
                onChange={(e) => setDraft({ ...draft, verifier: { ...draft.verifier, agentId: e.target.value } })}
              />
            </label>
          </div>
          <div className="field">
            <label>Verifier revision
              <input
                className="input"
                type="number"
                min={1}
                value={draft.verifier.revision}
                onChange={(e) => setDraft({ ...draft, verifier: { ...draft.verifier, revision: Number(e.target.value) } })}
              />
            </label>
          </div>
        </div>
        <JsonField id="orchestrator-create-worker-pool" label="Worker pool (JSON)" text={workerPoolText} error={workerPoolError} onChange={editWorkerPool} />
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
