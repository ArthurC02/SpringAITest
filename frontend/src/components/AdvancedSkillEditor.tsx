import { useCallback, useEffect, useRef, useState } from 'react'
import { ApiError } from '../api/http'
import { createSkill, exportSkill, getSkill, updateSkill, validateSkill } from '../api/skills'
import type { NodeInfo, Skill, SkillValidation } from '../types'
import { CODE_LABEL, WARN_CODES } from '../skills/validationLabels'
import NodeCatalog from './NodeCatalog'
import YamlEditor from './YamlEditor'
import { useToast } from './Toast'

const DEBOUNCE_MS = 800

export type AdvancedMode =
  | { kind: 'create' }
  | { kind: 'edit'; name: string }
  | { kind: 'view'; name: string } // 內建 kb_query：唯讀檢視，無存檔/驗證

interface Props {
  mode: AdvancedMode
  initialDefinition: string
  saved?: Skill | null
  /** 存檔成功後回呼（create 時 name 未知傳 null）。呼叫端據此刷新清單。 */
  onSaved: (name: string | null) => void
  onClose: () => void
}

/**
 * 進階 Skill 編輯器：三欄（節點目錄 / YAML / 驗證結果）。從舊 SkillsTab 抽出，供
 * 兩門共用（簡單模式「進階編輯」單向交棒、既有 skill 直接編輯、內建 kb_query 唯讀檢視）。
 * ponytail: 存檔/驗證 state 內聚在本元件（不上抬給呼叫端），比設計稿的受控 props 少一層鑽孔、行為一致。
 */
export default function AdvancedSkillEditor({ mode, initialDefinition, saved, onSaved, onClose }: Props) {
  const toast = useToast()
  const readOnly = mode.kind === 'view'
  const [definition, setDefinition] = useState(initialDefinition)
  const [savedState, setSavedState] = useState<Skill | null>(saved ?? null)
  const [validation, setValidation] = useState<SkillValidation | null>(null)
  const [validating, setValidating] = useState(false)
  const [formError, setFormError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const taRef = useRef<HTMLTextAreaElement>(null)
  const seqRef = useRef(0) // 丟棄過期驗證回應

  const runValidate = useCallback(async (def: string) => {
    const seq = ++seqRef.current
    setValidating(true)
    try {
      const v = await validateSkill(def)
      if (seq === seqRef.current) setValidation(v)
    } catch (e) {
      if (seq === seqRef.current) {
        setValidation({ valid: false, errors: [{ code: '', message: (e as Error).message }] })
      }
    } finally {
      if (seq === seqRef.current) setValidating(false)
    }
  }, [])

  // 停止輸入 800ms 後自動驗證（view 模式不驗）。
  useEffect(() => {
    if (readOnly || !definition.trim()) {
      setValidation(null)
      return
    }
    const t = window.setTimeout(() => runValidate(definition), DEBOUNCE_MS)
    return () => window.clearTimeout(t)
  }, [definition, readOnly, runValidate])

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
    const caret = before.length + lead.length + snippet.length
    requestAnimationFrame(() => {
      ta.focus()
      ta.setSelectionRange(caret, caret)
    })
  }

  async function onSave() {
    if (readOnly) return
    setBusy(true)
    setFormError(null)
    try {
      if (mode.kind === 'edit') {
        await updateSkill(mode.name, definition)
        const fresh = await getSkill(mode.name)
        setSavedState(fresh)
        setDefinition(fresh.definition ?? definition)
        toast(`已儲存（r${fresh.current_revision}）`, 'success')
        onSaved(mode.name)
      } else {
        // 新建：名稱由後端從 YAML 解析（重複 → 409）。
        await createSkill(definition)
        toast('已建立', 'success')
        onSaved(null)
      }
    } catch (e) {
      if (e instanceof ApiError && e.fieldErrors) {
        setValidation({
          valid: false,
          errors: Object.entries(e.fieldErrors).map(([code, message]) => ({ code, message })),
        })
      }
      let msg = (e as Error).message
      if (e instanceof ApiError && e.status === 409) msg = `名稱已存在：${e.message}`
      else if (e instanceof ApiError && e.status === 422) msg = `驗證未過：${e.message}`
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

  const blocking = validation?.errors.filter((e) => !WARN_CODES.has(e.code)) ?? []
  const canSave = !readOnly && !busy && !validating && blocking.length === 0 && definition.trim().length > 0
  const name = mode.kind === 'create' ? null : mode.name

  let title = '新增 Skill（進階）'
  if (mode.kind === 'view') title = `檢視 ${name}`
  else if (name) title = `編輯 ${name}`

  return (
    <section className="skill-editor">
      <div className="skill-editor__head">
        <h3 className="skill-editor__title">
          {title}
          {savedState && <span className="badge badge--user">r{savedState.current_revision}</span>}
        </h3>
        <div className="skill-editor__actions">
          {!readOnly && (
            <button
              className="btn"
              type="button"
              onClick={() => runValidate(definition)}
              disabled={busy || !definition.trim()}
            >
              驗證
            </button>
          )}
          {!readOnly && (
            <button className="btn btn--primary" type="button" onClick={onSave} disabled={!canSave}>
              {busy ? '儲存中…' : '儲存'}
            </button>
          )}
          {name && (
            <button className="btn" type="button" onClick={() => onExport(name)} disabled={busy}>
              匯出
            </button>
          )}
          <button className="btn" type="button" onClick={onClose} disabled={busy}>
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
        {!readOnly && (
          <div className="skill-editor__col">
            <h4 className="skill-editor__col-title">節點目錄</h4>
            <NodeCatalog onInsert={insertNode} />
          </div>
        )}

        <div className="skill-editor__col">
          <h4 className="skill-editor__col-title">YAML 編輯器</h4>
          <YamlEditor
            ref={taRef}
            value={definition}
            onChange={readOnly ? undefined : setDefinition}
            readOnly={readOnly}
            label="Skill YAML 定義"
          />
        </div>

        {!readOnly && (
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
        )}
      </div>
    </section>
  )
}
