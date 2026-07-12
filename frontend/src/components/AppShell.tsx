import { useState } from 'react'
import { useCopilotReadable, useCopilotAction } from '@copilotkit/react-core'
import { CopilotSidebar } from '@copilotkit/react-ui'
import type { Session } from '../types'
import { useDocuments } from '../hooks/useDocuments'
import { invokeWorkflow } from '../api/workflows'
import ChatView from './ChatView'
import DocumentsView from './DocumentsView'
import WorkflowsView from './WorkflowsView'
import AnalysisView from './AnalysisView'
import ConfigView from './ConfigView'

type View = 'chat' | 'documents' | 'workflows' | 'analysis' | 'config'

const VIEWS: View[] = ['chat', 'documents', 'workflows', 'analysis', 'config']

const NAV: { id: View; icon: string; label: string; adminOnly?: boolean }[] = [
  { id: 'chat', icon: '💬', label: '聊天' },
  { id: 'documents', icon: '📄', label: '文件' },
  { id: 'workflows', icon: '⚙', label: '工作流' },
  { id: 'analysis', icon: '📊', label: '分析' },
  { id: 'config', icon: '🔧', label: '系統設定', adminOnly: true },
]

interface Props {
  session: Session
  onLogout: () => void
}

/** 已登入外殼：左側欄導覽 + 頂欄身分 + 右主內容區（useState 切視圖，不用 router）。 */
export default function AppShell({ session, onLogout }: Props) {
  const [view, setView] = useState<View>('chat')
  const isAdmin = session.role === 'ADMIN'
  const items = NAV.filter((n) => !n.adminOnly || isAdmin)

  // useDocuments 提升到此層：AppShell 的 copilot action(建立/刪除)與 DocumentsView 共用
  // 同一份狀態,避免兩處各自實例化造成雙重輪詢(見契約)。DocumentsView 改吃 props。
  const documents = useDocuments()

  // ---- 餵給副駕的畫面上下文(readable) ----
  useCopilotReadable({
    description: '目前登入的使用者身分（username / 角色 / 租戶代碼）',
    value: { username: session.username, role: session.role, tenantCode: session.tenantCode },
  })
  useCopilotReadable({
    description: '目前開啟的視圖（五選一：chat/documents/workflows/analysis/config）',
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
        const res = await invokeWorkflow('rag_qa', { question })
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
        '切換主畫面視圖。允許值:chat(聊天)、documents(文件)、workflows(工作流)、analysis(分析)、config(系統設定,僅管理員)。',
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
    <div className="shell">
      <aside className="shell__sidebar">
        <div className="shell__brand">Spring AI</div>
        <nav>
          {items.map((n) => (
            <button
              key={n.id}
              className={`shell__nav${view === n.id ? ' shell__nav--active' : ''}`}
              onClick={() => setView(n.id)}
            >
              <span className="shell__nav-icon">{n.icon}</span>
              {n.label}
            </button>
          ))}
        </nav>
      </aside>

      <div className="shell__main">
        <header className="shell__topbar">
          <span className="shell__identity">
            {session.username} @ {session.tenantCode}
          </span>
          <span className={`badge badge--${isAdmin ? 'admin' : 'user'}`}>{session.role}</span>
          <button className="btn shell__logout" onClick={onLogout}>
            登出
          </button>
        </header>

        <div className="shell__content">
          {view === 'chat' && <ChatView />}
          {view === 'documents' && <DocumentsView documents={documents} />}
          {view === 'workflows' && <WorkflowsView />}
          {view === 'analysis' && <AnalysisView />}
          {view === 'config' && <ConfigView isAdmin={isAdmin} />}
        </div>
      </div>

      {/* 全站 AI 副駕:浮動側欄(自帶開合鈕),不動既有五視圖版面。defaultOpen=false。 */}
      <CopilotSidebar
        defaultOpen={false}
        instructions="你是這個 AI 資料平台的操作助理。可讀取畫面上下文,並用提供的動作代使用者建立/刪除文件、查詢知識庫、切換視圖。刪除文件務必先讓使用者確認。"
        labels={{
          title: 'AI 副駕',
          initial: '嗨,我是 AI 副駕。可以幫你建立/刪除文件、查詢知識庫或切換視圖。',
          placeholder: '輸入訊息…',
        }}
      />
    </div>
  )
}
