import { useCallback, useRef, useState, type ReactNode } from 'react'
import { listSkillCatalog } from '../api/skills'
import type { Skill, SkillInfo, SkillKind } from '../types'
import type { SkillForm } from '../skills/templates'
import { useSkillRows, type SkillRow } from '../hooks/useSkillRows'
import { useSkillSelection, type SelectedSkill } from '../hooks/useSkillSelection'
import SkillHistory from './SkillHistory'
import SkillRunPanel from './SkillRunPanel'
import ErrorText from './ErrorText'
import Skeleton from './Skeleton'
import { useConfirm } from './ConfirmDialog'
import { runWithToast, useToast } from './Toast'

const SOURCE_LABEL = { custom: '自訂', builtin: '內建' } as const

interface EditorControls {
  onSaved: () => void
  onClose: () => void
}

interface Props {
  isAdmin: boolean
  kind: SkillKind
  noun: string
  emptyLabel: string
  listCustom: () => Promise<SkillInfo[]>
  getDetail: (name: string) => Promise<Skill>
  download: (name: string) => Promise<void>
  disable: (name: string) => Promise<void>
  renderCustomEditor: (skill: Skill, controls: EditorControls) => ReactNode
  renderBuiltinEditor?: (name: string, definition: string, controls: EditorControls) => ReactNode
  renderCreateEditor?: (controls: EditorControls & { onAdvanced: (definition: string) => void }) => ReactNode
  renderSimpleEditor?: (
    initial: { name: string; templateId: string; form: SkillForm },
    controls: EditorControls & { onAdvanced: (definition: string) => void },
  ) => ReactNode
  renderAdvancedEditor?: (definition: string, controls: EditorControls) => ReactNode
  upload?: (file: File) => Promise<void>
}

/** 無 kind 決策的共享 presentation；artifact API 與 editor 均由兩個 Home 組裝。 */
export default function SkillHome(props: Props) {
  const toast = useToast()
  const confirm = useConfirm()
  const uploadRef = useRef<HTMLInputElement>(null)
  const [customEditor, setCustomEditor] = useState<Skill | null>(null)
  const [builtinEditor, setBuiltinEditor] = useState<{ name: string; definition: string } | null>(null)
  const [creating, setCreating] = useState(false)
  const [simpleEdit, setSimpleEdit] = useState<{ name: string; templateId: string; form: SkillForm } | null>(null)
  const [advancedDefinition, setAdvancedDefinition] = useState<string | null>(null)

  const invalidateEditors = useCallback(() => {
    setCustomEditor(null)
    setBuiltinEditor(null)
    setCreating(false)
    setSimpleEdit(null)
    setAdvancedDefinition(null)
  }, [])
  const { rows, loading, error, reload } = useSkillRows(props.kind, props.listCustom)
  const selection = useSkillSelection({
    kind: props.kind,
    reload,
    invalidateEditors,
    notify: toast,
  })

  const closeEditor = useCallback(() => invalidateEditors(), [invalidateEditors])
  const savedEditor = useCallback(() => {
    // Advanced/package saves may change the canonical name or revision. Return to
    // the freshly loaded list instead of retaining a stale selection snapshot.
    selection.reset()
    invalidateEditors()
    void reload()
  }, [invalidateEditors, reload, selection])
  const savedSimpleEditor = useCallback(() => {
    // Simple editor owns post-save warnings and the embedded run panel; keep it mounted.
    void reload()
  }, [reload])

  async function openCustomEdit(row: SkillRow) {
    const selected: SelectedSkill = {
      name: row.name, source: row.source, schema: row.schema, kind: row.kind, revision: row.revision,
    }
    const generation = selection.beginEdit(selected)
    if (generation == null) return
    await runWithToast(toast, () => props.getDetail(row.name), {
      onSuccess: (skill) => {
        if (!selection.isRequestActive(generation) || skill.current_revision !== row.revision) return
        setCustomEditor(skill)
      },
    })
  }

  async function openBuiltinView(row: SkillRow) {
    const selected: SelectedSkill = {
      name: row.name, source: row.source, schema: row.schema, kind: row.kind, revision: row.revision,
    }
    const generation = selection.beginEdit(selected)
    if (generation == null) return
    await runWithToast(toast, listSkillCatalog, {
      onSuccess: (catalog) => {
        if (!selection.isRequestActive(generation)) return
        const entry = catalog.find((item) => item.name === row.name)
        if (entry?.revision !== row.revision) return
        setBuiltinEditor({ name: row.name, definition: entry.definition ?? '' })
      },
    })
  }

  async function onDisable(name: string) {
    if (!(await confirm(`停用${props.noun}「${name}」？停用後不再出現在執行清單，歷史 revision 仍保留。`, {
      danger: true,
      confirmLabel: '停用',
    }))) return
    await runWithToast(toast, () => props.disable(name), {
      success: '已停用',
      onSuccess: () => {
        if (selection.selected?.name === name) selection.reset()
        return reload()
      },
    })
  }

  async function onUpload(file: File | undefined) {
    if (uploadRef.current) uploadRef.current.value = ''
    if (!file || !props.upload) return
    await runWithToast(toast, () => props.upload!(file), { onSuccess: reload })
  }

  const controls = { onSaved: savedEditor, onClose: closeEditor }
  if (customEditor) return <>{props.renderCustomEditor(customEditor, controls)}</>
  if (builtinEditor && props.renderBuiltinEditor) {
    return <>{props.renderBuiltinEditor(builtinEditor.name, builtinEditor.definition, controls)}</>
  }
  if (advancedDefinition && props.renderAdvancedEditor) {
    return <>{props.renderAdvancedEditor(advancedDefinition, controls)}</>
  }
  if (simpleEdit && props.renderSimpleEditor) {
    return <>{props.renderSimpleEditor(simpleEdit, {
      onSaved: savedSimpleEditor,
      onClose: closeEditor,
      onAdvanced: (definition) => {
        setAdvancedDefinition(definition)
        setSimpleEdit(null)
      },
    })}</>
  }
  if (creating && props.renderCreateEditor) {
    return <>{props.renderCreateEditor({
      onSaved: savedSimpleEditor,
      onClose: closeEditor,
      onAdvanced: (definition) => setAdvancedDefinition(definition),
    })}</>
  }

  if (selection.selected) {
    const row: SkillRow = {
      ...selection.selected,
      description: '', required_role: '', enabled: true, simpleForm: null,
    }
    const builtin = selection.selected.source === 'builtin'
    return (
      <div className="skill-tree">
        <div className="skill-tree__head">
          <button className="btn" type="button" disabled={selection.restorePending} onClick={selection.reset}>← 返回清單</button>
          <h3 className="skill-tree__title">
            {selection.selected.name}{' '}
            <span className={`badge badge--src-${selection.selected.source}`}>{SOURCE_LABEL[selection.selected.source]}</span>
          </h3>
          <div className="seg" role="group" aria-label={`${props.noun}子功能`}>
            <button className="btn" disabled={selection.restorePending} aria-pressed={selection.sub === 'edit'} onClick={() => selection.navigateSub('edit')}>{builtin ? '檢視' : '編輯'}</button>
            <button className="btn" disabled={selection.restorePending} aria-pressed={selection.sub === 'run'} onClick={() => selection.navigateSub('run')}>試跑</button>
            <button className="btn" disabled={selection.restorePending} aria-pressed={selection.sub === 'history'} onClick={() => selection.navigateSub('history')}>版本控管</button>
          </div>
        </div>
        {selection.sub === 'edit' && (
          <div>
            <p className="muted">{builtin ? `內建${props.noun}為唯讀。` : `編輯此${props.noun}。`}</p>
            <button className="btn" type="button" disabled={selection.restorePending} onClick={() => builtin ? openBuiltinView(row) : openCustomEdit(row)}>{builtin ? '檢視骨架' : '開啟編輯器'}</button>
          </div>
        )}
        {selection.sub === 'run' && <SkillRunPanel name={selection.selected.name} inputSchema={selection.selected.schema} />}
        {selection.sub === 'history' && (
          <SkillHistory
            name={selection.selected.name}
            canRevert={!builtin && !selection.restorePending}
            onReverted={selection.onHistoryReverted}
            onRestorePendingChange={selection.onRestorePendingChange}
          />
        )}
      </div>
    )
  }

  return (
    <div className="skills">
      {props.isAdmin && (props.renderCreateEditor || props.upload) && (
        <div className="skills__bar">
          {props.renderCreateEditor && <button className="btn btn--info" onClick={() => setCreating(true)}>＋ 新增{props.noun}</button>}
          {props.upload && (
            <>
              <button className="btn" type="button" onClick={() => uploadRef.current?.click()}>⬆ 上傳 Agent Skill 套件</button>
              <input ref={uploadRef} type="file" accept=".zip,application/zip" className="visually-hidden" aria-label="上傳 Agent Skill 套件" onChange={(event) => onUpload(event.target.files?.[0])} />
            </>
          )}
        </div>
      )}
      <ErrorText msg={error} />
      {loading && rows.length === 0 ? <Skeleton rows={3} /> : rows.length === 0 && !error ? (
        <p className="muted">{props.emptyLabel}</p>
      ) : (
        <div className="table-wrap">
          <table className="table">
            <thead><tr><th>名稱</th><th>描述</th><th>來源</th><th>角色</th><th>rev</th><th>操作</th></tr></thead>
            <tbody>{rows.map((row) => (
              <tr key={`${row.source}:${row.name}`}>
                <td>{row.name}</td><td>{row.description}</td>
                <td><span className={`badge badge--src-${row.source}`}>{SOURCE_LABEL[row.source]}</span></td>
                <td><span className={`badge badge--${row.required_role === 'ADMIN' ? 'admin' : 'user'}`}>{row.required_role}</span></td>
                <td>{row.revision != null ? `r${row.revision}` : '—'}</td>
                <td className="skills__ops">
                  {row.source === 'builtin' ? (
                    <button className="btn" onClick={() => openBuiltinView(row)}>檢視</button>
                  ) : props.isAdmin && <button className="btn" onClick={() => openCustomEdit(row)}>編輯</button>}
                  {row.source === 'custom' && props.isAdmin && row.simpleForm && props.renderSimpleEditor && (
                    <button className="btn" onClick={() => setSimpleEdit({ name: row.name, templateId: row.simpleForm!.templateId, form: row.simpleForm!.form })}>簡單編輯</button>
                  )}
                  <button className="btn" onClick={() => selection.open(row, 'run')}>試跑</button>
                  <button className="btn" onClick={() => selection.open(row, 'history')}>版本</button>
                  {row.source === 'custom' && props.isAdmin && <button className="btn" onClick={() => runWithToast(toast, () => props.download(row.name), { success: '已下載' })}>下載</button>}
                  {row.source === 'custom' && props.isAdmin && <button className="btn btn--danger" onClick={() => onDisable(row.name)}>停用</button>}
                </td>
              </tr>
            ))}</tbody>
          </table>
        </div>
      )}
    </div>
  )
}
