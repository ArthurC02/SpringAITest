import { useChat } from '../hooks/useChat'
import MessageList from './MessageList'
import Composer from './Composer'

/** 聊天視圖：直接沿用既有 useChat + MessageList + Composer（SSE 契約未動）。 */
export default function ChatView() {
  const { messages, loading, send, stop, clear } = useChat()
  return (
    <div className="chatview">
      <div className="chatview__bar">
        <button className="btn" onClick={clear} disabled={messages.length === 0} title="清除對話">
          清除對話
        </button>
      </div>
      <MessageList messages={messages} loading={loading} />
      <Composer streaming={loading} onSend={send} onStop={stop} />
    </div>
  )
}
