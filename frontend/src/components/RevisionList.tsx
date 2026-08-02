import type { ReactNode } from 'react'
import Skeleton from './Skeleton'
import { runWithToast, useToast } from './Toast'

interface RevisionItem {
  revision: number
  definition_sha256: string
}

/**
 * Workflow / Orchestrator 共用的「revision 清單 + 還原」區塊（不含外層 section/標題，
 * 呼叫端保留 —— 兩邊標題文字不同）。AgentEditor 的歷史頁另含 skill_bindings、回溯前需
 * confirm() 確認，結構不同，刻意不塞進同一個元件。
 */
export default function RevisionList<T extends RevisionItem>({
  loading,
  revisions,
  restoreLabel = '還原',
  successMessage,
  onRestore,
  onRestored,
  renderExtra,
}: {
  loading: boolean
  revisions: T[]
  restoreLabel?: string
  successMessage: string
  onRestore: (revision: number) => Promise<unknown>
  onRestored: () => void
  renderExtra?: (revision: T) => ReactNode
}) {
  const toast = useToast()
  if (loading) return <Skeleton rows={2} />
  return (
    <ul className="agent-preview__list">
      {revisions.map((r) => (
        <li key={r.revision}>
          <span>
            r{r.revision} · {r.definition_sha256.slice(0, 12)}
            {renderExtra?.(r)}
          </span>
          <button
            className="btn"
            onClick={() =>
              void runWithToast(toast, () => onRestore(r.revision), {
                success: successMessage,
                onSuccess: onRestored,
              })
            }
          >
            {restoreLabel}
          </button>
        </li>
      ))}
    </ul>
  )
}
