interface FeatureStatusFlags {
  agentBuilderEnabled: boolean
  agentTestRunEnabled: boolean
  workflowDesignerEnabled: boolean
  multiAgentDispatchEnabled: boolean
  agentChatEnabled: boolean
  agentWriteToolsEnabled: boolean
  runDiscoveryEnabled: boolean
  agentTriggersEnabled: boolean
}

/** [鍵, 人話名稱, 一句話說明] —— 說明只描述「這是什麼」，不解讀因果或內部依賴關係。 */
const FEATURE_LABELS: [keyof FeatureStatusFlags, string, string][] = [
  ['agentBuilderEnabled', 'Agent 建置器', '建立與管理 Agent 草稿、發布、業務規則。'],
  ['agentTestRunEnabled', 'Agent 測試主控台', '對已發布 Agent 執行一次性測試。'],
  ['workflowDesignerEnabled', '流程設計器', '設計與發布多 Agent 協作流程。'],
  ['multiAgentDispatchEnabled', '多 Agent 協作派工', '執行已發布的多 Agent 協作流程。'],
  ['agentChatEnabled', 'Agent 協作聊天', '在聊天畫面選擇已發布的協作流程(僅白名單租戶開放)。'],
  ['agentWriteToolsEnabled', '寫入核准與治理', 'Agent 具備寫入能力時的核准佇列與上線治理儀表板。'],
  ['runDiscoveryEnabled', '執行總覽', '不需知道執行 ID 也能瀏覽自己的 Agent 與協作執行紀錄。'],
  ['agentTriggersEnabled', '排程觸發', '在指定時刻自動啟動一次已發布的協作流程。'],
]

/**
 * W4 功能開通狀態頁(規格 §4):純呈現既有 `GET /api/features` 已取回的旗標
 * (由 AppShell 登入後取一次並傳入,這裡不重新呼叫、不新增端點)。只呈現「目前狀態」,
 * 不解讀成因或內部依賴關係——那可能反過來洩漏部署細節。
 */
export default function FeatureStatusView({ flags }: { flags: FeatureStatusFlags }) {
  return (
    <section className="agent-block">
      <h2>功能開通狀態</h2>
      <p className="muted">
        以下狀態依這個系統目前的部署設定而定,並非個別租戶各自的開關;僅呈現目前狀態,不代表任何功能理應開通或關閉。
      </p>
      <div className="table-wrap">
        <table className="table">
          <thead>
            <tr>
              <th>功能</th>
              <th>說明</th>
              <th>目前狀態</th>
            </tr>
          </thead>
          <tbody>
            {FEATURE_LABELS.map(([key, label, desc]) => (
              <tr key={key}>
                <td>{label}</td>
                <td className="muted">{desc}</td>
                <td>
                  <span className={`chip ${flags[key] ? 'chip--ready' : 'chip--skip'}`}>
                    {flags[key] ? '已開通' : '未開通'}
                  </span>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  )
}
