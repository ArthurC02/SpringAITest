import { useState, type FormEvent } from 'react'
import { invokeSkill } from '../api/skills'
import { ApiError } from '../api/http'
import type { SkillInputField, SkillResult } from '../types'
import { answerOf, ANSWER_KEYS } from '../skills/answerOf'
import ErrorText from './ErrorText'
import Markdown from './Markdown'
import TraceView from './TraceView'

type Kind = 'str' | 'int' | 'float' | 'bool' | 'list' | 'dict'
/** 未知型別回落成 str（text input，字串送出）。內建 schema 只有這六型。 */
const TYPED_KINDS: readonly string[] = ['int', 'float', 'bool', 'list', 'dict']
function kindOf(type: string): Kind {
  return TYPED_KINDS.includes(type) ? (type as Kind) : 'str'
}

/** 有非空 input_schema → 逐欄依型別渲染；否則（或無 schema）退回整體 JSON textarea。 */
function schemaFields(
  schema?: Record<string, SkillInputField> | null,
): [string, SkillInputField][] | null {
  if (!schema) return null
  const fields = Object.entries(schema)
  return fields.length > 0 ? fields : null
}

/** output 頂層純量鍵 → 摘要 chips（不寫死鍵名，排除答案欄位）。 */
function scalarsOf(output: Record<string, unknown>): [string, string][] {
  return Object.entries(output)
    .filter(([, v]) => typeof v === 'string' || typeof v === 'number' || typeof v === 'boolean')
    .filter(([k]) => !ANSWER_KEYS.includes(k))
    .map(([k, v]) => [k, String(v)] as [string, string])
    .slice(0, 8)
}

/**
 * 依 schema 逐欄組 typed payload；回傳 { input } 或 { errors }（逐欄中文錯誤）。
 * 留空規則:所有選填欄位空值一律省略該鍵;required + 空 → 「必填」。
 */
function buildPayload(
  fields: [string, SkillInputField][],
  values: Record<string, string | boolean>,
): { input?: Record<string, unknown>; errors?: Record<string, string> } {
  const input: Record<string, unknown> = {}
  const errors: Record<string, string> = {}

  for (const [key, f] of fields) {
    const kind = kindOf(f.type)
    const raw = values[key]

    if (kind === 'bool') {
      if (raw === true) input[key] = true
      else if (f.required) errors[key] = `「${key}」為必填。`
      continue
    }

    const v = typeof raw === 'string' ? raw : ''
    const trimmed = v.trim()

    if (trimmed === '') {
      if (f.required) errors[key] = `「${key}」為必填。`
      continue // 選填留空 → 省略該鍵
    }

    if (kind === 'str') {
      if (f.min_length != null && v.length < f.min_length) {
        errors[key] = `「${key}」至少需 ${f.min_length} 個字。`
      } else {
        input[key] = v
      }
      continue
    }

    if (kind === 'int') {
      const n = Number(trimmed)
      if (!Number.isInteger(n)) errors[key] = `「${key}」必須是整數。`
      else input[key] = n
      continue
    }

    if (kind === 'float') {
      const n = Number(trimmed)
      if (!Number.isFinite(n)) errors[key] = `「${key}」必須是數字。`
      else input[key] = n
      continue
    }

    // list / dict：該欄自己的 JSON。空白已在上面省略，絕不進 JSON.parse。
    let parsed: unknown
    try {
      parsed = JSON.parse(v)
    } catch {
      errors[key] = `「${key}」不是有效的 JSON。`
      continue
    }
    if (kind === 'list') {
      if (Array.isArray(parsed)) input[key] = parsed
      else errors[key] = `「${key}」必須是清單。`
    } else {
      if (parsed !== null && typeof parsed === 'object' && !Array.isArray(parsed)) input[key] = parsed
      else errors[key] = `「${key}」必須是物件。`
    }
  }

  return Object.keys(errors).length > 0 ? { errors } : { input }
}

interface Props {
  name: string
  inputSchema?: Record<string, SkillInputField> | null
}

/** 試跑子功能：以正式 invokeSkill 執行已存在的 skill，展開節點軌跡（存後試）。 */
export default function SkillRunPanel({ name, inputSchema }: Props) {
  const [values, setValues] = useState<Record<string, string | boolean>>({})
  const [json, setJson] = useState('')
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({})
  const [result, setResult] = useState<SkillResult | null>(null)
  const [runError, setRunError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const fields = schemaFields(inputSchema)

  function setField(key: string, value: string | boolean) {
    setValues((v) => ({ ...v, [key]: value }))
    setFieldErrors((fe) => {
      if (!fe[key]) return fe
      const { [key]: _drop, ...rest } = fe
      return rest
    })
  }

  async function onRun(e: FormEvent) {
    e.preventDefault()
    setRunError(null)
    setFieldErrors({})
    setResult(null)

    let input: Record<string, unknown>
    if (fields) {
      const built = buildPayload(fields, values)
      if (built.errors) {
        setFieldErrors(built.errors)
        return
      }
      input = built.input!
    } else {
      try {
        const parsed: unknown = json.trim() ? JSON.parse(json) : {}
        if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
          throw new Error('input 必須是 JSON 物件，例如 { "key": "value" }')
        }
        input = parsed as Record<string, unknown>
      } catch (err) {
        setRunError((err as Error).message)
        return
      }
    }

    setBusy(true)
    try {
      setResult(await invokeSkill(name, input))
    } catch (err) {
      // 伺服器端逐欄錯誤（camelCase fieldErrors）→ 映到對應欄位;對不上或無 → 整體錯誤。
      const known =
        err instanceof ApiError && err.fieldErrors && fields
          ? Object.fromEntries(
              Object.entries(err.fieldErrors).filter(([k]) => k in (inputSchema ?? {})),
            )
          : {}
      if (Object.keys(known).length > 0) setFieldErrors(known)
      else setRunError((err as Error).message)
    } finally {
      setBusy(false)
    }
  }

  const answer = result ? answerOf(result.output) : null

  return (
    <div className="wf__panel">
      <form className="wf-form" onSubmit={onRun}>
        {fields ? (
          fields.map(([key, f]) => {
            const kind = kindOf(f.type)
            return (
              <div className="field" key={key}>
                <label htmlFor={`run-${key}`}>
                  {key}
                  {f.required && <span className="field-error"> *</span>}
                </label>
                {kind === 'bool' ? (
                  <input
                    id={`run-${key}`}
                    type="checkbox"
                    checked={values[key] === true}
                    onChange={(e) => setField(key, e.target.checked)}
                  />
                ) : kind === 'list' || kind === 'dict' ? (
                  <textarea
                    id={`run-${key}`}
                    className="textarea textarea--mono"
                    value={typeof values[key] === 'string' ? (values[key] as string) : ''}
                    placeholder={kind === 'list' ? '[]' : '{ }'}
                    onChange={(e) => setField(key, e.target.value)}
                  />
                ) : (
                  <input
                    id={`run-${key}`}
                    className="input"
                    type={kind === 'str' ? 'text' : 'number'}
                    step={kind === 'int' ? '1' : kind === 'float' ? 'any' : undefined}
                    value={typeof values[key] === 'string' ? (values[key] as string) : ''}
                    onChange={(e) => setField(key, e.target.value)}
                  />
                )}
                {fieldErrors[key] && (
                  <p className="field-error" role="alert">
                    {fieldErrors[key]}
                  </p>
                )}
              </div>
            )
          })
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
        <ErrorText msg={runError} />
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
