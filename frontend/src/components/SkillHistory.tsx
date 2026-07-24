import { useCallback, useEffect, useRef, useState } from 'react'
import { listSkillRevisions, restoreSkillRevision } from '../api/skills'
import type { Skill, SkillRevision } from '../types'
import { fmtDate } from '../format'
import { useResource } from '../hooks/useResource'
import { canRestoreRevision, isActiveSkillRequest } from '../skills/revision'
import ErrorText from './ErrorText'
import Skeleton from './Skeleton'
import { useConfirm } from './ConfirmDialog'
import { useToast } from './Toast'

interface Props {
  name: string
  /** 內建 kb-query 不可回溯（非 DB skill）；隱藏回溯鈕。 */
  canRevert?: boolean
  /** false 表示 parent 已處理同步失敗；child 不再顯示成功訊息。 */
  onReverted?: (restored: Skill) => boolean | void | Promise<boolean | void>
  onRestorePendingChange?: (pending: boolean) => void
}

/**
 * 版本控管子功能：唯讀歷史（依 revision 遞減，顯示 sha）＋ 回溯。
 * 回溯一律交由 server 以 historical revision 建立新 revision，不改寫歷史。
 * agentic 的 package bytes 永不下送給 client，因此不可用 definition-only PUT 模擬回溯。
 */
export default function SkillHistory({
  name,
  canRevert = true,
  onReverted,
  onRestorePendingChange,
}: Props) {
  const toast = useToast()
  const confirm = useConfirm()
  const fetchRevisions = useCallback(() => listSkillRevisions(name), [name])
  const { data: revisions, error, reload } = useResource(fetchRevisions)
  const [busy, setBusy] = useState(false)
  const mountedRef = useRef(false)
  const restoreGenerationRef = useRef(0)

  useEffect(() => {
    mountedRef.current = true
    return () => {
      mountedRef.current = false
      restoreGenerationRef.current += 1
    }
  }, [])

  async function revert(r: SkillRevision) {
    if (
      !(await confirm(
        `回溯到 r${r.revision}？將以該版內容產生一個新的 revision，歷史不會被改寫。`,
        { confirmLabel: '回溯' },
      ))
    )
      return
    const generation = ++restoreGenerationRef.current
    const isActive = () => isActiveSkillRequest(
      mountedRef.current,
      generation,
      restoreGenerationRef.current,
    )
    if (!isActive()) return
    setBusy(true)
    if (!isActive()) return
    onRestorePendingChange?.(true)
    try {
      const restored = await restoreSkillRevision(name, r.revision)
      if (!isActive()) return
      const synchronized = await onReverted?.(restored)
      if (!isActive() || synchronized === false) return
      await reload()
      if (!isActive()) return
      toast(`已回溯 r${r.revision}（產生新版）`, 'success')
    } catch (error) {
      if (isActive()) toast((error as Error).message, 'error')
    } finally {
      if (isActive()) {
        setBusy(false)
        if (isActive()) onRestorePendingChange?.(false)
      }
    }
  }

  return (
    <section className="skill-history">
      <div className="skill-editor__head">
        <h3 className="skill-editor__title">版本控管 {name}</h3>
      </div>
      <p className="muted">唯讀文字對照（依 revision 遞減）；Flow 定義或 Agent Skill 套件會由伺服器一併回復。</p>
      <ErrorText msg={error} />
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
                {r.created_by} · {fmtDate(r.created_at)}
              </span>
              <code className="rev__sha">{r.definition_sha256.slice(0, 12)}</code>
              <span className="badge badge--user">{r.kind === 'agentic' ? 'Agent Skill' : 'Flow'}</span>
              {r.kind === 'agentic' && !canRestoreRevision(r) && (
                <span className="muted">此舊版未保存套件，無法回溯</span>
              )}
              {canRevert && r !== revisions[0] && (
                <button
                  className="btn"
                  type="button"
                  disabled={busy || !canRestoreRevision(r)}
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
