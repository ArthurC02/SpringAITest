import { useEffect, useState, type FormEvent } from 'react'
import { listWorkflows, invokeWorkflow } from '../api/workflows'
import type { WorkflowInfo, WorkflowResult } from '../types'
import Markdown from './Markdown'
import Skeleton from './Skeleton'

// 已知工作流 → 友善單欄輸入（前端硬編碼映射）。未知名稱 fallback 成 JSON textarea。
// ponytail: 硬編碼映射，天花板是每加一個工作流要改這裡；
//           升級路徑是後端在 /api/workflows 回傳 input schema 後改為動態渲染。
const KNOWN: Record<string, { field: string; label: string; multiline: boolean }> = {
  summarize: { field: 'text', label: '要摘要的文字', multiline: true },
  triage: { field: 'question', label: '問題', multiline: false },
  rag_qa: { field: 'question', label: '問題', multiline: false },
}

/** 從 output 取一個字串型答案欄位（answer/summary/reply 擇一存在）用 Markdown 渲染。 */
function answerOf(output: Record<string, unknown>): string | null {
  for (const k of ['answer', 'summary', 'reply']) {
    const v = output[k]
    if (typeof v === 'string') return v
  }
  return null
}

export default function WorkflowsView() {
  const [flows, setFlows] = useState<WorkflowInfo[]>([])
  const [listError, setListError] = useState<string | null>(null)
  const [listLoading, setListLoading] = useState(true)
  const [selected, setSelected] = useState<WorkflowInfo | null>(null)
  const [value, setValue] = useState('') // 友善欄位值，或未知工作流的 JSON 文字
  const [result, setResult] = useState<WorkflowResult | null>(null)
  const [runError, setRunError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    listWorkflows()
      .then(setFlows)
      .catch((e) => setListError((e as Error).message))
      .finally(() => setListLoading(false))
  }, [])

  function pick(f: WorkflowInfo) {
    setSelected(f)
    setValue('')
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
      const spec = KNOWN[selected.name]
      let input: Record<string, unknown>
      if (spec) {
        input = { [spec.field]: value }
      } else {
        const parsed: unknown = value.trim() ? JSON.parse(value) : {}
        // 契約要求 input 是 JSON 物件；數字/陣列/字串直接擋下，免送出吃 400。
        if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
          throw new Error('input 必須是 JSON 物件，例如 { "key": "value" }')
        }
        input = parsed as Record<string, unknown>
      }
      setResult(await invokeWorkflow(selected.name, input))
    } catch (err) {
      // JSON.parse 失敗與 404/403/502 皆走這（ApiError.message 或解析錯誤）。
      setRunError((err as Error).message)
    } finally {
      setBusy(false)
    }
  }

  const spec = selected ? KNOWN[selected.name] : undefined
  const answer = result ? answerOf(result.output) : null

  return (
    <div className="view">
      <div className="view__head">
        <h2 className="view__title">工作流</h2>
      </div>

      {listError && (
        <p className="error-text" role="alert">
          {listError}
        </p>
      )}

      <div className="wf">
        <div className="wf__list">
          {listLoading ? (
            <Skeleton rows={3} />
          ) : (
            flows.length === 0 && !listError && <p className="muted">尚無工作流。</p>
          )}
          {flows.map((f) => (
            <button
              key={f.name}
              className={`wf__item${selected?.name === f.name ? ' wf__item--active' : ''}`}
              onClick={() => pick(f)}
            >
              <div className="wf__item-name">
                {f.name}{' '}
                <span className={`badge badge--${f.required_role === 'ADMIN' ? 'admin' : 'user'}`}>
                  {f.required_role}
                </span>
              </div>
              <div className="wf__item-desc">{f.description}</div>
            </button>
          ))}
        </div>

        <div className="wf__panel">
          {!selected ? (
            <p className="muted">從左側選一個工作流。</p>
          ) : (
            <form className="wf-form" onSubmit={onRun}>
              <div className="field">
                <label htmlFor="wf-input">
                  {spec ? spec.label : 'input（JSON 物件）'}
                </label>
                {spec && !spec.multiline ? (
                  <input
                    id="wf-input"
                    className="input"
                    value={value}
                    onChange={(e) => setValue(e.target.value)}
                  />
                ) : (
                  <textarea
                    id="wf-input"
                    className="textarea"
                    value={value}
                    onChange={(e) => setValue(e.target.value)}
                    placeholder={spec ? '' : '{ "key": "value" }'}
                  />
                )}
              </div>
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
              {answer != null && <Markdown>{answer}</Markdown>}
              <details>
                <summary>完整 output（JSON）</summary>
                <pre>{JSON.stringify(result.output, null, 2)}</pre>
              </details>
            </div>
          )}
        </div>
      </div>
    </div>
  )
}
