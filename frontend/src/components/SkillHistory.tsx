import { useEffect, useState } from 'react'
import { listSkillRevisions, updateSkill } from '../api/skills'
import type { SkillRevision } from '../types'
import Skeleton from './Skeleton'
import { useToast } from './Toast'

interface Props {
  name: string
  /** 內建 kb_query 不可回溯（非 DB skill）；隱藏回溯鈕。 */
  canRevert?: boolean
  onReverted?: () => void
}

/**
 * 版本控管子功能：唯讀歷史（依 revision 遞減，顯示 sha）＋ 回溯。
 * 回溯 = 取某版 definition 重新 updateSkill 產生新 revision，不改寫歷史（純前端組合）。
 */
export default function SkillHistory({ name, canRevert = true, onReverted }: Props) {
  const toast = useToast()
  const [revisions, setRevisions] = useState<SkillRevision[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function load() {
    setError(null)
    try {
      setRevisions(await listSkillRevisions(name))
    } catch (e) {
      setError((e as Error).message)
    }
  }

  useEffect(() => {
    load()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [name])

  async function revert(r: SkillRevision) {
    if (!window.confirm(`回溯到 r${r.revision}？將以該版內容產生一個新的 revision，歷史不會被改寫。`))
      return
    setBusy(true)
    try {
      await updateSkill(name, r.definition)
      toast(`已回溯 r${r.revision}（產生新版）`, 'success')
      await load()
      onReverted?.()
    } catch (e) {
      toast((e as Error).message, 'error')
    } finally {
      setBusy(false)
    }
  }

  return (
    <section className="skill-history">
      <div className="skill-editor__head">
        <h3 className="skill-editor__title">版本控管 {name}</h3>
      </div>
      <p className="muted">唯讀文字對照（依 revision 遞減），可回溯任一版產生新版。</p>
      {error && (
        <p className="error-text" role="alert">
          {error}
        </p>
      )}
      {!revisions && !error ? (
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
              {canRevert && r !== revisions[0] && (
                <button
                  className="btn"
                  type="button"
                  disabled={busy}
                  onClick={(e) => {
                    e.preventDefault()
                    revert(r)
                  }}
                >
                  回溯此版
                </button>
              )}
            </summary>
            <pre>{r.definition}</pre>
          </details>
        ))
      )}
    </section>
  )
}
