import { useCallback, useEffect, useState } from 'react'
import { listConfig, updateConfig } from '../api/config'
import type { ConfigEntry } from '../types'
import { fmtDate } from '../format'
import { useToast } from './Toast'
import Skeleton from './Skeleton'
import SkillHome from './SkillHome'
import NodeParamsTab from './NodeParamsTab'

// 系統設定重構（設計 §1）：Skill 功能樹進駐系統設定，工作流唯讀 tab 退場，
// 頂層導覽的「工作流與 Skill」視圖一併移除。順序 = Skill 優先、一般設定墊底。
type Tab = 'skill' | 'nodeParams' | 'general'

const TABS: { id: Tab; label: string }[] = [
  { id: 'skill', label: 'Skill' },
  { id: 'nodeParams', label: '工作流節點參數' },
  { id: 'general', label: '一般設定' },
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

/** 系統設定:三分頁容器(Skill/工作流節點參數/一般設定),用 useState 切換,無 router。 */
export default function ConfigView({ isAdmin }: { isAdmin: boolean }) {
  const [tab, setTab] = useState<Tab>('skill')

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

      {tab === 'skill' && <SkillHome isAdmin={isAdmin} />}
      {tab === 'nodeParams' && <NodeParamsTab isAdmin={isAdmin} />}
      {tab === 'general' && <GeneralConfigTab isAdmin={isAdmin} />}
    </div>
  )
}
