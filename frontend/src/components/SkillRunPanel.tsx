import { useState, type FormEvent } from 'react'
import { invokeSkill } from '../api/skills'
import type { SkillInputField, SkillResult } from '../types'
import { answerOf, ANSWER_KEYS } from '../skills/answerOf'
import Markdown from './Markdown'
import TraceView from './TraceView'

/** input_schema 全為 str → 逐欄 text input；否則（或無 schema）退回 JSON textarea。 */
function strFields(
  schema?: Record<string, SkillInputField> | null,
): [string, SkillInputField][] | null {
  if (!schema) return null
  const fields = Object.entries(schema)
  if (fields.length === 0 || !fields.every(([, f]) => f.type === 'str')) return null
  return fields
}

/** output 頂層純量鍵 → 摘要 chips（不寫死鍵名，排除答案欄位）。 */
function scalarsOf(output: Record<string, unknown>): [string, string][] {
  return Object.entries(output)
    .filter(([, v]) => typeof v === 'string' || typeof v === 'number' || typeof v === 'boolean')
    .filter(([k]) => !ANSWER_KEYS.includes(k))
    .map(([k, v]) => [k, String(v)] as [string, string])
    .slice(0, 8)
}

interface Props {
  name: string
  inputSchema?: Record<string, SkillInputField> | null
}

/** 試跑子功能：以正式 invokeSkill 執行已存在的 skill，展開節點軌跡（存後試）。 */
export default function SkillRunPanel({ name, inputSchema }: Props) {
  const [values, setValues] = useState<Record<string, string>>({})
  const [json, setJson] = useState('')
  const [result, setResult] = useState<SkillResult | null>(null)
  const [runError, setRunError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const fields = strFields(inputSchema)

  async function onRun(e: FormEvent) {
    e.preventDefault()
    setBusy(true)
    setRunError(null)
    setResult(null)
    try {
      let input: Record<string, unknown>
      if (fields) {
        input = {}
        for (const [key, f] of fields) {
          const v = values[key] ?? ''
          if (f.required && !v.trim()) throw new Error(`「${key}」為必填。`)
          if (f.min_length != null && v.length > 0 && v.length < f.min_length) {
            throw new Error(`「${key}」至少需 ${f.min_length} 個字。`)
          }
          if (v !== '') input[key] = v
        }
      } else {
        const parsed: unknown = json.trim() ? JSON.parse(json) : {}
        if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
          throw new Error('input 必須是 JSON 物件，例如 { "key": "value" }')
        }
        input = parsed as Record<string, unknown>
      }
      setResult(await invokeSkill(name, input))
    } catch (err) {
      setRunError((err as Error).message)
    } finally {
      setBusy(false)
    }
  }

  const answer = result ? answerOf(result.output) : null

  return (
    <div className="wf__panel">
      <form className="wf-form" onSubmit={onRun}>
        {fields ? (
          fields.map(([key, f]) => (
            <div className="field" key={key}>
              <label htmlFor={`run-${key}`}>
                {key}
                {f.required && <span className="field-error"> *</span>}
              </label>
              <input
                id={`run-${key}`}
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
            <label htmlFor="run-input">input（JSON 物件）</label>
            <textarea
              id="run-input"
              className="textarea"
              value={json}
              onChange={(e) => setJson(e.target.value)}
              placeholder='{ "query": "…" }'
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
  )
}
