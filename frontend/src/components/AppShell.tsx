import { lazy, Suspense, useEffect, useState } from 'react'
import { useCopilotReadable, useCopilotAction } from '@copilotkit/react-core'
import { CopilotSidebar } from '@copilotkit/react-ui'
import type { ChatOrchestrator, Session } from '../types'
import { useDocuments } from '../hooks/useDocuments'
import { invokeSkill } from '../api/skills'
import { getFeatures } from '../api/agents'
import { listChatOrchestrators } from '../api/chatOrchestrators'
import { ConfirmProvider } from './ConfirmDialog'
import { ToastProvider } from './Toast'
import ErrorBoundary from './ErrorBoundary'
import ChatView from './ChatView'
import DocumentsView from './DocumentsView'
import AnalysisView from './AnalysisView'
import ConfigView from './ConfigView'
import { agentPlatformTabs } from '../agentPlatformTabs'

const AgentPlatformView = lazy(() => import('./AgentPlatformView'))
const ApprovalInbox = lazy(() => import('./ApprovalInbox'))
const OperationsGovernanceView = lazy(() => import('./OperationsGovernanceView'))

type View = 'chat' | 'documents' | 'analysis' | 'config' | 'agentPlatform' | 'approvals' | 'operations'

// copilot 的 switchView 只認四個原視圖(不含 Agent 平台,副駕不涉入 Agent Builder)。
const VIEWS: View[] = ['chat', 'documents', 'analysis', 'config']

const NAV: { id: View; icon: string; label: string; adminOnly?: boolean }[] = [
  { id: 'chat', icon: '💬', label: '聊天' },
  { id: 'documents', icon: '📄', label: '文件' },
  { id: 'analysis', icon: '📊', label: '分析' },
  { id: 'config', icon: '🔧', label: '系統設定', adminOnly: true },
]

// Agent 平台入口:Agents(D1)/Workflow Designer(D4)/Orchestrators(D4)三個分頁的共用入口,
// 只要其中一個分頁的 flag + 權限成立就出現;個別分頁仍在 AgentPlatformView 各自 fail-closed。
const AGENT_PLATFORM_NAV = { id: 'agentPlatform' as const, icon: '🧑‍💼', label: 'Agent 平台' }
const APPROVALS_NAV = { id: 'approvals' as const, icon: '✅', label: 'Approvals' }
const OPERATIONS_NAV = { id: 'operations' as const, icon: '📈', label: 'Operations' }

interface Props {
  session: Session
  onLogout: () => void
  selectedOrchestratorId: string | null
  onSelectOrchestrator: (id: string | null) => void
}

/** 已登入外殼：左側欄導覽 + 頂欄身分 + 右主內容區（useState 切視圖，不用 router）。 */
export default function AppShell({
  session,
  onLogout,
  selectedOrchestratorId,
  onSelectOrchestrator,
}: Props) {
  const [view, setView] = useState<View>('chat')
  const [agentBuilderEnabled, setAgentBuilderEnabled] = useState(false)
  const [agentTestRunEnabled, setAgentTestRunEnabled] = useState(false)
  const [workflowDesignerEnabled, setWorkflowDesignerEnabled] = useState(false)
  const [multiAgentDispatchEnabled, setMultiAgentDispatchEnabled] = useState(false)
  const [agentChatEnabled, setAgentChatEnabled] = useState(false)
  const [agentWriteToolsEnabled, setAgentWriteToolsEnabled] = useState(false)
  const [chatOrchestrators, setChatOrchestrators] = useState<ChatOrchestrator[]>([])
  const isAdmin = session.role === 'ADMIN'
  // Capability comparison is exact: `workflow.manage.other` is never sufficient.
  const canManageWorkflow = (session.capabilities ?? []).includes('workflow.manage')
  const items = NAV.filter((n) => !n.adminOnly || isAdmin)
  // 入口與內層分頁共用同一份閘門判斷,避免「側欄有入口、進去卻是空分頁」的漂移。
  const showAgentPlatform =
    agentPlatformTabs({ isAdmin, agentBuilderEnabled, workflowDesignerEnabled, canManageWorkflow }).length > 0
  const navItems = [
    ...items,
    ...(showAgentPlatform ? [AGENT_PLATFORM_NAV] : []),
    ...(agentWriteToolsEnabled ? [APPROVALS_NAV] : []),
    ...(agentWriteToolsEnabled && canManageWorkflow ? [OPERATIONS_NAV] : []),
  ]

  // features flag:失敗或 false 一律 fail-closed(不顯示 Agents 入口)。登入即取一次。
  useEffect(() => {
    let cancelled = false
    getFeatures()
      .then((f) => {
        if (!cancelled) {
          setAgentBuilderEnabled(!!f.agentBuilderEnabled)
          setAgentTestRunEnabled(!!f.agentBuilderEnabled && !!f.agentTestRunEnabled)
          setWorkflowDesignerEnabled(!!f.workflowDesignerEnabled)
          setMultiAgentDispatchEnabled(!!f.multiAgentDispatchEnabled)
          setAgentChatEnabled(!!f.agentChatEnabled)
          setAgentWriteToolsEnabled(!!f.agentWriteToolsEnabled)
        }
      })
      .catch(() => {
        if (!cancelled) {
          setAgentBuilderEnabled(false)
          setAgentTestRunEnabled(false)
          setWorkflowDesignerEnabled(false)
          setMultiAgentDispatchEnabled(false)
          setAgentChatEnabled(false)
          setAgentWriteToolsEnabled(false)
        }
      })
    return () => {
      cancelled = true
    }
  }, [])

  // Global flag is only the first gate. A non-canary tenant receives 404 from the
  // authenticated catalog route, which keeps the UI exactly legacy.
  useEffect(() => {
    let cancelled = false
    if (!agentChatEnabled) {
      setChatOrchestrators([])
      onSelectOrchestrator(null)
      return
    }
    listChatOrchestrators()
      .then((items) => {
        if (!cancelled) setChatOrchestrators(items)
      })
      .catch(() => {
        if (!cancelled) {
          setChatOrchestrators([])
          onSelectOrchestrator(null)
        }
      })
    return () => {
      cancelled = true
    }
  }, [agentChatEnabled, onSelectOrchestrator])

  // useDocuments 提升到此層：AppShell 的 copilot action(建立/刪除)與 DocumentsView 共用
  // 同一份狀態,避免兩處各自實例化造成雙重輪詢(見契約)。DocumentsView 改吃 props。
  const documents = useDocuments()

  // 分頁標題隨視圖更新（沿用 NAV 的中文 label，不另建映射）。
  useEffect(() => {
    const label =
      view === 'agentPlatform' ? AGENT_PLATFORM_NAV.label
        : view === 'approvals' ? APPROVALS_NAV.label
          : view === 'operations' ? OPERATIONS_NAV.label
            : NAV.find((n) => n.id === view)?.label ?? ''
    document.title = `${label} — 資料分析平台`
  }, [view])

  // ---- 餵給副駕的畫面上下文(readable) ----
  useCopilotReadable({
    description: '目前登入的使用者身分（username / 角色 / 租戶代碼）',
    value: { username: session.username, role: session.role, tenantCode: session.tenantCode },
  })
  useCopilotReadable({
    description: '目前開啟的視圖（四選一：chat/documents/analysis/config）',
    value: view,
  })
  useCopilotReadable(
    {
      description: '目前租戶的文件清單（含處理狀態 processing/ready/failed 與片段數）',
      value: documents.docs.map((d) => ({
        id: d.id,
        title: d.title,
        status: d.status,
        chunk_count: d.chunk_count,
      })),
    },
    [documents.docs],
  )
  // 分析摘要的 readable 刻意掛在 AnalysisView 內(只有該視圖開著才餵),避免此層多一支常駐輪詢。

  // ---- 副駕可代為執行的動作(action;走既有 api 模組,自動帶 JWT,授權由 platform/backend 把關) ----
  useCopilotAction(
    {
      name: 'createDocument',
      description: '建立一份供 AI 檢索的文件。建立後會在背景非同步處理(chunking + 向量化)。',
      parameters: [
        { name: 'title', type: 'string', description: '文件標題', required: true },
        { name: 'text', type: 'string', description: '文件內文', required: true },
      ],
      handler: async ({ title, text }) => {
        // 沿用 useDocuments.create:202 → 樂觀插入 → 輪詢至就緒(不繞過)。
        await documents.create(title, text)
        return `已建立文件「${title}」,正在背景處理中,稍後會變為就緒。`
      },
    },
    [documents.create],
  )

  useCopilotAction(
    {
      name: 'askKnowledgeBase',
      description: '用租戶知識庫回答問題(RAG 檢索問答)。回傳答案文字。',
      parameters: [
        { name: 'question', type: 'string', description: '要問知識庫的問題', required: true },
      ],
      handler: async ({ question }) => {
        const res = await invokeSkill('rag-qa', { question })
        const answer = res.output?.answer
        return typeof answer === 'string' ? answer : JSON.stringify(res.output)
      },
    },
    [],
  )

  useCopilotAction(
    {
      name: 'switchView',
      description:
        '切換主畫面視圖。允許值:chat(聊天)、documents(文件)、analysis(分析)、config(系統設定,僅管理員)。',
      parameters: [
        { name: 'view', type: 'string', description: '目標視圖(見上述允許值)', required: true },
      ],
      handler: async ({ view: target }) => {
        if (!VIEWS.includes(target as View)) return `未知的視圖「${target}」。`
        if (target === 'config' && !isAdmin) return '沒有權限:系統設定僅限管理員。'
        setView(target as View)
        return `已切換到「${target}」視圖。`
      },
    },
    [isAdmin],
  )

  useCopilotAction(
    {
      name: 'deleteDocument',
      description: '刪除一份文件。刪除前一律在畫面上請使用者確認,經確認才真正刪除。',
      parameters: [
        { name: 'id', type: 'string', description: '要刪除的文件 id', required: true },
      ],
      // renderAndWaitForResponse = 協定內建 human-in-the-loop:先渲染確認 UI,
      // respond() 回傳結果給 LLM 後才結束。刪除動作只在使用者按下確認時發生。
      renderAndWaitForResponse: ({ args, respond, status }) => {
        const doc = documents.docs.find((d) => d.id === args.id)
        const title = doc?.title ?? args.id ?? '(未知文件)'
        if (status === 'complete') return <div className="copilot-confirm">刪除「{title}」已處理。</div>
        const acting = status === 'executing'
        return (
          <div className="copilot-confirm">
            <p>確認刪除文件「{title}」?此動作無法復原。</p>
            <button
              className="btn btn--danger"
              disabled={!acting}
              onClick={async () => {
                // 失敗也要 respond,否則 HITL 卡住、LLM 端永遠等不到結果。
                try {
                  if (args.id) await documents.remove(args.id)
                  respond?.('已刪除')
                } catch (e) {
                  respond?.(`刪除失敗:${(e as Error).message}`)
                }
              }}
            >
              確認刪除
            </button>
            <button className="btn" disabled={!acting} onClick={() => respond?.('使用者取消刪除')}>
              取消
            </button>
          </div>
        )
      },
    },
    [documents.docs, documents.remove],
  )

  return (
    <ToastProvider>
      <ConfirmProvider>
      <div className="shell">
        <aside className="shell__sidebar">
          <h1 className="shell__brand">資料分析平台</h1>
          <nav aria-label="主選單">
            {navItems.map((n) => (
              <button
                key={n.id}
                className={`shell__nav${view === n.id ? ' shell__nav--active' : ''}`}
                data-testid={`nav-${n.id}`}
                aria-current={view === n.id ? 'page' : undefined}
                onClick={() => setView(n.id)}
              >
                <span className="shell__nav-icon" aria-hidden="true">
                  {n.icon}
                </span>
                {n.label}
              </button>
            ))}
          </nav>
        </aside>

        <div className="shell__main">
          <header className="shell__topbar">
            <span className="shell__identity" data-testid="session-identity">
              {session.username} @ {session.tenantCode}
            </span>
            <span className={`badge badge--${isAdmin ? 'admin' : 'user'}`}>{session.role}</span>
            <button className="btn shell__logout" data-testid="logout-button" onClick={onLogout}>
              登出
            </button>
          </header>

          {/* key={view}：某視圖崩潰後切換到別的視圖即自動復原（重掛邊界）。 */}
          <main className="shell__content">
            <ErrorBoundary key={view}>
              <Suspense fallback={<div className="muted" role="status">Loading…</div>}>
              {view === 'chat' && (
                <ChatView
                  session={session}
                  orchestrators={chatOrchestrators}
                  selectedOrchestratorId={selectedOrchestratorId}
                  onSelectOrchestrator={onSelectOrchestrator}
                />
              )}
              {view === 'documents' && <DocumentsView documents={documents} />}
              {view === 'analysis' && <AnalysisView />}
              {view === 'config' && <ConfigView isAdmin={isAdmin} />}
              {view === 'agentPlatform' && showAgentPlatform && (
                <AgentPlatformView
                  isAdmin={isAdmin}
                  agentBuilderEnabled={agentBuilderEnabled}
                  agentTestRunEnabled={agentTestRunEnabled}
                  workflowDesignerEnabled={workflowDesignerEnabled}
                  canManageWorkflow={canManageWorkflow}
                  multiAgentDispatchEnabled={multiAgentDispatchEnabled}
                />
              )}
              {view === 'approvals' && agentWriteToolsEnabled && <ApprovalInbox />}
              {view === 'operations' && agentWriteToolsEnabled && canManageWorkflow && <OperationsGovernanceView />}
              </Suspense>
            </ErrorBoundary>
          </main>
        </div>

        {/* 全站 AI 副駕:浮動側欄(自帶開合鈕),不動既有五視圖版面。defaultOpen=false。 */}
        <div data-testid="copilot-sidebar">
          <CopilotSidebar
            defaultOpen={false}
            instructions={[
              '你是「資料分析平台」的操作助理,一律以繁體中文簡潔回答。',
              '',
              '平台操作手冊(使用者問「怎麼做」時照此說明步驟):',
              '- 文件:AI 檢索用的知識庫。新增:文件視圖 → 填標題 → 內容來源選「上傳檔案」(.txt/.md)或「貼上文字」→ 按「新增文件」。送出後狀態「處理中」,背景切塊與向量化完成後轉「就緒」,失敗則顯示「失敗」;清單可刪除文件。',
              '- 聊天:與 AI 對話(串流回覆),「新對話」會重開上下文。',
              '- 分析:查看統計摘要。',
              '- 系統設定:僅管理員(ADMIN)可見可改。「業務流程」管理 YAML 宣告式流程；「Agent Skills」管理 SKILL.md 套件並可上傳、編輯、下載。兩區都可試跑與查看版本；試跑會執行已存在的能力並可展開節點軌跡。',
              '- Agent 平台 › Agents:開著 Agent 編輯器時,你可以解釋每個欄位在問什麼、依使用者的描述幫忙填草稿(fillAgentDraft)、加一條商業規則(addAgentBusinessRule)。Skill/工具/fact/動作一律只能從畫面上的目錄挑,沒有的就說沒有。你只會改表單,「儲存草稿 → 驗證 → 發布」一律由使用者自己按,你不會也不能代為發布。',
              '文件依租戶隔離,使用者只看得到自己租戶的資料。',
              '',
              '你可代為執行的動作:createDocument(建文件)、deleteDocument(刪文件,務必先經使用者確認)、askKnowledgeBase(用知識庫回答問題)、switchView(切換視圖)。',
              '回答「怎麼做 X」時先給步驟,若該事能用動作代勞,主動提議由你執行。沒把握的功能明說不確定,不要編造。',
            ].join('\n')}
            labels={{
              title: 'AI 副駕',
              initial:
                '嗨,我是 AI 副駕,懂這個平台的操作,也能直接代勞。試試:\n・「文件功能怎麼用?」\n・「幫我把這段文字存成文件:…」\n・「用知識庫回答:…」\n・「切到分析頁」',
              placeholder: '輸入訊息…',
            }}
          />
        </div>
      </div>
      </ConfirmProvider>
    </ToastProvider>
  )
}
