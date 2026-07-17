// 聊天相關 localStorage 鍵名的單一事實來源。登出（401 或手動）時要清乾淨這些鍵
// （跨使用者殘留是隱私邊界），因此鍵名只在這裡定義一次，其餘檔案一律 import 使用。
export const CHAT_USER_ID_KEY = 'springai-chat:userId'
export const CHAT_CONVERSATION_ID_KEY = 'springai-chat:conversationId'
export const CHAT_MESSAGES_KEY = 'springai-chat:messages'
