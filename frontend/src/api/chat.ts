// 與 Spring 後端 /api/chat/stream 對接的薄封裝（含 SSE 解析）。
// 開發時經由 Vite proxy 轉發到 http://localhost:8080（見 vite.config.ts），
// 因此這裡一律用相對路徑 /api，免處理 CORS。
import { getSession } from './auth'
import { apiFetch, parseErrorMessage, triggerLogout } from './http'
import { CHAT_CONVERSATION_ID_KEY } from '../storageKeys'
import {
  CHAT_HISTORY_PAGE_SIZE,
  parseChatHistoryPage,
  type ChatHistoryPage,
} from '../chatHistory'

/** 開一段新對話；下一個成功回應會提供新的 conversationId。 */
export function newConversation(): void {
  localStorage.removeItem(CHAT_CONVERSATION_ID_KEY)
}

export async function getChatHistoryPage(
  before?: string,
  signal?: AbortSignal,
): Promise<ChatHistoryPage> {
  const query = new URLSearchParams({ limit: String(CHAT_HISTORY_PAGE_SIZE) })
  if (before !== undefined) query.set('before', before)
  const response = await apiFetch<unknown>(`/api/chat/history/page?${query}`, { signal })
  return parseChatHistoryPage(response)
}

/**
 * 以串流方式送出訊息。對應 POST /api/chat/stream（後端回 text/event-stream）。
 * 每收到一個 token chunk 就呼叫一次 onToken，呼叫端可逐字累加顯示。
 *
 * 用 fetch + ReadableStream 而非原生 EventSource，因為 EventSource 只支援 GET、
 * 無法帶 JSON body；這裡維持與 /api/chat 一致的 POST 契約。
 */
export async function streamChat(
  message: string,
  onToken: (chunk: string) => void,
  signal?: AbortSignal,
  orchestratorId?: string | null,
): Promise<void> {
  const headers: Record<string, string> = {
    'Content-Type': 'application/json',
    Accept: 'text/event-stream, application/json',
  }
  const session = getSession()
  if (!session) {
    triggerLogout()
    throw new Error('請先登入')
  }
  headers.Authorization = `Bearer ${session.token}`
  const requestSessionToken = session.token

  const conversationId = localStorage.getItem(CHAT_CONVERSATION_ID_KEY)

  const res = await fetch('/api/chat/stream', {
    method: 'POST',
    headers,
    body: JSON.stringify({
      message,
      ...(conversationId ? { conversationId } : {}),
      ...(orchestratorId ? { orchestratorId } : {}),
    }),
    signal,
  })
  if (res.status === 401 && getSession()?.token === requestSessionToken) {
    triggerLogout()
  }

  if (!res.ok || !res.body) {
    // 錯誤時 body 是 ApiError JSON（非 SSE），比照 apiFetch 取 message，
    // 免得整包原始 JSON 被當成訊息塞進聊天泡泡。
    const message = await parseErrorMessage(res, `串流請求失敗（HTTP ${res.status}）`)
    throw new Error(message)
  }

  const responseConversationId = res.headers.get('X-Conversation-Id')
  if (responseConversationId) localStorage.setItem(CHAT_CONVERSATION_ID_KEY, responseConversationId)

  const reader = res.body.getReader()
  const decoder = new TextDecoder()
  let buffer = ''

  for (;;) {
    const { value, done } = await reader.read()
    if (done) break
    buffer += decoder.decode(value, { stream: true })

    // SSE 事件以空行（\n\n）分隔；只處理已完整收到的事件，剩餘留在 buffer。
    let sep: number
    while ((sep = buffer.indexOf('\n\n')) !== -1) {
      const rawEvent = buffer.slice(0, sep)
      buffer = buffer.slice(sep + 2)
      const { event, data } = parseSseEvent(rawEvent)
      // 串流中途失敗時 platform 送 `event:error` + `data:<通用錯誤訊息>` 後正常結束；
      // 以該訊息拋出，走 useChat 既有的錯誤泡泡路徑。正常 data frame 行為不變。
      if (event === 'error') {
        await reader.cancel()
        throw new Error(data || '串流過程發生錯誤')
      }
      if (data) onToken(data)
    }
  }
}

/**
 * 從一個 SSE 事件文字取出 event 名稱與 data 內容。
 * Spring 的 SSE writer 寫成 `data:<值>`（冒號後不加裝飾空格），多行值會拆成多個 data: 行，
 * 因此這裡取 `data:` 之後的全部字元（不去除前導空格，以保留 token 原本的空白），
 * 並把多個 data: 行以 \n 接回，還原原始 token。event 行（同樣無空格風格）用來標示錯誤 frame。
 */
function parseSseEvent(rawEvent: string): { event: string; data: string } {
  let event = ''
  const out: string[] = []
  for (const line of rawEvent.split('\n')) {
    if (line.startsWith('data:')) out.push(line.slice(5))
    else if (line.startsWith('event:')) event = line.slice(6).trim()
  }
  return { event, data: out.join('\n') }
}
