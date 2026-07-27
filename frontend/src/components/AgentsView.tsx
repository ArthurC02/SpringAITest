import { useState } from 'react'
import { deactivateAgent, enableAgent, listAgents } from '../api/agents'
import type { AgentSummary } from '../types'
import { fmtDate } from '../format'
import { useResource } from '../hooks/useResource'
import ErrorText from './ErrorText'
import Skeleton from './Skeleton'
import { useConfirm } from './ConfirmDialog'
import { runWithToast, useToast } from './Toast'
import AgentEditor from './AgentEditor'

/** 清單狀態欄:停用優先,再看是否已發布。 */
function statusOf(a: AgentSummary): { label: string; kind: 'admin' | 'user' } {
  if (!a.enabled) return { label: '已停用', kind: 'admin' }
  if (a.published_revision != null) return { label: '已發布', kind: 'user' }
  return { label: '草稿', kind: 'user' }
}

/**
 * Agent Builder 工作區(D1):清單(名稱/狀態/目前 revision)＋建立精靈＋草稿編輯路由。
 * 入口本身已在 AgentPlatformView 以 features flag 把關;此處寫入操作再依 isAdmin 隱藏(伺服器為權威)。
 * 外層 .view 容器由 AgentPlatformView 提供,此處不再自帶(避免雙重 padding 與巢狀捲動)。
 */
export default function AgentsView({
  isAdmin,
  agentTestRunEnabled,
}: {
  isAdmin: boolean
  agentTestRunEnabled: boolean
}) {
  const toast = useToast()
  const confirm = useConfirm()
  const { data, loading, error, reload } = useResource(listAgents)
  const rows = data ?? []
  // null = 清單;{ id: null } = 建立;{ id: string } = 編輯。
  const [editing, setEditing] = useState<{ id: string | null } | null>(null)

  async function onDeactivate(a: AgentSummary) {
    if (
      !(await confirm(
        `停用 Agent「${a.name}」?停用後不可開始新 run,歷史 revision 仍保留。`,
        { danger: true, confirmLabel: '停用' },
      ))
    )
      return
    await runWithToast(toast, () => deactivateAgent(a.id), {
      success: '已停用',
      onSuccess: () => reload(),
    })
  }

  async function onEnable(a: AgentSummary) {
    if (
      !(await confirm(`重新啟用 Agent「${a.name}」?啟用後可再次開始 run。`, {
        confirmLabel: '重新啟用',
      }))
    )
      return
    await runWithToast(toast, () => enableAgent(a.id), {
      success: '已啟用',
      onSuccess: () => reload(),
    })
  }

  if (editing) {
    return (
      <AgentEditor
        key={editing.id ?? 'new'}
        agentId={editing.id}
        isAdmin={isAdmin}
        agentTestRunEnabled={agentTestRunEnabled}
        onClose={() => {
          setEditing(null)
          void reload()
        }}
        onCreated={(id) => setEditing({ id })}
        onChanged={() => void reload()}
      />
    )
  }

  return (
    <>
      {isAdmin && (
        <div className="skills__bar">
          <button className="btn btn--info" onClick={() => setEditing({ id: null })}>
            ＋ 建立 Agent
          </button>
        </div>
      )}

      <ErrorText msg={error} />

      {loading && rows.length === 0 ? (
        <Skeleton rows={3} />
      ) : rows.length === 0 && !error ? (
        <p className="muted">尚無 Agent。</p>
      ) : (
        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr>
                <th>名稱</th>
                <th>slug</th>
                <th>狀態</th>
                <th>目前 revision</th>
                <th>更新時間</th>
                <th>操作</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((a) => {
                const s = statusOf(a)
                return (
                  <tr key={a.id}>
                    <td>{a.name}</td>
                    <td>{a.slug}</td>
                    <td>
                      <span className={`badge badge--${s.kind}`}>{s.label}</span>
                    </td>
                    <td>{a.published_revision != null ? `r${a.published_revision}` : '—'}</td>
                    <td className="muted">{fmtDate(a.updated_at)}</td>
                    <td className="skills__ops">
                      <button className="btn" onClick={() => setEditing({ id: a.id })}>
                        {isAdmin ? '編輯' : '檢視'}
                      </button>
                      {isAdmin && a.enabled && (
                        <button className="btn btn--danger" onClick={() => onDeactivate(a)}>
                          停用
                        </button>
                      )}
                      {isAdmin && !a.enabled && (
                        <button className="btn" onClick={() => onEnable(a)}>
                          重新啟用
                        </button>
                      )}
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      )}
    </>
  )
}
