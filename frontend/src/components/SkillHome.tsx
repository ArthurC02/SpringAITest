import { useCallback, useEffect, useRef, useState } from 'react'
import { deleteSkill, exportSkill, getSkill, importSkill, listSkillCatalog, listSkills } from '../api/skills'
import type {
  Skill,
  SkillCatalogEntry,
  SkillInfo,
  SkillInputField,
  SkillKind,
  SkillSimpleForm,
} from '../types'
import type { SkillForm } from '../skills/templates'
import { isAgenticDefinition } from '../skills/agenticPackage'
import {
  isActiveSkillRequest,
  isCurrentSkillRequest,
  isSynchronizedSkillSnapshot,
} from '../skills/revision'
import { useResource } from '../hooks/useResource'
import AdvancedSkillEditor, { type AdvancedMode } from './AdvancedSkillEditor'
import AgentSkillEditor from './AgentSkillEditor'
import SimpleSkillEditor from './SimpleSkillEditor'
import SkillHistory from './SkillHistory'
import SkillRunPanel from './SkillRunPanel'
import ErrorText from './ErrorText'
import Skeleton from './Skeleton'
import { useConfirm } from './ConfirmDialog'
import { runWithToast, useToast } from './Toast'

const SOURCE_LABEL: Record<'custom' | 'builtin', string> = { custom: '自訂', builtin: '內建' }

/** 清單一列（合併 custom 管理資料與 catalog 的 input_schema）。 */
interface Row {
  name: string
  description: string
  required_role: string
  source: 'custom' | 'builtin'
  revision: number | null
  enabled: boolean
  schema: Record<string, SkillInputField> | null
  kind: SkillKind
  /** 簡單模式建立品才有；有值才顯示「簡單編輯」入口。 */
  simpleForm: SkillSimpleForm | null
}

type Sub = 'edit' | 'run' | 'history'
type Selected = Pick<Row, 'name' | 'source' | 'schema' | 'kind' | 'revision'> | null

/**
 * Skill 功能樹：可編輯 Skill 清單（custom 全部 + 全部內建唯讀）＋ 三子功能（編輯/試跑/版本）。
 * template-* 骨架一律過濾（縫④，SSR-P1-004）——它們只在 compose 依 basedOn 精確取用。
 */
export default function SkillHome({ isAdmin }: { isAdmin: boolean }) {
  const toast = useToast()
  const confirm = useConfirm()
  const [selected, setSelected] = useState<Selected>(null)
  const [sub, setSub] = useState<Sub>('edit')
  const [creating, setCreating] = useState(false)
  const [simpleEdit, setSimpleEdit] = useState<
    { name: string; templateId: string; form: SkillForm } | null
  >(null)
  const [advanced, setAdvanced] = useState<{ mode: AdvancedMode; def: string; saved?: Skill | null } | null>(
    null,
  )
  const [agentEdit, setAgentEdit] = useState<string | null>(null)
  const [restorePending, setRestorePending] = useState(false)
  const uploadRef = useRef<HTMLInputElement>(null)
  const mountedRef = useRef(false)
  const skillRequestGenerationRef = useRef(0)
  const restorePendingRef = useRef(false)

  useEffect(() => {
    mountedRef.current = true
    return () => {
      mountedRef.current = false
      skillRequestGenerationRef.current += 1
      restorePendingRef.current = false
    }
  }, [])

  const fetchRows = useCallback(async (): Promise<Row[]> => {
    const [customs, catalog] = await Promise.all([listSkills(), listSkillCatalog()])
    const schemaOf = (name: string): Record<string, SkillInputField> | null =>
      catalog.find((c) => c.name === name)?.input_schema ?? null
    const kindOf = (name: string, fallback?: SkillKind): SkillKind =>
      catalog.find((c) => c.name === name)?.kind ?? fallback ?? 'flow'
    const customRows: Row[] = customs.map((s: SkillInfo) => ({
      name: s.name,
      description: s.description,
      required_role: s.required_role,
      source: 'custom',
      revision: s.current_revision,
      enabled: s.enabled,
      schema: schemaOf(s.name),
      kind: kindOf(s.name, s.kind),
      simpleForm: s.simpleForm ?? null,
    }))
    // 內建：全部列出（唯讀）；template-* 骨架一律排除（縫④）——只在 compose 依 basedOn 精確取用。
    const builtinRows: Row[] = catalog
      .filter((c: SkillCatalogEntry) => c.source === 'builtin' && !c.name.startsWith('template-'))
      .map((c) => ({
        name: c.name,
        description: c.description,
        required_role: c.required_role,
        source: 'builtin',
        revision: c.revision,
        enabled: true,
        schema: c.input_schema ?? null,
        kind: c.kind ?? 'flow',
        simpleForm: null, // 內建骨架無簡單模式表單狀態
      }))
    return [...customRows, ...builtinRows]
  }, [])

  const { data, loading, error, reload } = useResource(fetchRows)
  const rows = data ?? []

  function open(row: Row, s: Sub) {
    if (restorePendingRef.current) return
    skillRequestGenerationRef.current += 1
    setCreating(false)
    setSimpleEdit(null)
    setAdvanced(null)
    setAgentEdit(null)
    setSelected({
      name: row.name,
      source: row.source,
      schema: row.schema,
      kind: row.kind,
      revision: row.revision,
    })
    setSub(s)
  }

  function backToList() {
    if (restorePendingRef.current) return
    skillRequestGenerationRef.current += 1
    setSelected(null)
    setCreating(false)
    setSimpleEdit(null)
    setAdvanced(null)
    setAgentEdit(null)
  }

  // 有 simpleForm 的 custom skill 才進得來：讀回存下的範本身分＋表單值，掛簡單編輯器。
  function openSimpleEdit(row: Row) {
    if (restorePendingRef.current || !row.simpleForm) return
    skillRequestGenerationRef.current += 1
    setSelected(null)
    setCreating(false)
    setAdvanced(null)
    setAgentEdit(null)
    setSimpleEdit({ name: row.name, templateId: row.simpleForm.templateId, form: row.simpleForm.form })
  }

  function navigateSub(next: Sub) {
    if (restorePendingRef.current) return
    skillRequestGenerationRef.current += 1
    setAdvanced(null)
    setAgentEdit(null)
    setSub(next)
  }

  // 下載 package（flow：自足 SKILL.md；agentic：原始 zip bytes）。走 apiFetchBlob（Bearer/401 一致）。
  async function onDownload(name: string) {
    await runWithToast(toast, () => exportSkill(name), { success: '已下載' })
  }

  // 上傳 Agent Skill 套件（ADMIN）：一律送原始 zip bytes；name 由 Workflow 唯一 YAML parser 推導。
  // client 不解析 name、不猜 route；接受與否由 server import validation 判定。
  async function onUpload(file: File | undefined) {
    if (uploadRef.current) uploadRef.current.value = ''
    if (!file) return
    await runWithToast(toast, () => importSkill(file, file.name), {
      onSuccess: (stored) => {
        toast(`已匯入 ${stored.name}`, 'success')
        return reload()
      },
    })
  }

  async function onDisable(name: string) {
    if (
      !(await confirm(
        `停用 Skill「${name}」？停用後不再出現在執行清單，歷史 revision 仍保留。`,
        { danger: true, confirmLabel: '停用' },
      ))
    )
      return
    await runWithToast(toast, () => deleteSkill(name), {
      success: '已停用',
      onSuccess: () => {
        if (selected?.name === name) backToList()
        return reload()
      },
    })
  }

  // 內建 kb-query「檢視」需要骨架原文：從 catalog 取（縫②）。
  async function openBuiltinView(name: string, schema: Row['schema'], revision: number | null) {
    if (restorePendingRef.current) return
    const generation = ++skillRequestGenerationRef.current
    setSelected({ name, source: 'builtin', schema, kind: 'flow', revision })
    setSub('edit')
    await runWithToast(toast, () => listSkillCatalog(), {
      onSuccess: (catalog) => {
        if (generation !== skillRequestGenerationRef.current || restorePendingRef.current) return
        const entry = catalog.find((c) => c.name === name)
        if (entry?.revision !== revision) return
        setAdvanced({ mode: { kind: 'view', name }, def: entry.definition ?? '' })
      },
    })
  }

  // ponytail: custom「編輯」走進階保真（載原始 YAML → updateSkill 產新版），
  // 不走簡單模式——簡單模式無法反解 rule/flow/params，會用重選骨架靜默重建（設計 §10 偏差②）。
  // kind-aware：agentic skill（definition 的 metadata.kind: agentic）改開 package 編輯器（save 走 import，不走 flow CRUD）；
  // 其餘（含 kind 缺席 → fall back flow）走既有 YAML 進階編輯器。
  async function openCustomEdit(
    name: string,
    schema: Row['schema'],
    kind: SkillKind,
    revision: number | null,
  ) {
    if (restorePendingRef.current) return
    const generation = ++skillRequestGenerationRef.current
    setSelected({ name, source: 'custom', schema, kind, revision })
    setSub('edit')
    await runWithToast(toast, () => getSkill(name), {
      onSuccess: (s) => {
        if (
          generation !== skillRequestGenerationRef.current
          || restorePendingRef.current
          || s.current_revision !== revision
        ) return
        if (s.kind === 'agentic' || isAgenticDefinition(s.definition)) {
          setSelected((current) => (current ? { ...current, kind: 'agentic' } : current))
          setAdvanced(null)
          setAgentEdit(name)
        } else {
          setAdvanced({ mode: { kind: 'edit', name }, def: s.definition ?? '', saved: s })
        }
      },
    })
  }

  function onRestorePendingChange(pending: boolean) {
    if (!mountedRef.current) return
    restorePendingRef.current = pending
    if (!mountedRef.current) return
    setRestorePending(pending)
    if (pending) {
      // 任何 restore 前啟動的 GET/editor 都不可再覆寫畫面；overlay 也不得保留舊內容。
      skillRequestGenerationRef.current += 1
      setAdvanced(null)
      setAgentEdit(null)
    }
  }

  async function loadRestoredSnapshot(
    name: string,
    expectedRevision: number,
    generation: number,
  ) {
    const isActive = () => isActiveSkillRequest(
      mountedRef.current,
      generation,
      skillRequestGenerationRef.current,
    )
    // 正常情況第一輪即一致；短暫 eventual visibility 時最多重讀三次，不接受半新半舊資料。
    for (let attempt = 0; attempt < 3; attempt += 1) {
      if (!isActive()) return null
      const [detail, catalog] = await Promise.all([getSkill(name), listSkillCatalog()])
      if (!isActive()) return null
      const entry = catalog.find((item) => item.name === name)
      if (entry && isSynchronizedSkillSnapshot(expectedRevision, detail, entry)) {
        return { detail, entry }
      }
    }
    if (!isActive()) return null
    throw new Error('Skill 已回溯，但最新版本資料尚未同步，請返回清單後重新開啟。')
  }

  /** Restore response 只提供 expected revision；UI 僅以同 revision 的 detail+catalog 原子快照更新。 */
  async function onHistoryReverted(restored: Skill) {
    const generation = ++skillRequestGenerationRef.current
    const expectedRevision = restored.current_revision
    const isActive = () => isActiveSkillRequest(
      mountedRef.current,
      generation,
      skillRequestGenerationRef.current,
    )
    try {
      const snapshot = await loadRestoredSnapshot(restored.name, expectedRevision, generation)
      if (!isActive() || !snapshot) return false
      const { detail, entry } = snapshot
      if (!isCurrentSkillRequest(
        generation,
        skillRequestGenerationRef.current,
        expectedRevision,
        detail.current_revision,
      )) return false

      if (!isActive()) return false
      setSelected((current) =>
        current?.name === restored.name
          ? {
              ...current,
              kind: detail.kind ?? 'flow',
              schema: entry.input_schema ?? null,
              revision: detail.current_revision,
            }
          : current,
      )
      if (!isActive()) return false
      setAdvanced(null)
      if (!isActive()) return false
      setAgentEdit(null)
      if (!isActive()) return false
      await reload()
      if (!isActive()) return false
      return true
    } catch (error) {
      if (!isActive()) return false
      // Restore 已成功但讀不到一致快照時，退回清單；不能解鎖仍帶舊 kind/schema 的 selected。
      skillRequestGenerationRef.current += 1
      restorePendingRef.current = false
      setRestorePending(false)
      if (!mountedRef.current) return false
      toast((error as Error).message, 'error')
      if (!mountedRef.current) return false
      setSelected(null)
      if (!mountedRef.current) return false
      setAdvanced(null)
      if (!mountedRef.current) return false
      setAgentEdit(null)
      if (!mountedRef.current) return false
      setSub('edit')
      if (!mountedRef.current) return false
      await reload()
      return false
    }
  }

  // ---- Agent Skill（agentic）package 編輯器覆蓋層（save 走 import） ----
  if (agentEdit) {
    return (
      <AgentSkillEditor
        name={agentEdit}
        onSaved={() => {
          reload()
          setAgentEdit(null)
          if (selected) setSub('edit')
        }}
        onClose={() => setAgentEdit(null)}
      />
    )
  }

  // ---- 進階編輯器覆蓋層（簡單→進階交棒、內建檢視） ----
  if (advanced) {
    return (
      <AdvancedSkillEditor
        mode={advanced.mode}
        initialDefinition={advanced.def}
        saved={advanced.saved}
        onSaved={() => {
          reload()
          setAdvanced(null)
          if (selected) setSub('edit')
        }}
        onClose={() => setAdvanced(null)}
      />
    )
  }

  // ---- 簡單模式編輯既有 skill（重跑 compose 更新 definition + simple_form；進階可交棒） ----
  if (simpleEdit) {
    return (
      <SimpleSkillEditor
        initial={simpleEdit}
        onSaved={() => reload()}
        onAdvanced={(def) => {
          setAdvanced({ mode: { kind: 'edit', name: simpleEdit.name }, def })
          setSimpleEdit(null)
        }}
        onClose={() => setSimpleEdit(null)}
      />
    )
  }

  // ---- 新增 Skill（簡單模式，進階可交棒） ----
  if (creating) {
    return (
      <SimpleSkillEditor
        onSaved={() => reload()}
        onAdvanced={(def) => setAdvanced({ mode: { kind: 'create' }, def })}
        onClose={backToList}
      />
    )
  }

  // ---- 已選 skill：三子功能 ----
  if (selected) {
    const isBuiltin = selected.source === 'builtin'
    const editLabel = isBuiltin ? '檢視' : '編輯'
    return (
      <div className="skill-tree">
        <div className="skill-tree__head">
          <button className="btn" type="button" disabled={restorePending} onClick={backToList}>
            ← 返回清單
          </button>
          <h3 className="skill-tree__title">
            {selected.name} <span className={`badge badge--src-${selected.source}`}>{SOURCE_LABEL[selected.source]}</span>
          </h3>
          <div className="seg" role="group" aria-label="Skill 子功能">
            <button
              className="btn"
              disabled={restorePending}
              aria-pressed={sub === 'edit'}
              onClick={() => navigateSub('edit')}
            >
              {editLabel}
            </button>
            <button
              className="btn"
              disabled={restorePending}
              aria-pressed={sub === 'run'}
              onClick={() => navigateSub('run')}
            >
              試跑
            </button>
            <button
              className="btn"
              disabled={restorePending}
              aria-pressed={sub === 'history'}
              onClick={() => navigateSub('history')}
            >
              版本控管
            </button>
          </div>
        </div>

        {sub === 'edit' && (
          <div>
            <p className="muted">
              {isBuiltin
                ? '內建 Skill 為唯讀。可檢視其骨架設定，或改用「試跑／版本控管」。'
                : '編輯此 Skill 的完整定義（進階編輯器，保留原始 YAML 逐字修改）。'}
            </p>
            <button
              className="btn"
              type="button"
              disabled={restorePending}
              onClick={() =>
                isBuiltin
                  ? openBuiltinView(selected.name, selected.schema, selected.revision)
                  : openCustomEdit(selected.name, selected.schema, selected.kind, selected.revision)
              }
            >
              {isBuiltin ? '檢視骨架' : '開啟編輯器'}
            </button>
          </div>
        )}
        {sub === 'run' && <SkillRunPanel name={selected.name} inputSchema={selected.schema} />}
        {sub === 'history' && (
          <SkillHistory
            name={selected.name}
            canRevert={!isBuiltin && !restorePending}
            onReverted={onHistoryReverted}
            onRestorePendingChange={onRestorePendingChange}
          />
        )}
      </div>
    )
  }

  // ---- 清單 ----
  return (
    <div className="skills">
      {isAdmin && (
        <div className="skills__bar">
          <button className="btn btn--info" onClick={() => setCreating(true)}>
            ＋ 新增 Skill
          </button>
          <button className="btn" type="button" onClick={() => uploadRef.current?.click()}>
            ⬆ 上傳 Agent Skill 套件
          </button>
          {/* ponytail: 原生 file input，隱藏以按鈕觸發。一律送原始 zip bytes，接受與否由 server import validation 判定。 */}
          <input
            ref={uploadRef}
            type="file"
            accept=".zip,application/zip"
            className="visually-hidden"
            aria-label="上傳 Agent Skill 套件"
            onChange={(e) => onUpload(e.target.files?.[0])}
          />
        </div>
      )}

      <ErrorText msg={error} />

      {loading && rows.length === 0 ? (
        <Skeleton rows={3} />
      ) : rows.length === 0 && !error ? (
        <p className="muted">尚無 Skill。</p>
      ) : (
        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr>
                <th>名稱</th>
                <th>描述</th>
                <th>來源</th>
                <th>角色</th>
                <th>rev</th>
                <th>操作</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((r) => (
                <tr key={`${r.source}:${r.name}`}>
                  <td>{r.name}</td>
                  <td>{r.description}</td>
                  <td>
                    <span className={`badge badge--src-${r.source}`}>{SOURCE_LABEL[r.source]}</span>
                  </td>
                  <td>
                    <span className={`badge badge--${r.required_role === 'ADMIN' ? 'admin' : 'user'}`}>
                      {r.required_role}
                    </span>
                  </td>
                  <td>{r.revision != null ? `r${r.revision}` : '—'}</td>
                  <td className="skills__ops">
                    {r.source === 'builtin' ? (
                      <button className="btn" onClick={() => openBuiltinView(r.name, r.schema, r.revision)}>
                        檢視
                      </button>
                    ) : (
                      isAdmin && (
                        <button
                          className="btn"
                          onClick={() => openCustomEdit(r.name, r.schema, r.kind, r.revision)}
                        >
                          編輯
                        </button>
                      )
                    )}
                    {/* 簡單模式建立品（有 simpleForm）才給零術語的簡單編輯入口；純 YAML／package 匯入品維持進階編輯。 */}
                    {r.source === 'custom' && isAdmin && r.simpleForm && (
                      <button className="btn" onClick={() => openSimpleEdit(r)}>
                        簡單編輯
                      </button>
                    )}
                    <button className="btn" onClick={() => open(r, 'run')}>
                      試跑
                    </button>
                    <button className="btn" onClick={() => open(r, 'history')}>
                      版本
                    </button>
                    {r.source === 'custom' && isAdmin && (
                      <button className="btn" onClick={() => onDownload(r.name)}>
                        下載
                      </button>
                    )}
                    {r.source === 'custom' && isAdmin && (
                      <button className="btn btn--danger" onClick={() => onDisable(r.name)}>
                        停用
                      </button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}
