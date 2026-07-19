import { useCallback, useState } from 'react'
import { deleteSkill, getSkill, listSkillCatalog, listSkills } from '../api/skills'
import type { Skill, SkillCatalogEntry, SkillInfo, SkillInputField } from '../types'
import { useResource } from '../hooks/useResource'
import AdvancedSkillEditor, { type AdvancedMode } from './AdvancedSkillEditor'
import SimpleSkillEditor from './SimpleSkillEditor'
import SkillHistory from './SkillHistory'
import SkillRunPanel from './SkillRunPanel'
import ErrorText from './ErrorText'
import Skeleton from './Skeleton'
import { useToast } from './Toast'

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
}

type Sub = 'edit' | 'run' | 'history'
type Selected = { name: string; source: 'custom' | 'builtin'; schema: Row['schema'] } | null

/**
 * Skill 功能樹：可編輯 Skill 清單（custom 全部 + 全部內建唯讀）＋ 三子功能（編輯/試跑/版本）。
 * template_* 骨架一律過濾（縫④，SSR-P1-004）——它們只在 compose 依 basedOn 精確取用。
 */
export default function SkillHome({ isAdmin }: { isAdmin: boolean }) {
  const toast = useToast()
  const [selected, setSelected] = useState<Selected>(null)
  const [sub, setSub] = useState<Sub>('edit')
  const [creating, setCreating] = useState(false)
  const [advanced, setAdvanced] = useState<{ mode: AdvancedMode; def: string; saved?: Skill | null } | null>(
    null,
  )

  const fetchRows = useCallback(async (): Promise<Row[]> => {
    const [customs, catalog] = await Promise.all([listSkills(), listSkillCatalog()])
    const schemaOf = (name: string): Record<string, SkillInputField> | null =>
      catalog.find((c) => c.name === name)?.input_schema ?? null
    const customRows: Row[] = customs.map((s: SkillInfo) => ({
      name: s.name,
      description: s.description,
      required_role: s.required_role,
      source: 'custom',
      revision: s.current_revision,
      enabled: s.enabled,
      schema: schemaOf(s.name),
    }))
    // 內建：全部列出（唯讀）；template_* 骨架一律排除（縫④）——只在 compose 依 basedOn 精確取用。
    const builtinRows: Row[] = catalog
      .filter((c: SkillCatalogEntry) => c.source === 'builtin' && !c.name.startsWith('template_'))
      .map((c) => ({
        name: c.name,
        description: c.description,
        required_role: c.required_role,
        source: 'builtin',
        revision: c.revision,
        enabled: true,
        schema: c.input_schema ?? null,
      }))
    return [...customRows, ...builtinRows]
  }, [])

  const { data, loading, error, reload } = useResource(fetchRows)
  const rows = data ?? []

  function open(row: Row, s: Sub) {
    setCreating(false)
    setAdvanced(null)
    setSelected({ name: row.name, source: row.source, schema: row.schema })
    setSub(s)
  }

  function backToList() {
    setSelected(null)
    setCreating(false)
    setAdvanced(null)
  }

  async function onDisable(name: string) {
    if (!window.confirm(`停用 Skill「${name}」？停用後不再出現在執行清單，歷史 revision 仍保留。`))
      return
    try {
      await deleteSkill(name)
      toast('已停用', 'success')
      if (selected?.name === name) backToList()
      await reload()
    } catch (e) {
      toast((e as Error).message, 'error')
    }
  }

  // 內建 kb_query「檢視」需要骨架原文：從 catalog 取（縫②）。
  async function openBuiltinView(name: string, schema: Row['schema']) {
    setSelected({ name, source: 'builtin', schema })
    setSub('edit')
    try {
      const catalog = await listSkillCatalog()
      const def = catalog.find((c) => c.name === name)?.definition ?? ''
      setAdvanced({ mode: { kind: 'view', name }, def })
    } catch (e) {
      toast((e as Error).message, 'error')
    }
  }

  // ponytail: custom「編輯」走進階保真（載原始 YAML → updateSkill 產新版），
  // 不走簡單模式——簡單模式無法反解 rule/flow/params，會用重選骨架靜默重建（設計 §10 偏差②）。
  async function openCustomEdit(name: string, schema: Row['schema']) {
    setSelected({ name, source: 'custom', schema })
    setSub('edit')
    try {
      const s = await getSkill(name)
      setAdvanced({ mode: { kind: 'edit', name }, def: s.definition ?? '', saved: s })
    } catch (e) {
      toast((e as Error).message, 'error')
    }
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
          <button className="btn" type="button" onClick={backToList}>
            ← 返回清單
          </button>
          <h3 className="skill-tree__title">
            {selected.name} <span className={`badge badge--src-${selected.source}`}>{SOURCE_LABEL[selected.source]}</span>
          </h3>
          <div className="seg" role="group" aria-label="Skill 子功能">
            <button className="btn" aria-pressed={sub === 'edit'} onClick={() => setSub('edit')}>
              {editLabel}
            </button>
            <button className="btn" aria-pressed={sub === 'run'} onClick={() => setSub('run')}>
              試跑
            </button>
            <button className="btn" aria-pressed={sub === 'history'} onClick={() => setSub('history')}>
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
              onClick={() =>
                isBuiltin
                  ? openBuiltinView(selected.name, selected.schema)
                  : openCustomEdit(selected.name, selected.schema)
              }
            >
              {isBuiltin ? '檢視骨架' : '開啟編輯器'}
            </button>
          </div>
        )}
        {sub === 'run' && <SkillRunPanel name={selected.name} inputSchema={selected.schema} />}
        {sub === 'history' && <SkillHistory name={selected.name} canRevert={!isBuiltin} onReverted={reload} />}
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
                      <button className="btn" onClick={() => openBuiltinView(r.name, r.schema)}>
                        檢視
                      </button>
                    ) : (
                      isAdmin && (
                        <button className="btn" onClick={() => openCustomEdit(r.name, r.schema)}>
                          編輯
                        </button>
                      )
                    )}
                    <button className="btn" onClick={() => open(r, 'run')}>
                      試跑
                    </button>
                    <button className="btn" onClick={() => open(r, 'history')}>
                      版本
                    </button>
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
