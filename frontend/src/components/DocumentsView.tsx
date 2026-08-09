import { useRef, useState, type ChangeEvent, type FormEvent } from 'react'
import type { useDocuments } from '../hooks/useDocuments'
import type { DocumentInfo } from '../types'
import { isConflict } from '../api/http'
import { fmtDate } from '../format'
import { extractDocumentText } from '../documentExtract'
import ErrorText from './ErrorText'
import FormField from './FormField'
import { useConfirm } from './ConfirmDialog'
import { useToast } from './Toast'
import Skeleton from './Skeleton'

const STATUS_LABEL: Record<string, string> = {
  processing: '處理中',
  ready: '就緒',
  failed: '失敗',
}

// 「問這份文件」(WS1-a):非 ready 狀態的停用原因文字,掛在按鈕 title 上(沿用既有
// title 屬性當 tooltip 的慣例,見 ChatView 的「新對話」鈕)。
const ASK_DISABLED_REASON: Record<string, string> = {
  processing: '文件仍在處理中,尚無法提問',
  failed: '文件處理失敗,無法提問',
}

function filePickStatus(extracting: boolean, fileName: string, charCount: number): string {
  if (extracting) return '讀取檔案中…'
  if (!fileName) return '尚未選擇檔案'
  return `${fileName}（已讀入 ${charCount} 字）`
}

function submitLabel(extracting: boolean, busy: boolean, retryable: boolean): string {
  if (extracting) return '讀取檔案中…'
  if (busy) return '送出中…'
  if (retryable) return '再試一次'
  return '新增文件'
}

interface Props {
  // 狀態由 AppShell 提升後以 props 傳入(與 copilot action 共用同一份,避免雙重輪詢)。
  documents: ReturnType<typeof useDocuments>
  // 展開副駕並帶入該文件脈絡(見 AppShell.askAboutDocument);不新增 API 欄位。
  onAskDocument: (doc: DocumentInfo) => void
}

/** 文件視圖：新增表單 + 清單，含 202→processing→輪詢至就緒的完整流程（見 useDocuments）。 */
export default function DocumentsView({ documents, onAskDocument }: Props) {
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
  const [extracting, setExtracting] = useState(false)
  const attemptRef = useRef<{ title: string; text: string; key: string } | null>(null)
  const formVersionRef = useRef(0)

  function formChanged() {
    formVersionRef.current += 1
    attemptRef.current = null
    setSubmitError(null)
  }

  // 切換內容來源時清掉另一模式的內容,避免「送出的到底是哪份」的混淆。
  function switchMode(m: 'file' | 'text') {
    if (m === mode) return
    formChanged()
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
    const normalizedTitle = title.trim()
    const normalizedText = text.trim()
    const previousAttempt = attemptRef.current
    const attempt = previousAttempt?.title === normalizedTitle && previousAttempt.text === normalizedText
      ? previousAttempt
      : { title: normalizedTitle, text: normalizedText, key: crypto.randomUUID() }
    attemptRef.current = attempt
    const submittedFormVersion = formVersionRef.current
    setBusy(true)
    setSubmitError(null)
    try {
      await create(attempt.title, attempt.text, attempt.key)
      attemptRef.current = null
      if (formVersionRef.current === submittedFormVersion) {
        setTitle('')
        setText('')
        setFileName('')
        toast('已送出,處理中', 'success')
      }
    } catch (err) {
      if (isConflict(err)) attemptRef.current = null
      if (formVersionRef.current === submittedFormVersion) {
        setSubmitError((err as Error).message)
      }
    } finally {
      setBusy(false)
    }
  }

  // ponytail(WS2-a): PDF/Word 在瀏覽器內用 pdf.js/mammoth 抽字（documentExtract.ts,動態
  // import 懶載入,不進主 bundle）,抽出結果沿用這裡既有的 create(title, text) 路徑,
  // backend 完全不變。.txt/.md 仍直接用 File.text()。
  async function onFile(e: ChangeEvent<HTMLInputElement>) {
    const f = e.target.files?.[0]
    e.target.value = '' // 允許重選同一檔案
    if (!f) return
    formChanged()
    setFileName(f.name)
    let extractedText: string
    if (/\.(pdf|docx)$/i.test(f.name)) {
      setExtracting(true)
      setTextErr('')
      try {
        const result = await extractDocumentText(f)
        if ('errorMessage' in result) {
          setText('')
          setTextErr(result.errorMessage)
          return
        }
        extractedText = result.text
      } finally {
        setExtracting(false)
      }
    } else {
      extractedText = await f.text()
    }
    setText(extractedText)
    setTextErr('')
    if (!title.trim()) {
      setTitle(f.name.replace(/\.(txt|md|pdf|docx)$/i, ''))
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
        <FormField
          id="doc-title"
          label="標題"
          value={title}
          onChange={(v) => {
            formChanged()
            setTitle(v)
            if (titleErr && v.trim()) setTitleErr('')
          }}
          placeholder="文件標題"
          error={titleErr}
        />
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
            <label htmlFor="doc-file">檔案（.txt／.md／.pdf／.docx）</label>
            <div className="file-pick">
              <input
                id="doc-file"
                className="file-pick__input"
                type="file"
                accept=".txt,.md,.pdf,.docx"
                onChange={onFile}
                disabled={extracting}
                aria-invalid={!!textErr}
                aria-describedby={textErr ? 'doc-text-err' : undefined}
              />
              <label htmlFor="doc-file" className="btn">選擇檔案…</label>
              <span className="muted">{filePickStatus(extracting, fileName, text.length)}</span>
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
                formChanged()
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
        <button className="btn btn--info" type="submit" disabled={busy || extracting}>
          {submitLabel(extracting, busy, !!submitError && attemptRef.current !== null)}
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
                <td className="table__actions">
                  <button
                    className="btn"
                    disabled={d.status !== 'ready'}
                    title={ASK_DISABLED_REASON[d.status]}
                    onClick={() => onAskDocument(d)}
                  >
                    問這份文件
                  </button>
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
