import { useCallback, useEffect, useState } from 'react'
import { listConfig, updateConfig } from '../api/config'
import type { ConfigEntry } from '../types'
import { useToast } from './Toast'
import Skeleton from './Skeleton'

function fmtDate(s: string): string {
  const d = new Date(s)
  return Number.isNaN(d.getTime()) ? s : d.toLocaleString()
}

/** 系統設定:GET 表格；ADMIN 可就地編輯 value + 儲存(PUT)。非 ADMIN 只讀。 */
export default function ConfigView({ isAdmin }: { isAdmin: boolean }) {
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
    <div className="view">
      <div className="view__head">
        <h2 className="view__title">系統設定</h2>
      </div>

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
    </div>
  )
}
