import type { Message } from './types'

export const CHAT_HISTORY_PAGE_SIZE = 50

export interface ChatHistoryItem {
  id: number
  reply: string
  createdAt: string
}

export interface ChatHistoryPage {
  items: ChatHistoryItem[]
  nextCursor: string | null
  hasMore: boolean
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return !!value && typeof value === 'object' && !Array.isArray(value)
}

/** Strictly validate the public page contract before it enters React state. */
export function parseChatHistoryPage(
  value: unknown,
  pageSize = CHAT_HISTORY_PAGE_SIZE,
): ChatHistoryPage {
  if (!isRecord(value) || !Array.isArray(value.items) || typeof value.hasMore !== 'boolean') {
    throw new Error('聊天記錄回應格式不正確')
  }
  if (value.items.length > pageSize) {
    throw new Error('聊天記錄回應超過頁面上限')
  }
  const items = value.items.map((item) => {
    if (!isRecord(item)
      || typeof item.id !== 'number'
      || !Number.isSafeInteger(item.id)
      || item.id <= 0
      || typeof item.reply !== 'string'
      || typeof item.createdAt !== 'string'
      || !Number.isFinite(Date.parse(item.createdAt))) {
      throw new Error('聊天記錄回應格式不正確')
    }
    return { id: item.id, reply: item.reply, createdAt: item.createdAt }
  })
  const nextCursor = value.nextCursor
  if (value.hasMore) {
    if (typeof nextCursor !== 'string' || nextCursor.length === 0) {
      throw new Error('聊天記錄回應格式不正確')
    }
  } else if (nextCursor !== null) {
    throw new Error('聊天記錄回應格式不正確')
  }
  return { items, nextCursor: nextCursor as string | null, hasMore: value.hasMore }
}

/** Backend pages are newest-first; the chat UI is oldest-first. */
export function historyItemsToMessages(items: ChatHistoryItem[]): Message[] {
  const seen = new Set<number>()
  return [...items].reverse().filter((item) => {
    if (seen.has(item.id)) return false
    seen.add(item.id)
    return true
  }).map((item) => ({
    id: `server:${item.id}`,
    role: 'assistant',
    content: item.reply,
  }))
}

function serverMessageId(message: Message): number | null {
  if (!message.id.startsWith('server:')) return null
  const id = Number(message.id.slice('server:'.length))
  return Number.isSafeInteger(id) && id > 0 ? id : null
}

/** Merge the authoritative newest page with an optional older persisted server cache. */
export function mergeInitialMessages(
  current: Message[],
  items: ChatHistoryItem[],
  hasMore: boolean,
): Message[] {
  const serverPage = historyItemsToMessages(items)
  const localSuffix = current.filter((message) => serverMessageId(message) === null)
  if (!hasMore) return [...serverPage, ...localSuffix]
  if (items.length === 0) return current

  const pageIds = new Set(items.map((item) => item.id))
  const oldestPageId = Math.min(...pageIds)
  const cachedIds = new Set<number>()
  const olderCachedServer = current.filter((message) => {
    const id = serverMessageId(message)
    if (id === null || id >= oldestPageId || pageIds.has(id) || cachedIds.has(id)) return false
    cachedIds.add(id)
    return true
  })
  return [...olderCachedServer, ...serverPage, ...localSuffix]
}

/** Prepend older server messages without replacing current local/optimistic/streaming state. */
export function mergeOlderMessages(current: Message[], items: ChatHistoryItem[]): Message[] {
  const existingIds = new Set(current.map((message) => message.id))
  const older = historyItemsToMessages(items).filter((message) => !existingIds.has(message.id))
  return older.length === 0 ? current : [...older, ...current]
}
