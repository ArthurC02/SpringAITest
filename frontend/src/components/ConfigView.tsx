import { useLayoutEffect, useMemo, useState } from 'react'
import { listConfig, updateConfig } from '../api/config'
import { AGENT_DEFAULT_CONFIG_FALLBACKS } from '../agentBuilder'
import type { ConfigEntry } from '../types'
import { fmtDate } from '../format'
import { useResource } from '../hooks/useResource'
import ErrorText from './ErrorText'
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
      // PUT 403(非 ADMIN)或其他錯誤在此顯示;真正授權以後端把關為準。
      setSaveError((e as Error).message)
    } finally {
      setSavingKey(null)
    }
  }

  return (
    <>
      <ErrorText msg={error ?? saveError} />

      {/* GET /api/config 只要求已登入(platform ConfigController 只有 [Authorize],
          app_config 也沒有 tenant 欄位),所以這張表不是私密的 —— ADMIN 在這裡寫的
          System Prompt 內容,別的租戶與一般 USER 都讀得到。 */}
      <p className="muted" role="note">
        此處所有設定值(含 <code>agent.defaults.system_prompt</code>)全平台共用、不分租戶:別的租戶
        ADMIN 會拿它當建立 Agent 的預設值,任何已登入使用者也都能透過 API 讀到全文。請勿填入機密內容
        (例如內部規章原文、客戶資料、金鑰或未公開的商業規則)。
      </p>

      {loading && entries.length === 0 ? (
        <Skeleton rows={4} />
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
