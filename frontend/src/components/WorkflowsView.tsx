import { useCallback, useEffect, useState } from 'react'
import {
  createWorkflow, getWorkflow, listWorkflowNodeCatalog, listWorkflowRevisions, listWorkflows,
  publishWorkflow, restoreWorkflowRevision, simulateWorkflow, putWorkflowDraft, validateWorkflow,
} from '../api/workflows'
import type { Workflow, WorkflowDraft, WorkflowKind, WorkflowRuntimeVariant, WorkflowSimulation, WorkflowValidation } from '../types'
import { semanticFingerprint } from '../workflowDesigner/graphAdapter'
import { semanticDiff } from '../workflowDesigner/diff'
import { createBlankDraft } from '../workflowDesigner/draft'
import WorkflowDesigner from '../workflowDesigner/WorkflowDesigner'
import ErrorText from './ErrorText'
import Skeleton from './Skeleton'
import { useResource } from '../hooks/useResource'
import { requireLoaded, runWithToast, useToast } from './Toast'
import RevisionList from './RevisionList'

function WorkflowEditor({ id, onClose }: { id: string; onClose: () => void }) {
  const toast = useToast()
  const catalog = useResource(listWorkflowNodeCatalog)
  const [workflow, setWorkflow] = useState<Workflow | null>(null)
  const [etag, setEtag] = useState<string | null>(null)
  const [draft, setDraft] = useState<WorkflowDraft | null>(null)
  const [savedSemantic, setSavedSemantic] = useState('')
  const [validation, setValidation] = useState<WorkflowValidation | null>(null)
  const [simulation, setSimulation] = useState<WorkflowSimulation | null>(null)
  const [blocked, setBlocked] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const revisions = useResource(useCallback(() => listWorkflowRevisions(id), [id]))

  const load = useCallback(async () => {
    setError(null)
    try {
      const result = await getWorkflow(id)
      setWorkflow(result.data); setEtag(result.etag); setDraft(result.data.draft)
      setSavedSemantic(semanticFingerprint(result.data.draft.definition)); setValidation(null); setSimulation(null); setBlocked(false)
    } catch (e) { setError((e as Error).message) }
  }, [id])
  useEffect(() => { void load() }, [load])
  const dirty = !!draft && semanticFingerprint(draft.definition) !== savedSemantic
  const writable = !!etag && !blocked && !!draft
  const onConflict = () => setBlocked(true)
  // 衝突不在這裡吞（吞掉 = runWithToast 看不到失敗 → 假成功 toast），一律往外拋，
  // 由 runWithToast 的 onConflict 統一鎖定編輯器。
  // 守衛不成立 = UI 狀態與寫入前提脫節（disabled 失守），一律拋錯而非靜默返回。
  async function save() {
    await putWorkflowDraft(id, requireLoaded(workflow, 'Workflow'), requireLoaded(draft, '草稿'), requireLoaded(etag, '草稿版本')); await load()
  }
  async function validate() { setValidation(await validateWorkflow(id, requireLoaded(etag, '草稿版本'))) }
  async function simulate() { setSimulation(await simulateWorkflow(id, requireLoaded(etag, '草稿版本'))) }
  async function publish() { await publishWorkflow(id, requireLoaded(workflow, 'Workflow').draft_version, requireLoaded(etag, '草稿版本')); await load() }
  if (!workflow || !draft) return <><button className="btn" onClick={onClose}>返回清單</button><ErrorText msg={error} /><Skeleton rows={4} /></>
  return <>
    <div className="view__head"><h2 className="view__title">{workflow.name}</h2><button className="btn" onClick={onClose}>返回清單</button></div>
    {blocked && <div className="agent-errors" role="alert">草稿已由其他人更新；為避免覆寫，所有操作已鎖定。<button className="btn" onClick={() => void load()}>重新載入</button></div>}
    <ErrorText msg={error} /><ErrorText msg={catalog.error} />
    <WorkflowDesigner definition={draft.definition} uiMetadata={draft.ui_metadata} catalog={catalog.data ?? []}
      validation={validation} simulation={simulation} disabled={!writable} onChange={(definition, ui_metadata) => { setDraft({ definition, ui_metadata }); setValidation(null); setSimulation(null) }} />
    <div className="agent-actions">
      <button className="btn btn--primary" disabled={!writable} onClick={() => void runWithToast(toast, save, { success: '草稿已儲存', onConflict })}>儲存草稿</button>
      <button className="btn" disabled={!writable || dirty} title={dirty ? '請先儲存草稿' : undefined} onClick={() => void runWithToast(toast, validate, { success: '驗證完成', onConflict })}>驗證</button>
      <button className="btn" disabled={!writable || dirty} onClick={() => void runWithToast(toast, simulate, { success: '模擬完成', onConflict })}>模擬</button>
      <button className="btn btn--info" disabled={!writable || dirty || !validation?.valid} onClick={() => void runWithToast(toast, publish, { success: '已發布', onConflict })}>發布</button>
    </div>
    {validation && <section className="agent-block"><h4>Validation</h4>{validation.valid ? <p className="notice-text">驗證通過。</p> : <ul className="agent-errors">{validation.errors.map((issue, index) => <li key={`${issue.id ?? 'graph'}-${index}`}>{issue.id ? `[${issue.id}] ` : ''}{issue.message}</li>)}</ul>}</section>}
    {simulation?.trace && <section className="agent-block"><h4>Simulation trace（敏感資料已由 server 遮罩）</h4><ul>{simulation.trace.map((entry) => <li key={entry.node_id}>{entry.node_id}: {entry.status}{entry.summary ? ` — ${entry.summary}` : ''}</li>)}</ul></section>}
    <section className="agent-block"><h4>Revisions / Semantic Diff</h4><RevisionList loading={revisions.loading} revisions={revisions.data ?? []} restoreLabel="還原為新 revision" successMessage="已從歷史 revision 建立新 revision" onRestore={(revision) => restoreWorkflowRevision(id, revision)} onRestored={() => { void load(); void revisions.reload() }} renderExtra={(revision) => { const diff = revision.definition ? semanticDiff(draft.definition, revision.definition) : null; return diff ? ` · +${diff.added.length} −${diff.removed.length} ~${diff.changed.length}` : '' }} /></section>
  </>
}

export default function WorkflowsView() {
  const toast = useToast(); const resource = useResource(listWorkflows)
  const [editing, setEditing] = useState<string | null>(null); const [kind, setKind] = useState<WorkflowKind>('orchestrator')
  const [runtimeVariant, setRuntimeVariant] = useState<WorkflowRuntimeVariant>('worker')
  const [name, setName] = useState('')
  const blankDraft = (selectedKind: WorkflowKind) => createBlankDraft(selectedKind, runtimeVariant)
  const rows = resource.data ?? []
  if (editing) return <WorkflowEditor id={editing} onClose={() => { setEditing(null); void resource.reload() }} />
  return <><ErrorText msg={resource.error} />
    {kind === 'agent-runtime' && <label className="field">Runtime variant
      <select className="input" aria-label="Runtime variant" value={runtimeVariant} onChange={(event) => setRuntimeVariant(event.target.value as WorkflowRuntimeVariant)}>
        <option value="worker">Worker Harness</option>
        <option value="verifier">Read-only Verifier Harness</option>
      </select>
    </label>}
    <section className="agent-block"><h4>建立 Execution Harness</h4><div className="agent-runtime-grid"><input className="input" placeholder="名稱" value={name} onChange={(e) => setName(e.target.value)} /><select className="input" value={kind} onChange={(e) => setKind(e.target.value as WorkflowKind)}><option value="orchestrator">Orchestrator</option><option value="agent-runtime">Agent Runtime</option></select><button className="btn btn--primary" disabled={!name.trim()} onClick={() => void runWithToast(toast, async () => { const created = await createWorkflow({ name, kind, draft: blankDraft(kind) }); setEditing(created.id) }, { success: '已建立草稿' })}>建立</button></div><p className="muted">只可編輯 Harness graph；Prompt、Rule、Skill instruction 與 Agent binding 一律不在 Graph IR。</p></section>
    {resource.loading && rows.length === 0 ? <Skeleton rows={3} /> : <div className="table-wrap"><table className="table"><thead><tr><th>名稱</th><th>種類</th><th>發布</th><th>操作</th></tr></thead><tbody>{rows.map((row) => <tr key={row.id}><td>{row.name}</td><td>{row.kind}</td><td>{row.published_revision == null ? '草稿' : `r${row.published_revision}`}</td><td><button className="btn" onClick={() => setEditing(row.id)}>編輯</button></td></tr>)}</tbody></table></div>}</>
}
