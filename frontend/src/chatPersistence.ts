import { CHAT_MESSAGES_KEY } from './storageKeys'
import type { Message } from './types'

let activeGeneration = 0

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

export function persistChatMessages(messages: Message[], generation: number): void {
  if (generation !== activeGeneration) return

  try {
    localStorage.setItem(CHAT_MESSAGES_KEY, JSON.stringify(messages))
  } catch {
    /* 容量滿等情況靜默忽略 */
  }
}