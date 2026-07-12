import { useEffect, useRef, useState, type UIEvent } from 'react'
import type { Message } from '../types'
import ChatBubble from './ChatBubble'

interface Props {
  messages: Message[]
  loading: boolean
}

const NEAR_BOTTOM_PX = 100
const prefersReducedMotion = () =>
  window.matchMedia('(prefers-reduced-motion: reduce)').matches

/**
 * 三態捲動（ChatGPT/Claude.ai 模式）：貼底才自動捲、使用者上捲即停、
 * 非貼底時顯示「跳至最新」膠囊。距底 < 100px 視為貼底。
 */
export default function MessageList({ messages, loading }: Props) {
  const endRef = useRef<HTMLDivElement>(null)
  const [atBottom, setAtBottom] = useState(true)
  const userCountRef = useRef(0)

  // 串流追加一律瞬移（'auto'）：smooth 動畫追不上 scrollHeight 成長時,
  // 自己觸發的 onScroll 會取樣到「距底 >100px」而誤判使用者上捲,永久關掉自動跟隨。
  // smooth 只保留給使用者主動的 jump()。
  function scrollToBottom(behavior: ScrollBehavior = 'auto') {
    endRef.current?.scrollIntoView({ behavior, block: 'end' })
  }

  // 貼底才自動捲；上捲後 atBottom 為 false 便不再打擾。
  // 例外：使用者送出新訊息時強制回底（上捲閱讀中按送出,自己的訊息應被捲入視野）。
  useEffect(() => {
    const userCount = messages.filter((m) => m.role === 'user').length
    const sentNew = userCount > userCountRef.current
    userCountRef.current = userCount
    if (sentNew) {
      setAtBottom(true)
      scrollToBottom()
      return
    }
    if (atBottom) scrollToBottom()
  }, [messages, loading, atBottom])

  function onScroll(e: UIEvent<HTMLDivElement>) {
    const el = e.currentTarget
    const dist = el.scrollHeight - el.scrollTop - el.clientHeight
    setAtBottom(dist < NEAR_BOTTOM_PX)
  }

  function jump() {
    setAtBottom(true)
    scrollToBottom(prefersReducedMotion() ? 'auto' : 'smooth')
  }

  return (
    <div className="chat-wrap">
      <div className="chat" onScroll={onScroll}>
        {messages.length === 0 && (
          <div className="chat__empty">
            <p>開始對話吧 👋</p>
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

      {!atBottom && (
        <button className="chat-jump" onClick={jump} aria-label="跳至最新訊息">
          <span aria-hidden="true">↓</span> 跳至最新
        </button>
      )}
    </div>
  )
}
