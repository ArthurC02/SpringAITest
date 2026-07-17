// 與 Spring 後端 /api/chat/stream 對接的薄封裝（含 SSE 解析）。
// 開發時經由 Vite proxy 轉發到 http://localhost:8080（見 vite.config.ts），
// 因此這裡一律用相對路徑 /api，免處理 CORS。
import { getSession } from './auth'
import { triggerLogout } from './http'

/**
 * mem0 長期記憶的分群鍵。登入後跟著使用者走（用 username），
 * 未登入時 fallback 到既有的「每瀏覽器一組穩定 UUID」邏輯。
 */
function getUserId(): string {
  const session = getSession()
  if (session?.username) return session.username

  const KEY = 'springai-chat:userId'
  let id = localStorage.getItem(KEY)
  if (!id) {
    id = crypto.randomUUID()
    localStorage.setItem(KEY, id)
  }
  return id
}

const CONVERSATION_KEY = 'springai-chat:conversationId'

/**
 * 一次對話的識別，供後端短期記憶（同對話多輪脈絡）分群用。
 * 與 userId 不同：userId 是「這個人」（跨對話長期記憶），conversationId 是「這一串對話」。
 */
function getConversationId(): string {
  let id = localStorage.getItem(CONVERSATION_KEY)
  if (!id) {
    id = crypto.randomUUID()
    localStorage.setItem(CONVERSATION_KEY, id)
  }
  return id
}

/** 開一段新對話：換掉 conversationId，讓後端的短期記憶重新開始（清除對話時呼叫）。 */
export function newConversation(): void {
  localStorage.setItem(CONVERSATION_KEY, crypto.randomUUID())
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
): Promise<void> {
  const headers: Record<string, string> = {
    'Content-Type': 'application/json',
    Accept: 'text/event-stream, application/json',
  }
  // 端點雖是 AllowAnonymous，但 ChatService 的工具路由（route→execute→summarize）在
  // userCtx 為 null 時會整段跳過、退回純聊天。這裡手帶 Bearer（不走 apiFetch，因 SSE 要
  // 原始 ReadableStream 處理），讓後端拿到 userCtx/tenant 才能執行數字類工具查詢；
  // 未登入則不帶，匿名聊天仍允許。
  const session = getSession()
  if (session) headers.Authorization = `Bearer ${session.token}`

  const res = await fetch('/api/chat/stream', {
    method: 'POST',
    headers,
    body: JSON.stringify({ message, userId: getUserId(), conversationId: getConversationId() }),
    signal,
  })
  // 這支端點 AllowAnonymous、驗證失敗永不回 401，platform 改用這個 header 標示
  // 「帶了 Authorization 但 JWT 驗證失敗」——只有在我們原本以為有 session 時才代表過期。
  if (res.headers.get('X-Auth-Invalid') && session) {
    triggerLogout()
    throw new Error('登入已過期，請重新登入')
  }

  if (!res.ok || !res.body) {
    // 錯誤時 body 是 ApiError JSON（非 SSE），比照 apiFetch 取 message，
    // 免得整包原始 JSON 被當成訊息塞進聊天泡泡。
    const data = await res.json().catch(() => null)
    const message =
      data && typeof data.message === 'string'
        ? data.message
        : `串流請求失敗（HTTP ${res.status}）`
    throw new Error(message)
  }

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
      const data = parseSseData(rawEvent)
      if (data) onToken(data)
    }
  }
}

/**
 * 從一個 SSE 事件文字取出 data 內容。
 * Spring 的 SSE writer 寫成 `data:<值>`（冒號後不加裝飾空格），多行值會拆成多個 data: 行，
 * 因此這裡取 `data:` 之後的全部字元（不去除前導空格，以保留 token 原本的空白），
 * 並把多個 data: 行以 \n 接回，還原原始 token。
 */
function parseSseData(rawEvent: string): string {
  const out: string[] = []
  for (const line of rawEvent.split('\n')) {
    if (line.startsWith('data:')) out.push(line.slice(5))
  }
  return out.join('\n')
}
