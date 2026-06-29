import { useChat } from './hooks/useChat'
import MessageList from './components/MessageList'
import Composer from './components/Composer'
import './App.css'

export default function App() {
  const { messages, loading, send, clear } = useChat()

  return (
    <div className="app">
      <header className="app__header">
        <h1>Spring AI Chat</h1>
        <span className="app__badge">React + Vite · /api/chat</span>
        <button
          className="app__clear"
          onClick={clear}
          disabled={messages.length === 0}
          title="清除對話"
        >
          清除
        </button>
      </header>

      <MessageList messages={messages} loading={loading} />
      <Composer disabled={loading} onSend={send} />
    </div>
  )
}
