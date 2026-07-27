import { useState } from 'react'
import AgentsView from './AgentsView'
import WorkflowsView from './WorkflowsView'
import OrchestratorsView from './OrchestratorsView'

type Tab = 'agents' | 'workflows' | 'orchestrators'

const LABEL: Record<Tab, string> = {
  agents: 'Agents',
  workflows: 'Workflow Designer',
  orchestrators: 'Orchestrators',
}

interface Gates {
  isAdmin: boolean
  agentBuilderEnabled: boolean
  workflowDesignerEnabled: boolean
  canManageWorkflow: boolean
}

interface Props extends Gates {
  agentTestRunEnabled: boolean
  multiAgentDispatchEnabled: boolean
}

/**
 * 可見分頁清單 = 這個工作區的唯一閘門來源。側欄入口(AppShell)也用它決定要不要出現,
 * 兩處共用同一份判斷才不會漂移成「入口在、分頁空」的白畫面。
 */
export function agentPlatformTabs(g: Gates): Tab[] {
  return [
    ...(g.agentBuilderEnabled && g.isAdmin ? (['agents'] as const) : []),
    ...(g.workflowDesignerEnabled && g.canManageWorkflow ? (['workflows', 'orchestrators'] as const) : []),
  ]
}

/**
 * Agent 平台工作區:Agent(D1,可重用單元)→ Workflow Designer(D4,拼圖)→ Orchestrators(D4,
 * 把已發布 Workflow 註冊成可執行的根)三個相依環節共用一個側欄入口 + 內層分頁。
 * 每個分頁各自守自己的 flag + capability(fail-closed 邊界,刻意不合併):
 * agents 要 agentBuilderEnabled && ADMIN;workflows/orchestrators 要 workflowDesignerEnabled
 * 且精確的 workflow.manage capability(ADMIN 角色本身永遠不蘊含它)。
 * 外層 .view 只在此處出現一次,子視圖不再自帶(避免雙重 padding 與巢狀捲動容器)。
 */
export default function AgentPlatformView(props: Props) {
  const { isAdmin, agentTestRunEnabled, multiAgentDispatchEnabled } = props
  const tabs = agentPlatformTabs(props)
  const [tab, setTab] = useState<Tab | null>(null)
  // 旗標是登入後非同步取得的:選中的分頁若已不在可見清單就退回第一個(fail-closed)。
  const active = tab && tabs.includes(tab) ? tab : tabs[0]

  return (
    <div className="view">
      <div className="view__head">
        <h2 className="view__title">Agent 平台</h2>
      </div>

      {/* 只有一個可見分頁時不生分頁列——沒有真正的選擇就不要多餘 UI。 */}
      {tabs.length > 1 && (
        <div className="seg config-tabs" role="group" aria-label="Agent 平台分頁">
          {tabs.map((t) => (
            <button
              key={t}
              type="button"
              className="btn"
              data-testid={`agent-platform-tab-${t}`}
              aria-pressed={active === t}
              onClick={() => setTab(t)}
            >
              {LABEL[t]}
            </button>
          ))}
        </div>
      )}

      {active === 'agents' && (
        <AgentsView isAdmin={isAdmin} agentTestRunEnabled={agentTestRunEnabled} />
      )}
      {active === 'workflows' && <WorkflowsView />}
      {active === 'orchestrators' && (
        <OrchestratorsView multiAgentDispatchEnabled={multiAgentDispatchEnabled} />
      )}
    </div>
  )
}
