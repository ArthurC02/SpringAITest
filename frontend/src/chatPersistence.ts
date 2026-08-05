import { CHAT_MESSAGES_KEY } from './storageKeys'
import type { Message } from './types'

let activeGeneration = 0
export const MAX_PERSISTED_CHAT_MESSAGES = 100
export const MAX_PERSISTED_CHAT_BYTES = 1024 * 1024

export type ChatPersistenceResult = 'written' | 'stale' | 'quota_failed'

function jsonBytes(value: unknown): number {
  return new TextEncoder().encode(JSON.stringify(value)).byteLength
}

function rawBytes(value: string): number {
  return new TextEncoder().encode(value).byteLength
}

function isMessage(value: unknown): value is Message {
  if (!value || typeof value !== 'object') return false
  const candidate = value as Partial<Message>
  return typeof candidate.id === 'string'
    && (candidate.role === 'user' || candidate.role === 'assistant')
    && typeof candidate.content === 'string'
    && (candidate.error === undefined || typeof candidate.error === 'boolean')
}

function discardInvalidCache(): void {
  try {
    localStorage.removeItem(CHAT_MESSAGES_KEY)
  } catch {
    // A denied storage implementation needs no second signal during initial load.
  }
}

/** Reject oversized or malformed raw cache before it can become visible application state. */
export function loadPersistedChatMessages(): Message[] {
  let raw: string | null
  try {
    raw = localStorage.getItem(CHAT_MESSAGES_KEY)
  } catch {
    return []
  }
  if (!raw) return []
  if (rawBytes(raw) > MAX_PERSISTED_CHAT_BYTES) {
    discardInvalidCache()
    return []
  }

  try {
    const parsed: unknown = JSON.parse(raw)
    if (!Array.isArray(parsed) || !parsed.every(isMessage)) {
      discardInvalidCache()
      return []
    }
    return capPersistedChatMessages(parsed)
  } catch {
    discardInvalidCache()
    return []
  }
}

/** Persist only the newest suffix; this never mutates the visible in-memory conversation. */
export function capPersistedChatMessages(messages: Message[]): Message[] {
  const newestFirst: Message[] = []
  let bytes = 2 // JSON array brackets
  for (let index = messages.length - 1; index >= 0 && newestFirst.length < MAX_PERSISTED_CHAT_MESSAGES; index -= 1) {
    const messageBytes = jsonBytes(messages[index])
    const separatorBytes = newestFirst.length === 0 ? 0 : 1
    if (bytes + separatorBytes + messageBytes > MAX_PERSISTED_CHAT_BYTES) break
    newestFirst.push(messages[index])
    bytes += separatorBytes + messageBytes
  }
  return newestFirst.reverse()
}

/** 每個 useChat 實例取得自己的寫入世代；登出後舊世代的延遲 flush 一律失效。 */
export function getChatPersistenceGeneration(): number {
  return activeGeneration
}

/**
 * 在清除聊天 storage 前撤銷所有既有寫入資格，避免 debounce/unmount/beforeunload
 * 於登出後把前一位使用者的訊息寫回。
 */
export function invalidateChatPersistence(): void {
  activeGeneration += 1
}

export function persistChatMessages(messages: Message[], generation: number): ChatPersistenceResult {
  if (generation !== activeGeneration) return 'stale'

  try {
    localStorage.setItem(CHAT_MESSAGES_KEY, JSON.stringify(capPersistedChatMessages(messages)))
    return 'written'
  } catch {
    // Deliberately return only a content-free signal; callers must not log message data.
    return 'quota_failed'
  }
}
