import { useCallback, useEffect, useRef, useState } from 'react'
import { getChatHistoryPage, streamChat, newConversation } from '../api/chat'
import {
  getChatPersistenceGeneration,
  loadPersistedChatMessages,
  persistChatMessages,
} from '../chatPersistence'
import { createChatChunkBatcher, type ChatChunkBatcher } from '../chatChunkBatcher'
import { runGuardedChatRequest } from '../chatRequestLifecycle'
import { mergeInitialMessages, mergeOlderMessages } from '../chatHistory'
import type { Message, Session } from '../types'

interface ActiveRequest {
  controller: AbortController
  assistantId: string
  batcher: ChatChunkBatcher
}

function loadMessages(): Message[] {
  return loadPersistedChatMessages()
}

function removeEmptyAssistantPlaceholder(messages: Message[], assistantId: string): Message[] {
  const message = messages.find((item) => item.id === assistantId)
  return message?.content === ''
    ? messages.filter((item) => item.id !== assistantId)
    : messages
}

/** Owns chat UI state, SSE request lifetime, and debounced local persistence. */
export function useChat(
  orchestratorId: string | null = null,
  session: Session | null = null,
) {
  const [messages, setMessages] = useState<Message[]>(loadMessages)
  const [loading, setLoading] = useState(false)
  const [persistenceWarning, setPersistenceWarning] = useState<string | null>(null)
  const persistenceGenerationRef = useRef(getChatPersistenceGeneration())
  const activeRequestRef = useRef<ActiveRequest | null>(null)
  const messagesRef = useRef(messages)
  messagesRef.current = messages
  const timerRef = useRef<ReturnType<typeof setTimeout> | undefined>(undefined)
  const quotaSignaledRef = useRef(false)
  const terminalPersistedMessagesRef = useRef<Message[] | null>(null)
  const historyRequestRef = useRef<AbortController | null>(null)
  const historyGenerationRef = useRef(0)
  const historyBusyRef = useRef(false)
  const nextCursorRef = useRef<string | null>(null)
  const [historyLoadingKind, setHistoryLoadingKind] = useState<'initial' | 'older' | null>(null)
  const [historyError, setHistoryError] = useState<string | null>(null)
  const [hasMoreHistory, setHasMoreHistory] = useState(false)

  const commitMessages = useCallback((update: (current: Message[]) => Message[]) => {
    const next = update(messagesRef.current)
    messagesRef.current = next
    setMessages(next)
  }, [])

  const persist = useCallback((showWarning: boolean) => {
    const result = persistChatMessages(messagesRef.current, persistenceGenerationRef.current)
    if (result === 'quota_failed' && showWarning && !quotaSignaledRef.current) {
      quotaSignaledRef.current = true
      setPersistenceWarning('聊天記錄暫時無法儲存在此瀏覽器')
    }
  }, [])

  const abortActiveRequest = useCallback((removeEmptyPlaceholder: boolean) => {
    const activeRequest = activeRequestRef.current
    if (!activeRequest) return

    activeRequest.batcher.flush()
    activeRequestRef.current = null
    activeRequest.controller.abort()
    if (removeEmptyPlaceholder) {
      commitMessages((current) => removeEmptyAssistantPlaceholder(current, activeRequest.assistantId))
    }
  }, [commitMessages])

  const abortActiveRequestOnUnmount = useCallback(() => {
    const activeRequest = activeRequestRef.current
    if (!activeRequest) return

    activeRequest.batcher.flush()
    activeRequestRef.current = null
    activeRequest.controller.abort()
    // The cleanup flush must not persist an empty placeholder from the cancelled request.
    messagesRef.current = removeEmptyAssistantPlaceholder(
      messagesRef.current,
      activeRequest.assistantId,
    )
  }, [])

  const cancelHistoryRequest = useCallback(() => {
    historyGenerationRef.current += 1
    historyRequestRef.current?.abort()
    historyRequestRef.current = null
    historyBusyRef.current = false
    setHistoryLoadingKind(null)
  }, [])

  const sessionKey = session
    ? `${session.tenantCode}\0${session.username}\0${session.token}`
    : null

  const requestHistoryPage = useCallback(async (before?: string) => {
    if (!sessionKey || historyBusyRef.current) return

    const generation = ++historyGenerationRef.current
    const controller = new AbortController()
    historyRequestRef.current?.abort()
    historyRequestRef.current = controller
    historyBusyRef.current = true
    setHistoryLoadingKind(before === undefined ? 'initial' : 'older')
    setHistoryError(null)
    try {
      const page = await getChatHistoryPage(before, controller.signal)
      if (controller.signal.aborted || historyGenerationRef.current !== generation) return
      commitMessages((current) => before === undefined
        ? mergeInitialMessages(current, page.items, page.hasMore)
        : mergeOlderMessages(current, page.items))
      nextCursorRef.current = page.nextCursor
      setHasMoreHistory(page.hasMore)
    } catch (error) {
      if (controller.signal.aborted || historyGenerationRef.current !== generation) return
      setHistoryError(error instanceof Error ? error.message : '載入聊天記錄失敗')
    } finally {
      if (historyGenerationRef.current === generation) {
        historyRequestRef.current = null
        historyBusyRef.current = false
        setHistoryLoadingKind(null)
      }
    }
  }, [commitMessages, sessionKey])

  useEffect(() => {
    cancelHistoryRequest()
    nextCursorRef.current = null
    setHasMoreHistory(false)
    setHistoryError(null)
    if (sessionKey) void requestHistoryPage()
    return cancelHistoryRequest
  }, [cancelHistoryRequest, requestHistoryPage, sessionKey])

  useEffect(() => {
    clearTimeout(timerRef.current)
    if (terminalPersistedMessagesRef.current === messages) {
      terminalPersistedMessagesRef.current = null
      timerRef.current = undefined
      return
    }
    terminalPersistedMessagesRef.current = null
    timerRef.current = setTimeout(
      () => persist(true),
      500,
    )
  }, [messages, persist])

  useEffect(() => {
    const flush = () => persist(false)
    window.addEventListener('beforeunload', flush)
    return () => {
      abortActiveRequestOnUnmount()
      clearTimeout(timerRef.current)
      flush()
      window.removeEventListener('beforeunload', flush)
    }
  }, [abortActiveRequestOnUnmount, persist])

  const send = useCallback(async (text: string) => {
    const trimmed = text.trim()
    if (!trimmed) return

    // Defensive replacement support for callers that race the composer's disabled state.
    abortActiveRequest(true)

    const assistantId = crypto.randomUUID()
    commitMessages((current) => [
      ...current,
      { id: crypto.randomUUID(), role: 'user', content: trimmed },
      { id: assistantId, role: 'assistant', content: '' },
    ])
    setLoading(true)

    let request!: ActiveRequest
    const batcher = createChatChunkBatcher(
      () => activeRequestRef.current === request,
      (content) => {
        commitMessages((current) =>
          current.map((message) =>
            message.id === assistantId
              ? { ...message, content: message.content + content }
              : message,
          ),
        )
      },
    )
    request = { controller: new AbortController(), assistantId, batcher }
    activeRequestRef.current = request
    await runGuardedChatRequest(
      request,
      () => activeRequestRef.current === request,
      (onToken) => streamChat(
        trimmed,
        onToken,
        request.controller.signal,
        orchestratorId,
      ),
      {
        onToken: (chunk) => request.batcher.append(chunk),
        onAbort: () => {
          request.batcher.flush()
          commitMessages((current) => removeEmptyAssistantPlaceholder(current, assistantId))
        },
        onError: (error) => {
          request.batcher.flush()
          commitMessages((current) => {
            const partial = current.find((item) => item.id === assistantId)
            if (partial?.content) {
              return [
                ...current,
                {
                  id: crypto.randomUUID(),
                  role: 'assistant' as const,
                  content: error.message,
                  error: true,
                },
              ]
            }
            return current.map((message) =>
              message.id === assistantId
                ? { ...message, content: error.message, error: true }
                : message,
            )
          })
        },
        onFinish: () => {
          request.batcher.flush()
          clearTimeout(timerRef.current)
          timerRef.current = undefined
          terminalPersistedMessagesRef.current = messagesRef.current
          persist(true)
          setLoading(false)
          activeRequestRef.current = null
        },
      },
    )
  }, [abortActiveRequest, commitMessages, orchestratorId, persist])

  const stop = useCallback(() => {
    activeRequestRef.current?.controller.abort()
  }, [])

  const clear = useCallback(() => {
    abortActiveRequest(false)
    cancelHistoryRequest()
    nextCursorRef.current = null
    setHasMoreHistory(false)
    setHistoryError(null)
    newConversation()
    commitMessages(() => [])
    setLoading(false)
  }, [abortActiveRequest, cancelHistoryRequest, commitMessages])

  const loadOlder = useCallback(() => {
    const cursor = nextCursorRef.current
    if (cursor) void requestHistoryPage(cursor)
  }, [requestHistoryPage])

  const retryHistory = useCallback(() => {
    void requestHistoryPage(nextCursorRef.current ?? undefined)
  }, [requestHistoryPage])

  return {
    messages,
    loading,
    persistenceWarning,
    historyLoading: historyLoadingKind !== null,
    loadingOlderHistory: historyLoadingKind === 'older',
    historyError,
    hasMoreHistory,
    send,
    stop,
    clear,
    loadOlder,
    retryHistory,
  }
}
