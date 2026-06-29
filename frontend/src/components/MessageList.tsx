import { useEffect, useRef } from 'react'
import type { Message } from '../types'
import ChatBubble from './ChatBubble'

interface Props {
  messages: Message[]
  loading: boolean
}

export default function MessageList({ messages, loading }: Props) {
  const endRef = useRef<HTMLDivElement>(null)

  // 新訊息或進入 loading 時，自動捲到底。
  useEffect(() => {
    endRef.current?.scrollIntoView({ behavior: 'smooth', block: 'end' })
  }, [messages, loading])

  return (
    <div className="chat">
      {messages.length === 0 && (
        <div className="chat__empty">
          <p>開始跟 Spring AI 對話吧 👋</p>
          <p className="chat__hint">
            送出後會呼叫後端 <code>POST /api/chat</code>（經 Vite proxy 轉到 :8080）。
          </p>
        </div>
      )}

      {messages.map((m) => (
        <ChatBubble key={m.id} message={m} />
      ))}

      <div ref={endRef} />
    </div>
  )
}
