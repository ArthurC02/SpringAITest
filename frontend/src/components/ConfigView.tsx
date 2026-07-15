import { useCallback, useEffect, useState } from 'react'
import { listConfig, updateConfig } from '../api/config'
import { listWorkflows } from '../api/workflows'
import type { ConfigEntry, WorkflowInfo } from '../types'
import { useToast } from './Toast'
import Skeleton from './Skeleton'

function fmtDate(s: string): string {
  const d = new Date(s)
  return Number.isNaN(d.getTime()) ? s : d.toLocaleString()
}

// Skill 管理已移到「工作流與 Skill」視圖的 Tab 2（設計文稿 §5.1：不新增頂層視圖，
// skill 的家在工作流視圖，不在系統設定）。這裡只留一般設定 + 工作流唯讀清單。
type Tab = 'general' | 'workflows'

const TABS: { id: Tab; label: string }[] = [
  { id: 'general', label: '一般設定' },
  { id: 'workflows', label: '工作流' },
]

/** 一般設定：GET 表格；ADMIN 可就地編輯 value + 儲存(PUT)。非 ADMIN 只讀。 */
function GeneralConfigTab({ isAdmin }: { isAdmin: boolean }) {
  const toast = useToast()
  const [entries, setEntries] = useState<ConfigEntry[]>([])
  const [drafts, setDrafts] = useState<Record<string, string>>({})
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const [savingKey, setSavingKey] = useState<string | null>(null)

  const load = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      const list = await listConfig()
      setEntries(list)
      setDrafts(Object.fromEntries(list.map((e) => [e.key, e.value])))
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    load()
  }, [load])

  async function save(key: string) {
    setSavingKey(key)
    setError(null)
    try {
      const updated = await updateConfig(key, drafts[key])
      setEntries((prev) => prev.map((e) => (e.key === key ? updated : e)))
      toast('已儲存', 'success')
    } catch (e) {
      // PUT 403(非 ADMIN)或其他錯誤在此顯示;真正授權以後端把關為準。
      setError((e as Error).message)
    } finally {
      setSavingKey(null)
    }
  }

  return (
    <>
      {error && (
        <p className="error-text" role="alert">
          {error}
        </p>
      )}

      {loading && entries.length === 0 ? (
        <Skeleton rows={4} />
      ) : entries.length === 0 ? (
        <p className="muted">尚無設定。</p>
      ) : (
        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr>
                <th>Key</th>
                <th>Value</th>
                <th>更新時間</th>
                {isAdmin && <th></th>}
              </tr>
            </thead>
            <tbody>
              {entries.map((e) => (
                <tr key={e.key}>
                  <td>{e.key}</td>
                  <td>
                    {isAdmin ? (
                      <input
                        className="input"
                        value={drafts[e.key] ?? ''}
                        onChange={(ev) =>
                          setDrafts((prev) => ({ ...prev, [e.key]: ev.target.value }))
                        }
                      />
                    ) : (
                      e.value
                    )}
                  </td>
                  <td className="muted">{fmtDate(e.updatedAt)}</td>
                  {isAdmin && (
                    <td>
                      <button
                        className="btn btn--primary"
                        onClick={() => save(e.key)}
                        disabled={savingKey === e.key || drafts[e.key] === e.value}
                      >
                        {savingKey === e.key ? '儲存中…' : '儲存'}
                      </button>
                    </td>
                  )}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </>
  )
}

/** 工作流:code 註冊的工作流唯讀清單(無新增/編輯/刪除)。資料源沿用 GET /api/workflows。 */
function WorkflowsConfigTab() {
  const [flows, setFlows] = useState<WorkflowInfo[]>([])
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)

  useEffect(() => {
    listWorkflows()
      .then(setFlows)
      .catch((e) => setError((e as Error).message))
      .finally(() => setLoading(false))
  }, [])

  return (
    <>
      {error && (
        <p className="error-text" role="alert">
          {error}
        </p>
      )}

      {loading ? (
        <Skeleton rows={3} />
      ) : flows.length === 0 && !error ? (
        <p className="muted">尚無工作流。</p>
      ) : (
        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr>
                <th>名稱</th>
                <th>描述</th>
                <th>角色</th>
              </tr>
            </thead>
            <tbody>
              {flows.map((f) => (
                <tr key={f.name}>
                  <td>{f.name}</td>
                  <td>{f.description}</td>
                  <td>
                    <span
                      className={`badge badge--${f.required_role === 'ADMIN' ? 'admin' : 'user'}`}
                    >
                      {f.required_role}
                    </span>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </>
  )
}

/** 系統設定:兩分頁容器(一般設定/工作流),用 useState 切換,無 router。 */
export default function ConfigView({ isAdmin }: { isAdmin: boolean }) {
  const [tab, setTab] = useState<Tab>('general')

  return (
    <div className="view">
      <div className="view__head">
        <h2 className="view__title">系統設定</h2>
      </div>

      <div className="seg config-tabs" role="group" aria-label="系統設定分頁">
        {TABS.map((t) => (
          <button
            key={t.id}
            type="button"
            className="btn"
            aria-pressed={tab === t.id}
            onClick={() => setTab(t.id)}
          >
            {t.label}
          </button>
        ))}
      </div>

      {tab === 'general' && <GeneralConfigTab isAdmin={isAdmin} />}
      {tab === 'workflows' && <WorkflowsConfigTab />}
    </div>
  )
}
