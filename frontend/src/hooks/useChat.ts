import { useCallback, useEffect, useRef, useState } from 'react'
import { streamChat, newConversation } from '../api/chat'
import { getChatPersistenceGeneration, persistChatMessages } from '../chatPersistence'
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
export function useChat(orchestratorId: string | null = null) {
  const [messages, setMessages] = useState<Message[]>(loadMessages)
  const [loading, setLoading] = useState(false)
  // 登出會使此實例的世代失效，杜絕任何較晚發生的 lifecycle flush 回寫舊訊息。
  const persistenceGenerationRef = useRef(getChatPersistenceGeneration())
  // 串流中的 AbortController，供「停止產生」中止 fetch 用。
  const abortRef = useRef<AbortController | null>(null)
  // 最新 messages 的鏡像，供 debounce timer 與卸載時的 flush 讀取。
  const messagesRef = useRef(messages)
  messagesRef.current = messages
  const timerRef = useRef<ReturnType<typeof setTimeout> | undefined>(undefined)

  // 串流時逐 token 寫 localStorage 太頻繁；改為 500ms debounce，只在停止變動後才落地。
  // 錯誤/中止的最終狀態同樣是「一次 setMessages 後停止變動」，會經此持久化。
  useEffect(() => {
    clearTimeout(timerRef.current)
    timerRef.current = setTimeout(
      () => persistChatMessages(messagesRef.current, persistenceGenerationRef.current),
      500,
    )
  }, [messages])

  // 卸載或關閉分頁時把最新狀態立即 flush，涵蓋 debounce 視窗內離開而尚未落地的情況。
  useEffect(() => {
    const flush = () =>
      persistChatMessages(messagesRef.current, persistenceGenerationRef.current)
    window.addEventListener('beforeunload', flush)
    return () => {
      clearTimeout(timerRef.current)
      flush()
      window.removeEventListener('beforeunload', flush)
    }
  }, [])

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
        orchestratorId,
      )
    } catch (e) {
      if ((e as Error).name === 'AbortError') {
        // 使用者中止：保留已收到的 token；若一個字都還沒收到就移除空佔位泡泡。
        setMessages((m) => {
          const msg = m.find((x) => x.id === assistantId)
          return msg && msg.content === '' ? m.filter((x) => x.id !== assistantId) : m
        })
      } else {
        // 串流失敗：已收到的部分內容保留（platform 契約是 token 在前、error frame 在後），
        // 另附一顆錯誤泡泡；一個字都沒收到才把空佔位泡泡改成錯誤訊息。
        setMessages((m) => {
          const partial = m.find((x) => x.id === assistantId)
          if (partial && partial.content !== '') {
            return [
              ...m,
              { id: crypto.randomUUID(), role: 'assistant' as const, content: (e as Error).message, error: true },
            ]
          }
          return m.map((msg) =>
            msg.id === assistantId
              ? { ...msg, content: (e as Error).message, error: true }
              : msg,
          )
        })
      }
    } finally {
      setLoading(false)
      abortRef.current = null
    }
  }, [orchestratorId])

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
