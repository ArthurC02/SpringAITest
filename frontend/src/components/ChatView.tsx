import { useChat } from '../hooks/useChat'
import MessageList from './MessageList'
import Composer from './Composer'
import type { ChatOrchestrator } from '../types'

/** 聊天視圖：直接沿用既有 useChat + MessageList + Composer（SSE 契約未動）。 */
interface Props {
  orchestrators?: ChatOrchestrator[]
  selectedOrchestratorId?: string | null
  onSelectOrchestrator?: (id: string | null) => void
}

export default function ChatView({
  orchestrators = [],
  selectedOrchestratorId = null,
  onSelectOrchestrator,
}: Props) {
  const { messages, loading, send, stop, clear } = useChat(selectedOrchestratorId)
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
      <MessageList messages={messages} loading={loading} />
      <Composer streaming={loading} onSend={send} onStop={stop} />
    </div>
  )
}
