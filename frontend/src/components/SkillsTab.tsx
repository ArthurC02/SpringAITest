import { useCallback, useEffect, useRef, useState } from 'react'
import { ApiError } from '../api/http'
import {
  createSkill,
  deleteSkill,
  exportSkill,
  getSkill,
  listSkillRevisions,
  listSkills,
  updateSkill,
  validateSkill,
} from '../api/skills'
import type { NodeInfo, Skill, SkillInfo, SkillRevision, SkillValidation } from '../types'
import NodeCatalog from './NodeCatalog'
import Skeleton from './Skeleton'
import YamlEditor from './YamlEditor'
import { useToast } from './Toast'

const DEBOUNCE_MS = 800

/** 新建時的 YAML 骨架（規格 §3.1 的最小可存檔形狀）。 */
const TEMPLATE = `name: my_skill
description: 這個 skill 做什麼
required_role: USER
input_schema:
  query: {type: str, required: true, min_length: 1}
flow:
  - node: query_intake
`

/** 引擎錯誤碼 → 人話（規格 §3.4）。未知碼直接顯示原碼，不吞掉。 */
const CODE_LABEL: Record<string, string> = {
  unknown_node: '引用了不存在的節點或版本',
  unknown_tool: '引用了不存在的 Tool',
  unbounded_loop: 'loop 缺少 max_iterations 或超出 1~10',
  invalid_expression: '條件式含白名單外的語法（僅允許 state.<鍵>、比較、and/or/not、in）',
  forbidden_script: 'Script 含禁用語法（import/exec/eval/open/雙底線屬性/while…）',
  dataflow_error: '資料流警告：讀取了無前置步驟產出的鍵',
  invalid_flow: 'flow 為空、步驟型別未知，或 YAML 解析失敗',
}

// dataflow_error 是警告級：不阻擋存檔（規格 §3.4）。其餘皆為阻擋級。
const WARN_CODES = new Set(['dataflow_error'])

// 存檔 body 只有 definition：name/description/required_role 寫在 YAML 裡，
// 由後端（經 workflow validate）解析 —— 前端不再自己解析一次當第二份事實來源。

type Mode =
  | { kind: 'edit'; name: string | null } // name=null → 新建
  | { kind: 'history'; name: string }

/** Skill 管理（ADMIN）：清單 + 三欄編輯器（節點目錄 / YAML / 驗證結果）。 */
export default function SkillsTab() {
  const toast = useToast()
  const [list, setList] = useState<SkillInfo[]>([])
  const [loading, setLoading] = useState(true)
  const [listError, setListError] = useState<string | null>(null)

  const [mode, setMode] = useState<Mode | null>(null)
  const [definition, setDefinition] = useState('')
  const [saved, setSaved] = useState<Skill | null>(null) // 伺服器上的那一版
  const [revisions, setRevisions] = useState<SkillRevision[] | null>(null)
  const [validation, setValidation] = useState<SkillValidation | null>(null)
  const [validating, setValidating] = useState(false)
  const [formError, setFormError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const taRef = useRef<HTMLTextAreaElement>(null)
  const seqRef = useRef(0) // 丟棄過期的驗證回應（使用者已經改過內容了）

  const load = useCallback(async () => {
    setListError(null)
    try {
      setList(await listSkills())
    } catch (e) {
      setListError((e as Error).message)
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    load()
  }, [load])

  const runValidate = useCallback(async (def: string) => {
    const seq = ++seqRef.current
    setValidating(true)
    try {
      const v = await validateSkill(def)
      if (seq === seqRef.current) setValidation(v)
    } catch (e) {
      // 後端沒起來／500：不要白屏，把錯誤當成一條驗證結果顯示。
      if (seq === seqRef.current) {
        setValidation({ valid: false, errors: [{ code: '', message: (e as Error).message }] })
      }
    } finally {
      if (seq === seqRef.current) setValidating(false)
    }
  }, [])

  // AT4-14：停止輸入 800ms 後才打一次 /api/skills/validate。
  useEffect(() => {
    if (mode?.kind !== 'edit' || !definition.trim()) {
      setValidation(null)
      return
    }
    const t = window.setTimeout(() => runValidate(definition), DEBOUNCE_MS)
    return () => window.clearTimeout(t)
  }, [definition, mode, runValidate])

  function reset() {
    seqRef.current++
    setValidation(null)
    setValidating(false)
    setFormError(null)
  }

  function startCreate() {
    reset()
    setSaved(null)
    setDefinition(TEMPLATE)
    setMode({ kind: 'edit', name: null })
  }

  async function openEdit(name: string) {
    reset()
    setBusy(true)
    setRevisions(null)
    setMode({ kind: 'edit', name })
    try {
      const s = await getSkill(name)
      setSaved(s)
      setDefinition(s.definition ?? '')
    } catch (e) {
      setFormError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  /** AT4-16：唯讀歷史，無編輯／儲存入口。 */
  async function openHistory(name: string) {
    reset()
    setBusy(true)
    setSaved(null)
    setRevisions(null)
    setMode({ kind: 'history', name })
    try {
      setRevisions(await listSkillRevisions(name))
    } catch (e) {
      setFormError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  function insertNode(n: NodeInfo) {
    const snippet = `  - node: ${n.name}@${n.version}\n`
    const ta = taRef.current
    if (!ta) {
      setDefinition((d) => d + snippet)
      return
    }
    const at = ta.selectionStart
    const before = definition.slice(0, at)
    const after = definition.slice(ta.selectionEnd)
    const lead = before && !before.endsWith('\n') ? '\n' : ''
    const next = before + lead + snippet + after
    setDefinition(next)
    // 插入後把游標放在新片段之後，連續插入才會一行接一行。
    const caret = before.length + lead.length + snippet.length
    requestAnimationFrame(() => {
      ta.focus()
      ta.setSelectionRange(caret, caret)
    })
  }

  async function onSave() {
    if (mode?.kind !== 'edit') return
    setBusy(true)
    setFormError(null)
    try {
      if (mode.name) {
        // 更新：路由的 name 即身分；YAML 內 name 與路由不符 → 後端回 422。
        await updateSkill(mode.name, definition)
        await load()
        const fresh = await getSkill(mode.name)
        setSaved(fresh)
        setDefinition(fresh.definition ?? definition)
        toast(`已儲存（r${fresh.current_revision}）`, 'success')
      } else {
        // 新建：名稱由後端從 YAML 解析（重複 → 409）。前端不知道最終 name，存完回清單。
        await createSkill(definition)
        await load()
        setMode(null)
        toast('已建立', 'success')
      }
    } catch (e) {
      // 422：後端把引擎錯誤碼放 fieldErrors，逐條顯示在右欄。
      if (e instanceof ApiError && e.fieldErrors) {
        setValidation({
          valid: false,
          errors: Object.entries(e.fieldErrors).map(([code, message]) => ({ code, message })),
        })
      }
      const msg =
        e instanceof ApiError && e.status === 409
          ? `名稱已存在：${e.message}`
          : e instanceof ApiError && e.status === 422
            ? `驗證未過：${e.message}`
            : (e as Error).message
      setFormError(msg)
      toast(msg, 'error')
    } finally {
      setBusy(false)
    }
  }

  async function onExport(name: string) {
    setBusy(true)
    setFormError(null)
    try {
      await exportSkill(name)
    } catch (e) {
      const msg = (e as Error).message
      setFormError(msg)
      toast(msg, 'error')
    } finally {
      setBusy(false)
    }
  }

  async function onDisable(name: string) {
    // 沿用 repo 既有的 window.confirm 二次確認慣例（見 DocumentsView）。
    if (!window.confirm(`停用 Skill「${name}」？停用後不再出現在執行清單，歷史 revision 仍保留。`))
      return
    setBusy(true)
    try {
      await deleteSkill(name)
      await load()
      if (mode?.name === name) setMode(null)
      toast('已停用', 'success')
    } catch (e) {
      toast((e as Error).message, 'error')
    } finally {
      setBusy(false)
    }
  }

  const blocking = validation?.errors.filter((e) => !WARN_CODES.has(e.code)) ?? []
  const canSave = !busy && !validating && blocking.length === 0 && definition.trim().length > 0

  return (
    <div className="skills">
      <div className="skills__bar">
        <button className="btn btn--info" onClick={startCreate} disabled={busy}>
          ＋ 新增 Skill
        </button>
      </div>

      {listError && (
        <p className="error-text" role="alert">
          {listError}
        </p>
      )}

      {loading ? (
        <Skeleton rows={3} />
      ) : list.length === 0 && !listError ? (
        <p className="muted">尚無自訂 Skill。內建 Skill 請見「執行」分頁。</p>
      ) : (
        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr>
                <th>名稱</th>
                <th>描述</th>
                <th>角色</th>
                <th>rev</th>
                <th>狀態</th>
                <th>操作</th>
              </tr>
            </thead>
            <tbody>
              {list.map((s) => (
                <tr key={s.name}>
                  <td>{s.name}</td>
                  <td>{s.description}</td>
                  <td>
                    <span
                      className={`badge badge--${s.required_role === 'ADMIN' ? 'admin' : 'user'}`}
                    >
                      {s.required_role}
                    </span>
                  </td>
                  <td>r{s.current_revision}</td>
                  <td>
                    <span className={`chip ${s.enabled ? 'chip--ready' : 'chip--failed'}`}>
                      {s.enabled ? '啟用' : '停用'}
                    </span>
                  </td>
                  <td className="skills__ops">
                    <button className="btn" onClick={() => openEdit(s.name)} disabled={busy}>
                      編輯
                    </button>
                    <button
                      className="btn btn--danger"
                      onClick={() => onDisable(s.name)}
                      disabled={busy}
                    >
                      停用
                    </button>
                    <button className="btn" onClick={() => openHistory(s.name)} disabled={busy}>
                      歷史
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {mode?.kind === 'history' && (
        <section className="skill-history">
          <div className="skill-editor__head">
            <h3 className="skill-editor__title">歷史 {mode.name}</h3>
            <div className="skill-editor__actions">
              <button className="btn" type="button" onClick={() => setMode(null)}>
                關閉
              </button>
            </div>
          </div>
          <p className="muted">唯讀文字對照（依 revision 遞減），無編輯或儲存入口。</p>
          {formError && (
            <p className="error-text" role="alert">
              {formError}
            </p>
          )}
          {busy && !revisions ? (
            <Skeleton rows={3} />
          ) : revisions && revisions.length === 0 ? (
            <p className="muted">尚無 revision。</p>
          ) : (
            revisions?.map((r) => (
              <details className="rev" key={r.revision} open={r === revisions[0]}>
                <summary className="rev__head">
                  <span className="badge badge--user">r{r.revision}</span>
                  <span className="rev__meta">
                    {r.created_by} · {r.created_at}
                  </span>
                  <code className="rev__sha">{r.definition_sha256.slice(0, 12)}</code>
                </summary>
                <pre>{r.definition}</pre>
              </details>
            ))
          )}
        </section>
      )}

      {mode?.kind === 'edit' && (
        <section className="skill-editor">
          <div className="skill-editor__head">
            <h3 className="skill-editor__title">
              {mode.name ? `編輯 ${mode.name}` : '新增 Skill'}
              {saved && <span className="badge badge--user">r{saved.current_revision}</span>}
            </h3>
            <div className="skill-editor__actions">
              <button
                className="btn"
                type="button"
                onClick={() => runValidate(definition)}
                disabled={busy || !definition.trim()}
              >
                驗證
              </button>
              <button className="btn btn--primary" type="button" onClick={onSave} disabled={!canSave}>
                {busy ? '儲存中…' : '儲存'}
              </button>
              {mode.name && (
                <button
                  className="btn"
                  type="button"
                  onClick={() => onExport(mode.name!)}
                  disabled={busy}
                >
                  匯出
                </button>
              )}
              <button className="btn" type="button" onClick={() => setMode(null)} disabled={busy}>
                關閉
              </button>
            </div>
          </div>

          {formError && (
            <p className="error-text" role="alert">
              {formError}
            </p>
          )}

          <div className="skill-editor__cols">
            <div className="skill-editor__col">
              <h4 className="skill-editor__col-title">節點目錄</h4>
              <NodeCatalog onInsert={insertNode} />
            </div>

            <div className="skill-editor__col">
              <h4 className="skill-editor__col-title">YAML 編輯器</h4>
              <YamlEditor
                ref={taRef}
                value={definition}
                onChange={setDefinition}
                label="Skill YAML 定義"
              />
            </div>

            <div className="skill-editor__col">
              <h4 className="skill-editor__col-title">
                驗證結果 {validating && <span className="muted">驗證中…</span>}
              </h4>
              {!validation ? (
                <p className="muted">停止輸入 {DEBOUNCE_MS} 毫秒後自動驗證。</p>
              ) : validation.errors.length === 0 ? (
                <p className="notice-text">✅ 通過所有靜態驗證。</p>
              ) : (
                <ul className="vlist">
                  {validation.errors.map((err, i) => {
                    const warn = WARN_CODES.has(err.code)
                    return (
                      <li key={`${err.code}-${i}`} className={warn ? 'vlist__warn' : 'vlist__err'}>
                        <span className={`chip ${warn ? 'chip--warn' : 'chip--failed'}`}>
                          {warn ? '警告' : '錯誤'}
                        </span>
                        {err.line != null && <strong> 第 {err.line} 行</strong>}
                        <div className="vlist__label">{CODE_LABEL[err.code] ?? err.code}</div>
                        <div className="muted">{err.message}</div>
                      </li>
                    )
                  })}
                </ul>
              )}
            </div>
          </div>
        </section>
      )}
    </div>
  )
}
