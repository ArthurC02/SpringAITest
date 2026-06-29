import type { Message } from '../types'
import Markdown from './Markdown'

export default function ChatBubble({ message }: { message: Message }) {
  const isUser = message.role === 'user'
  // 使用者輸入與錯誤訊息以純文字呈現；AI 正常回覆才走 Markdown 渲染。
  const plain = isUser || message.error
  // AI 佔位泡泡（尚無任何 token）顯示打字中動畫。
  const waiting = !isUser && !message.error && message.content === ''
  return (
    <div
      className={`bubble bubble--${message.role}${message.error ? ' bubble--error' : ''}`}
    >
      <div className="bubble__role">{isUser ? '你' : 'AI'}</div>
      <div className={`bubble__content${waiting ? ' bubble__typing' : ''}`}>
        {waiting ? (
          <>
            <span />
            <span />
            <span />
          </>
        ) : plain ? (
          message.content
        ) : (
          <Markdown>{message.content}</Markdown>
        )}
      </div>
    </div>
  )
}
