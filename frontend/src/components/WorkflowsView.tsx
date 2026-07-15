import { useEffect, useState, type FormEvent } from 'react'
import { listWorkflows, invokeWorkflow } from '../api/workflows'
import { listSkillCatalog, invokeSkill } from '../api/skills'
import type { Runnable, SkillInputField, SkillResult, WorkflowResult } from '../types'
import Markdown from './Markdown'
import NodeCatalog from './NodeCatalog'
import Skeleton from './Skeleton'
import SkillsTab from './SkillsTab'
import TraceView from './TraceView'

/**
 * 執行表單依 input_schema 動態渲染：全為 str 欄位 → 逐欄 text input；
 * 其餘型別（或根本沒有 schema，例如舊 code 工作流）→ 退回 JSON textarea。
 */
function strFields(item: Runnable | null): [string, SkillInputField][] | null {
  const schema = item?.input_schema
  if (!schema) return null
  const fields = Object.entries(schema)
  if (fields.length === 0 || !fields.every(([, f]) => f.type === 'str')) return null
  return fields
}

const SOURCE_LABEL: Record<Runnable['source'], string> = {
  code: 'code',
  builtin: '內建',
  custom: '自訂',
}

/** 從 output 取一個字串型答案欄位用 Markdown 渲染。 */
function answerOf(output: Record<string, unknown>): string | null {
  for (const k of ['final_answer', 'answer', 'summary', 'reply']) {
    const v = output[k]
    if (typeof v === 'string') return v
  }
  return null
}

/** output 的頂層純量鍵 → 摘要 chips（answer_mode / verification / attempts…）。不寫死鍵名。 */
function scalarsOf(output: Record<string, unknown>): [string, string][] {
  return Object.entries(output)
    .filter(([, v]) => typeof v === 'string' || typeof v === 'number' || typeof v === 'boolean')
    .filter(([k]) => !['final_answer', 'answer', 'summary', 'reply'].includes(k))
    .map(([k, v]) => [k, String(v)] as [string, string])
    .slice(0, 8)
}

/** Tab 1：工作流（code）與 skill（builtin/custom）合併清單 + 執行 + 節點軌跡。 */
function RunTab() {
  const [items, setItems] = useState<Runnable[]>([])
  const [listError, setListError] = useState<string | null>(null)
  const [listLoading, setListLoading] = useState(true)
  const [selected, setSelected] = useState<Runnable | null>(null)
  const [values, setValues] = useState<Record<string, string>>({}) // input_schema 各欄位值
  const [json, setJson] = useState('') // 無 schema 時的 JSON 文字
  // 兩條 invoke 路徑的回應形狀不同（workflow 回 {workflow,…}、skill 回 {skill,…}），
  // 但畫面只吃 output，union 即可，不必硬統一成同一個型別。
  const [result, setResult] = useState<WorkflowResult | SkillResult | null>(null)
  const [runError, setRunError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    // 兩個來源各自成敗：skill 端點還沒上線時，工作流清單照樣要出得來（反之亦然）。
    Promise.allSettled([listWorkflows(), listSkillCatalog()])
      .then(([wf, sk]) => {
        const merged: Runnable[] = []
        if (wf.status === 'fulfilled') {
          merged.push(
            ...wf.value.map((w) => ({
              name: w.name,
              description: w.description,
              required_role: w.required_role,
              source: 'code' as const,
              revision: null,
              // node-first D7：code workflow 也可以帶 input_schema；有的話就走與 skill
              // 相同的動態字串欄位表單，缺欄或含非 str 型別由 strFields() 自然退回 JSON。
              input_schema: w.input_schema ?? null,
            })),
          )
        }
        if (sk.status === 'fulfilled') {
          merged.push(...sk.value.map((s) => ({ ...s, revision: s.revision ?? null })))
        }
        setItems(merged)
        const failed = [wf, sk].filter((r) => r.status === 'rejected')
        if (failed.length > 0) {
          setListError((failed[0] as PromiseRejectedResult).reason?.message ?? '清單載入失敗。')
        }
      })
      .finally(() => setListLoading(false))
  }, [])

  function pick(item: Runnable) {
    setSelected(item)
    setValues({})
    setJson('')
    setResult(null)
    setRunError(null)
  }

  async function onRun(e: FormEvent) {
    e.preventDefault()
    if (!selected) return
    setBusy(true)
    setRunError(null)
    setResult(null)
    try {
      let input: Record<string, unknown>
      if (fields) {
        input = {}
        for (const [key, f] of fields) {
          const v = values[key] ?? ''
          // 前端擋必填/長度只是省一趟往返；後端的 422 才是最終邊界。
          if (f.required && !v.trim()) throw new Error(`「${key}」為必填。`)
          if (f.min_length != null && v.length > 0 && v.length < f.min_length) {
            throw new Error(`「${key}」至少需 ${f.min_length} 個字。`)
          }
          if (v !== '') input[key] = v
        }
      } else {
        const parsed: unknown = json.trim() ? JSON.parse(json) : {}
        // 契約要求 input 是 JSON 物件；數字/陣列/字串直接擋下，免送出吃 400。
        if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
          throw new Error('input 必須是 JSON 物件，例如 { "key": "value" }')
        }
        input = parsed as Record<string, unknown>
      }
      const run = selected.source === 'code' ? invokeWorkflow : invokeSkill
      setResult(await run(selected.name, input))
    } catch (err) {
      // 前端驗證、JSON.parse 失敗與 404/403/422/504 皆走這（ApiError.message 或解析錯誤）。
      setRunError((err as Error).message)
    } finally {
      setBusy(false)
    }
  }

  const fields = strFields(selected)
  const answer = result ? answerOf(result.output) : null

  return (
    <div className="wf">
      <div className="wf__list">
        {listError && (
          <p className="error-text" role="alert">
            {listError}
          </p>
        )}
        {listLoading ? (
          <Skeleton rows={3} />
        ) : (
          items.length === 0 && !listError && <p className="muted">尚無可執行的工作流或 Skill。</p>
        )}
        {items.map((f) => (
          <button
            key={`${f.source}:${f.name}`}
            className={`wf__item${selected?.name === f.name && selected.source === f.source ? ' wf__item--active' : ''}`}
            onClick={() => pick(f)}
          >
            <div className="wf__item-name">
              {f.name}{' '}
              <span className={`badge badge--src-${f.source}`}>{SOURCE_LABEL[f.source]}</span>
              <span className={`badge badge--${f.required_role === 'ADMIN' ? 'admin' : 'user'}`}>
                {f.required_role}
              </span>
              {f.source === 'custom' && f.revision != null && (
                <span className="badge badge--user">r{f.revision}</span>
              )}
            </div>
            <div className="wf__item-desc">{f.description}</div>
          </button>
        ))}
      </div>

      <div className="wf__panel">
        {!selected ? (
          <p className="muted">從左側選一個工作流或 Skill。</p>
        ) : (
          <form className="wf-form" onSubmit={onRun}>
            {fields ? (
              fields.map(([key, f]) => (
                <div className="field" key={key}>
                  <label htmlFor={`wf-${key}`}>
                    {key}
                    {f.required && <span className="field-error"> *</span>}
                  </label>
                  <input
                    id={`wf-${key}`}
                    className="input"
                    value={values[key] ?? ''}
                    required={f.required}
                    minLength={f.min_length}
                    onChange={(e) => setValues((v) => ({ ...v, [key]: e.target.value }))}
                  />
                </div>
              ))
            ) : (
              <div className="field">
                <label htmlFor="wf-input">input（JSON 物件）</label>
                <textarea
                  id="wf-input"
                  className="textarea"
                  value={json}
                  onChange={(e) => setJson(e.target.value)}
                  placeholder='{ "key": "value" }'
                />
              </div>
            )}
            <button className="btn btn--primary" type="submit" disabled={busy}>
              {busy ? '執行中…' : '執行'}
            </button>
            {runError && (
              <p className="error-text" role="alert">
                {runError}
              </p>
            )}
          </form>
        )}

        {result && (
          <div className="wf-result">
            <div className="wf-result__chips">
              {scalarsOf(result.output).map(([k, v]) => (
                <span className="chip chip--skip" key={k}>
                  {k}: {v}
                </span>
              ))}
            </div>
            {answer != null && <Markdown>{answer}</Markdown>}
            <TraceView output={result.output} />
            <details>
              <summary>完整 output（JSON）</summary>
              <pre>{JSON.stringify(result.output, null, 2)}</pre>
            </details>
          </div>
        )}
      </div>
    </div>
  )
}

type Tab = 'run' | 'skills' | 'nodes'

const TABS: { id: Tab; label: string; adminOnly?: boolean }[] = [
  { id: 'run', label: '執行' },
  { id: 'skills', label: 'Skill 管理', adminOnly: true },
  { id: 'nodes', label: '節點目錄', adminOnly: true },
]

/**
 * 工作流與 Skill：三分頁（執行／Skill 管理／節點目錄），useState 切換，不新增頂層視圖。
 * Tab 2/3 僅 ADMIN 可見 —— 這只是 UX，真正的權限邊界在後端（每支 API 自己再判一次）。
 */
export default function WorkflowsView({ isAdmin }: { isAdmin: boolean }) {
  const [tab, setTab] = useState<Tab>('run')
  const tabs = TABS.filter((t) => !t.adminOnly || isAdmin)

  return (
    <div className="view">
      <div className="view__head">
        <h2 className="view__title">工作流與 Skill</h2>
      </div>

      <div className="seg config-tabs" role="group" aria-label="工作流與 Skill 分頁">
        {tabs.map((t) => (
          <button
            key={t.id}
            type="button"
            className="btn"
            aria-pressed={tab === t.id}
            onClick={() => setTab(t.id)}
          >
            {t.label}
          </button>
        ))}
      </div>

      {tab === 'run' && <RunTab />}
      {tab === 'skills' && isAdmin && <SkillsTab />}
      {tab === 'nodes' && isAdmin && (
        <div className="nodes-tab">
          <NodeCatalog />
        </div>
      )}
    </div>
  )
}
