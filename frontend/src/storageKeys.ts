// 聊天相關 localStorage 鍵名的單一事實來源。登出（401 或手動）時要清乾淨這些鍵
// （跨使用者殘留是隱私邊界），因此鍵名只在這裡定義一次，其餘檔案一律 import 使用。
export const CHAT_USER_ID_KEY = 'springai-chat:userId'
export const CHAT_CONVERSATION_ID_KEY = 'springai-chat:conversationId'
export const CHAT_MESSAGES_KEY = 'springai-chat:messages'

/**
 * 登出時清掉同一前綴的所有鍵（跨使用者殘留是隱私邊界）。先蒐集再刪：
 * 邊走訪邊 removeItem 會讓 storage.key(index) 的索引位移而漏刪。
 * storage 可能被瀏覽器停用，失敗一律吞掉——登出流程不能因此中斷。
 */
export function clearByPrefix(storage: Storage | undefined, prefix: string): void {
  if (!storage) return
  try {
    const keys: string[] = []
    for (let index = 0; index < storage.length; index += 1) {
      const key = storage.key(index)
      if (key?.startsWith(prefix)) keys.push(key)
    }
    for (const key of keys) storage.removeItem(key)
  } catch {
    // Storage unavailable; logout must continue.
  }
}
