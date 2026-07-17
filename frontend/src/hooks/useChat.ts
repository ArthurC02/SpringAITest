import { useCallback, useEffect, useRef, useState } from 'react'
import { streamChat, newConversation } from '../api/chat'
import { CHAT_MESSAGES_KEY } from '../storageKeys'
import type { Message } from '../types'

function loadMessages(): Message[] {
  try {
    const raw = localStorage.getItem(CHAT_MESSAGES_KEY)
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
  // 串流中的 AbortController，供「停止產生」中止 fetch 用。
  const abortRef = useRef<AbortController | null>(null)

  // 每次 messages 變動就寫回 localStorage（容量滿等情況靜默忽略）。
  useEffect(() => {
    try {
      localStorage.setItem(CHAT_MESSAGES_KEY, JSON.stringify(messages))
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
    const controller = new AbortController()
    abortRef.current = controller
    try {
      await streamChat(
        trimmed,
        (chunk) => {
          setMessages((m) =>
            m.map((msg) =>
              msg.id === assistantId
                ? { ...msg, content: msg.content + chunk }
                : msg,
            ),
          )
        },
        controller.signal,
      )
    } catch (e) {
      if ((e as Error).name === 'AbortError') {
        // 使用者中止：保留已收到的 token；若一個字都還沒收到就移除空佔位泡泡。
        setMessages((m) => {
          const msg = m.find((x) => x.id === assistantId)
          return msg && msg.content === '' ? m.filter((x) => x.id !== assistantId) : m
        })
      } else {
        // 串流失敗：把該佔位泡泡改成錯誤訊息。
        setMessages((m) =>
          m.map((msg) =>
            msg.id === assistantId
              ? { ...msg, content: (e as Error).message, error: true }
              : msg,
          ),
        )
      }
    } finally {
      setLoading(false)
      abortRef.current = null
    }
  }, [])

  // 停止產生：中止進行中的串流（AbortError 由 send 的 catch 當作正常中止處理）。
  const stop = useCallback(() => {
    abortRef.current?.abort()
  }, [])

  // 清除對話：同時換新 conversationId，讓後端短期記憶也一起重置。
  const clear = useCallback(() => {
    newConversation()
    setMessages([])
  }, [])

  return { messages, loading, send, stop, clear }
}
