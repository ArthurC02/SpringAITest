import { useState, type FormEvent } from 'react'
import type { useDocuments } from '../hooks/useDocuments'

function fmtDate(s: string): string {
  const d = new Date(s)
  return Number.isNaN(d.getTime()) ? s : d.toLocaleString()
}

const STATUS_LABEL: Record<string, string> = {
  processing: '處理中',
  ready: '就緒',
  failed: '失敗',
}

interface Props {
  // 狀態由 AppShell 提升後以 props 傳入(與 copilot action 共用同一份,避免雙重輪詢)。
  documents: ReturnType<typeof useDocuments>
}

/** 文件視圖：新增表單 + 清單，含 202→processing→輪詢至就緒的完整流程（見 useDocuments）。 */
export default function DocumentsView({ documents }: Props) {
  const { docs, loading, error, timedOut, create, remove } = documents
  const [title, setTitle] = useState('')
  const [text, setText] = useState('')
  const [submitError, setSubmitError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function onSubmit(e: FormEvent) {
    e.preventDefault()
    if (!title.trim() || !text.trim()) return
    setBusy(true)
    setSubmitError(null)
    try {
      await create(title.trim(), text.trim())
      setTitle('')
      setText('')
    } catch (err) {
      setSubmitError((err as Error).message)
    } finally {
      setBusy(false)
    }
  }

  async function onDelete(id: string, t: string) {
    if (!window.confirm(`刪除文件「${t}」?`)) return
    try {
      await remove(id)
    } catch (err) {
      setSubmitError((err as Error).message)
    }
  }

  return (
    <div className="view">
      <div className="view__head">
        <h2 className="view__title">文件</h2>
      </div>

      <form className="doc-form" onSubmit={onSubmit}>
        <div className="field">
          <label htmlFor="doc-title">標題</label>
          <input
            id="doc-title"
            className="input"
            value={title}
            onChange={(e) => setTitle(e.target.value)}
            placeholder="文件標題"
          />
        </div>
        <div className="field">
          <label htmlFor="doc-text">內容</label>
          <textarea
            id="doc-text"
            className="textarea"
            value={text}
            onChange={(e) => setText(e.target.value)}
            placeholder="貼上要讓 AI 檢索的文字…"
          />
        </div>
        <button className="btn btn--primary" type="submit" disabled={busy}>
          {busy ? '送出中…' : '新增文件'}
        </button>
        {submitError && <p className="error-text">{submitError}</p>}
      </form>

      {error && <p className="error-text">{error}</p>}
      {timedOut && (
        <p className="muted">仍在處理中，稍後重新整理頁面即可看到最新狀態。</p>
      )}

      {loading && docs.length === 0 ? (
        <p className="muted">載入中…</p>
      ) : docs.length === 0 ? (
        <p className="muted">尚無文件,新增一份讓 AI 檢索。</p>
      ) : (
        <table className="table">
          <thead>
            <tr>
              <th>標題</th>
              <th>狀態</th>
              <th>片段數</th>
              <th>建立時間</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            {docs.map((d) => (
              <tr key={d.id}>
                <td>{d.title}</td>
                <td>
                  <span className={`chip chip--${d.status}`}>
                    {STATUS_LABEL[d.status] ?? d.status}
                  </span>
                </td>
                <td>{d.chunk_count}</td>
                <td className="muted">{fmtDate(d.created_at)}</td>
                <td>
                  <button className="btn btn--danger" onClick={() => onDelete(d.id, d.title)}>
                    刪除
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  )
}
