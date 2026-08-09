import { useLayoutEffect, useMemo, useState } from 'react'
import { listConfig, updateConfig } from '../api/config'
import { AGENT_DEFAULT_CONFIG_FALLBACKS } from '../agentBuilder'
import type { ConfigEntry } from '../types'
import { fmtDate } from '../format'
import { useResource } from '../hooks/useResource'
import ErrorText from './ErrorText'
import { useToast } from './Toast'
import Skeleton from './Skeleton'
import AgentSkillHome from './AgentSkillHome'
import BusinessWorkflowHome from './BusinessWorkflowHome'
import NodeParamsTab from './NodeParamsTab'

// Agent Skill 與 Business Workflow 是兩個平級入口；Harness 節點參數維持獨立設定頁。
type Tab = 'businessWorkflows' | 'agentSkills' | 'nodeParams' | 'general'

const TABS: { id: Tab; label: string }[] = [
  { id: 'businessWorkflows', label: '業務流程' },
  { id: 'agentSkills', label: 'Agent Skills' },
  { id: 'nodeParams', label: '執行參數' },
  { id: 'general', label: '一般設定' },
]

/** 一般設定：GET 表格；就地編輯 value + 儲存(PUT)。GET/PUT 兩端都只開放 ADMIN
 *  (側欄 adminOnly + 後端 [AdminOnly]),下面的 isAdmin 分支只是第二道防線。 */
function GeneralConfigTab({ isAdmin }: { isAdmin: boolean }) {
  const toast = useToast()
  const { data, loading, error } = useResource(listConfig)
  const [entries, setEntries] = useState<ConfigEntry[]>([])
  const [drafts, setDrafts] = useState<Record<string, string>>({})
  const [saveError, setSaveError] = useState<string | null>(null)
  const [savingKey, setSavingKey] = useState<string | null>(null)

  // data 到齊後鏡射為可就地編輯的 entries + drafts。用 useLayoutEffect 在 paint 前寫入:
  // 避免 loading 轉 false 與此衍生之間夾一格「尚無設定」/空輸入框的閃爍(載入不得渲染成無資料)。
  useLayoutEffect(() => {
    if (!data) return
    setEntries(data)
    setDrafts(Object.fromEntries(data.map((e) => [e.key, e.value])))
  }, [data])

  // app_config 沒有 seed,所以 Agent 建立預設值的 key 起初不存在。合併顯示 fallback,
  // 讓「可在系統設定中調整」是真的;ADMIN 儲存時 PUT 會 upsert 建立該筆。
  const rows = useMemo(() => {
    const stored = new Set(entries.map((e) => e.key))
    const unset = Object.entries(AGENT_DEFAULT_CONFIG_FALLBACKS)
      .filter(([key]) => !stored.has(key))
      .map(([key, value]) => ({ key, value, updatedAt: '', unset: true }))
    return [...entries.map((e) => ({ ...e, unset: false })), ...unset]
  }, [entries])

  async function save(key: string, value: string) {
    setSavingKey(key)
    setSaveError(null)
    try {
      const updated = await updateConfig(key, value)
      setEntries((prev) =>
        prev.some((e) => e.key === key)
          ? prev.map((e) => (e.key === key ? updated : e))
          : [...prev, updated],
      )
      toast('已儲存', 'success')
    } catch (e) {
      // 儲存失敗在此顯示;真正授權以後端把關為準(非 ADMIN 連這個畫面都進不來)。
      setSaveError((e as Error).message)
    } finally {
      setSavingKey(null)
    }
  }

  return (
    <>
      <ErrorText msg={error ?? saveError} />

      {/* 讀寫皆需 ADMIN(由後端把關);app_config 以 (tenant_id, key) 複合主鍵做租戶隔離,
          GET/PUT 都經 RequireTenant() —— 每個租戶各有一份值,跨租戶讀不到。 */}
      <p className="muted" role="note">
        此處所有設定值(含 <code>agent.defaults.system_prompt</code>)僅適用於目前租戶:同租戶的其他
        ADMIN 可讀寫,並會成為本租戶建立 Agent 時的預設值;不影響其他租戶。
      </p>

      {loading && entries.length === 0 ? (
        <Skeleton rows={4} />
      ) : (
        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr>
                <th>設定鍵</th>
                <th>設定值</th>
                <th>更新時間</th>
                {isAdmin && <th></th>}
              </tr>
            </thead>
            <tbody>
              {rows.map((e) => (
                <tr key={e.key}>
                  <td>{e.key}</td>
                  <td>
                    {!isAdmin ? (
                      e.value
                    ) : // <input> 會吃掉換行,多行值(例如預設 System Prompt)必須用 textarea 編輯,
                    // 否則按下儲存就靜默把換行壓平。
                    (drafts[e.key] ?? e.value).includes('\n') ? (
                      <textarea
                        className="textarea"
                        aria-label={e.key}
                        value={drafts[e.key] ?? e.value}
                        onChange={(ev) =>
                          setDrafts((prev) => ({ ...prev, [e.key]: ev.target.value }))
                        }
                      />
                    ) : (
                      <input
                        className="input"
                        aria-label={e.key}
                        value={drafts[e.key] ?? e.value}
                        onChange={(ev) =>
                          setDrafts((prev) => ({ ...prev, [e.key]: ev.target.value }))
                        }
                      />
                    )}
                  </td>
                  <td className="muted">
                    {e.unset ? '預設值(尚未設定)' : fmtDate(e.updatedAt)}
                  </td>
                  {isAdmin && (
                    <td>
                      <button
                        className="btn btn--primary"
                        onClick={() => save(e.key, drafts[e.key] ?? e.value)}
                        disabled={
                          savingKey === e.key || (!e.unset && (drafts[e.key] ?? e.value) === e.value)
                        }
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

/** 系統設定：四個平級分頁，用 useState 切換，無 router。 */
export default function ConfigView({ isAdmin }: { isAdmin: boolean }) {
  const [tab, setTab] = useState<Tab>('businessWorkflows')

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

      {tab === 'businessWorkflows' && <BusinessWorkflowHome isAdmin={isAdmin} />}
      {tab === 'agentSkills' && <AgentSkillHome isAdmin={isAdmin} />}
      {tab === 'nodeParams' && <NodeParamsTab isAdmin={isAdmin} />}
      {tab === 'general' && <GeneralConfigTab isAdmin={isAdmin} />}
    </div>
  )
}
