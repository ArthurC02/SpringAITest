import { useCallback, useEffect, useState } from 'react'
import { streamChat } from '../api/chat'
import type { Message } from '../types'

const STORAGE_KEY = 'springai-chat:messages'

function loadMessages(): Message[] {
  try {
    const raw = localStorage.getItem(STORAGE_KEY)
    return raw ? (JSON.parse(raw) as Message[]) : []
  } catch {
    return []
  }
}

/**
 * 聊天狀態與行為的集中處：訊息清單、送出、清除，並把對話保存在 localStorage，
 * 重新整理頁面後仍在。元件只需呼叫 send()/clear() 並渲染 messages。
 */
export function useChat() {
  const [messages, setMessages] = useState<Message[]>(loadMessages)
  const [loading, setLoading] = useState(false)

  // 每次 messages 變動就寫回 localStorage（容量滿等情況靜默忽略）。
  useEffect(() => {
    try {
      localStorage.setItem(STORAGE_KEY, JSON.stringify(messages))
    } catch {
      /* ignore */
    }
  }, [messages])

  const send = useCallback(async (text: string) => {
    const trimmed = text.trim()
    if (!trimmed) return

    // 先放使用者訊息，再放一個空的 AI 佔位泡泡，串流的 token 會逐步填進這個泡泡。
    const assistantId = crypto.randomUUID()
    setMessages((m) => [
      ...m,
      { id: crypto.randomUUID(), role: 'user', content: trimmed },
      { id: assistantId, role: 'assistant', content: '' },
    ])
    setLoading(true)
    try {
      await streamChat(trimmed, (chunk) => {
        setMessages((m) =>
          m.map((msg) =>
            msg.id === assistantId
              ? { ...msg, content: msg.content + chunk }
              : msg,
          ),
        )
      })
    } catch (e) {
      // 串流失敗：把該佔位泡泡改成錯誤訊息。
      setMessages((m) =>
        m.map((msg) =>
          msg.id === assistantId
            ? { ...msg, content: (e as Error).message, error: true }
            : msg,
        ),
      )
    } finally {
      setLoading(false)
    }
  }, [])

  const clear = useCallback(() => setMessages([]), [])

  return { messages, loading, send, clear }
}
