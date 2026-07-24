import { useState, type ChangeEvent, type FormEvent } from 'react'
import type { useDocuments } from '../hooks/useDocuments'
import { fmtDate } from '../format'
import ErrorText from './ErrorText'
import { useConfirm } from './ConfirmDialog'
import { useToast } from './Toast'
import Skeleton from './Skeleton'

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
  const toast = useToast()
  const confirm = useConfirm()
  const [title, setTitle] = useState('')
  const [text, setText] = useState('')
  const [titleErr, setTitleErr] = useState('')
  const [textErr, setTextErr] = useState('')
  const [submitError, setSubmitError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [fileName, setFileName] = useState('')
  const [mode, setMode] = useState<'file' | 'text'>('file')

  // 切換內容來源時清掉另一模式的內容,避免「送出的到底是哪份」的混淆。
  function switchMode(m: 'file' | 'text') {
    if (m === mode) return
    setMode(m)
    setText('')
    setTextErr('')
    setFileName('')
  }

  async function onSubmit(e: FormEvent) {
    e.preventDefault()
    // 空 title/text 不再靜默 return，改顯示緊貼欄位的 inline 錯誤。
    const te = title.trim() ? '' : '請填寫標題'
    const xe = text.trim() ? '' : mode === 'file' ? '請選擇檔案' : '請填寫內容'
    setTitleErr(te)
    setTextErr(xe)
    if (te || xe) return
    setBusy(true)
    setSubmitError(null)
    try {
      await create(title.trim(), text.trim())
      setTitle('')
      setText('')
      setFileName('')
      toast('已送出,處理中', 'success')
    } catch (err) {
      setSubmitError((err as Error).message)
    } finally {
      setBusy(false)
    }
  }

  // ponytail: 檔案匯入只做前端讀文字填表單,API 不變;PDF/Word 解析需要後端支援時再加。
  async function onFile(e: ChangeEvent<HTMLInputElement>) {
    const f = e.target.files?.[0]
    e.target.value = '' // 允許重選同一檔案
    if (!f) return
    setText(await f.text())
    setFileName(f.name)
    setTextErr('')
    if (!title.trim()) {
      setTitle(f.name.replace(/\.(txt|md)$/i, ''))
      setTitleErr('')
    }
  }

  async function onDelete(id: string, t: string) {
    if (!(await confirm(`刪除文件「${t}」?`, { danger: true, confirmLabel: '刪除' }))) return
    try {
      await remove(id)
      toast('已刪除', 'success')
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
            onChange={(e) => {
              setTitle(e.target.value)
              if (titleErr && e.target.value.trim()) setTitleErr('')
            }}
            placeholder="文件標題"
            aria-invalid={!!titleErr}
            aria-describedby={titleErr ? 'doc-title-err' : undefined}
          />
          {titleErr && (
            <span className="field-error" id="doc-title-err" role="alert">
              {titleErr}
            </span>
          )}
        </div>
        <div className="field">
          <span id="doc-source-label">內容來源</span>
          <div className="seg" role="group" aria-labelledby="doc-source-label">
            <button
              type="button"
              className="btn"
              aria-pressed={mode === 'file'}
              onClick={() => switchMode('file')}
            >
              上傳檔案
            </button>
            <button
              type="button"
              className="btn"
              aria-pressed={mode === 'text'}
              onClick={() => switchMode('text')}
            >
              貼上文字
            </button>
          </div>
        </div>
        {mode === 'file' ? (
          <div className="field">
            <label htmlFor="doc-file">檔案（.txt／.md）</label>
            <div className="file-pick">
              <input
                id="doc-file"
                className="file-pick__input"
                type="file"
                accept=".txt,.md"
                onChange={onFile}
                aria-invalid={!!textErr}
                aria-describedby={textErr ? 'doc-text-err' : undefined}
              />
              <label htmlFor="doc-file" className="btn">選擇檔案…</label>
              <span className="muted">
                {fileName ? `${fileName}（已讀入 ${text.length} 字）` : '尚未選擇檔案'}
              </span>
            </div>
            {textErr && (
              <span className="field-error" id="doc-text-err" role="alert">
                {textErr}
              </span>
            )}
          </div>
        ) : (
          <div className="field">
            <label htmlFor="doc-text">內容</label>
            <textarea
              id="doc-text"
              className="textarea"
              value={text}
              onChange={(e) => {
                setText(e.target.value)
                if (textErr && e.target.value.trim()) setTextErr('')
              }}
              placeholder="貼上要讓 AI 檢索的文字…"
              aria-invalid={!!textErr}
              aria-describedby={textErr ? 'doc-text-err' : undefined}
            />
            {textErr && (
              <span className="field-error" id="doc-text-err" role="alert">
                {textErr}
              </span>
            )}
          </div>
        )}
        <button className="btn btn--info" type="submit" disabled={busy}>
          {busy ? '送出中…' : '新增文件'}
        </button>
        <ErrorText msg={submitError} />
      </form>

      <ErrorText msg={error} />
      {timedOut && (
        <p className="muted">仍在處理中，稍後重新整理頁面即可看到最新狀態。</p>
      )}

      {loading && docs.length === 0 ? (
        <div className="table-wrap">
          <Skeleton rows={4} />
        </div>
      ) : docs.length === 0 ? (
        <p className="muted">尚無文件,新增一份讓 AI 檢索。</p>
      ) : (
        <div className="table-wrap">
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
        </div>
      )}
    </div>
  )
}
