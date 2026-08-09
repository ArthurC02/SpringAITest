import { memo } from 'react'
import type { Message } from '../types'
import Markdown from './Markdown'
import { skillSourceLabel, stripSkillSentinel } from '../skillSentinel'

function ChatBubble({ message }: { message: Message }) {
  const isUser = message.role === 'user'
  // 使用者輸入與錯誤訊息以純文字呈現；AI 正常回覆才走 Markdown 渲染。
  const plain = isUser || message.error
  // AI 佔位泡泡（尚無任何 token）顯示打字中動畫。
  const waiting = !isUser && !message.error && message.content === ''
  // 只有 AI 正常回覆才可能帶 skill 來源 sentinel；剝離失敗（無標記/畸形/中段）
  // 一律 fail open，content 原樣不動。
  const { content, skillName } = !plain && !waiting
    ? stripSkillSentinel(message.content)
    : { content: message.content, skillName: null }
  return (
    <div
      className={`bubble bubble--${message.role}${message.error ? ' bubble--error' : ''}`}
    >
      <div className="bubble__role">{isUser ? '你' : 'AI'}</div>
      {skillName && <div className="bubble__source">{skillSourceLabel(skillName)}</div>}
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
          <Markdown>{content}</Markdown>
        )}
      </div>
    </div>
  )
}

// 串流時 messages 每 token 重建陣列，但未變的舊泡泡由 useChat 的 map 回傳同一參考，
// memo 得以跳過其重渲染（只重繪正在串流的那顆）。
export default memo(ChatBubble)
