import { useChat } from '../hooks/useChat'
import MessageList from './MessageList'
import Composer from './Composer'
import type { ChatOrchestrator, Session } from '../types'

/** 聊天視圖：直接沿用既有 useChat + MessageList + Composer（SSE 契約未動）。 */
interface Props {
  session: Session | null
  orchestrators?: ChatOrchestrator[]
  selectedOrchestratorId?: string | null
  onSelectOrchestrator?: (id: string | null) => void
}

export default function ChatView({
  session,
  orchestrators = [],
  selectedOrchestratorId = null,
  onSelectOrchestrator,
}: Props) {
  const {
    messages,
    loading,
    persistenceWarning,
    historyLoading,
    loadingOlderHistory,
    historyError,
    hasMoreHistory,
    send,
    stop,
    clear,
    loadOlder,
    retryHistory,
  } = useChat(selectedOrchestratorId, session)
  return (
    <div className="chatview">
      <div className="chatview__bar">
        <button className="btn" onClick={clear} disabled={messages.length === 0} title="清空目前對話並開一段新對話（重置上下文）">
          ＋ 新對話
        </button>
        {orchestrators.length > 0 && onSelectOrchestrator && (
          <label className="field">
            <span>協作模式</span>
            <select
              aria-label="選擇協作 Orchestrator"
              value={selectedOrchestratorId ?? ''}
              disabled={loading}
              onChange={(event) => onSelectOrchestrator(event.target.value || null)}
            >
              <option value="">預設協作模式</option>
              {orchestrators.map((orchestrator) => (
                <option key={orchestrator.id} value={orchestrator.id}>
                  {orchestrator.name}
                </option>
              ))}
            </select>
          </label>
        )}
      </div>
      <div className="chat-history-controls">
        {historyLoading && !loadingOlderHistory && (
          <span className="muted" role="status">正在載入聊天記錄…</span>
        )}
        {historyError && (
          <>
            <span className="error-text" role="alert">{historyError}</span>
            <button className="btn" onClick={retryHistory} disabled={historyLoading}>
              重試載入聊天記錄
            </button>
          </>
        )}
        {!historyError && hasMoreHistory && (
          <button className="btn" onClick={loadOlder} disabled={historyLoading}>
            {loadingOlderHistory ? '載入中…' : '載入更早'}
          </button>
        )}
      </div>
      <MessageList messages={messages} loading={loading} />
      {persistenceWarning && <p className="muted" role="status">{persistenceWarning}</p>}
      <Composer streaming={loading} onSend={send} onStop={stop} />
    </div>
  )
}
