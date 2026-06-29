// 與 Spring 後端 /api/chat 對接的薄封裝。
// 開發時經由 Vite proxy 轉發到 http://localhost:8080（見 vite.config.ts），
// 因此這裡一律用相對路徑 /api，免處理 CORS。

/** 對應後端 service.dto.ChatResponse。 */
export interface ChatResponse {
  id: number
  reply: string
  createdAt: string // ISO-8601（後端為 java.time.Instant）
}

const JSON_HEADERS = { 'Content-Type': 'application/json' }

/** 送出一則訊息並取得 AI 回覆。對應 POST /api/chat。 */
export async function sendChat(message: string, signal?: AbortSignal): Promise<ChatResponse> {
  const res = await fetch('/api/chat', {
    method: 'POST',
    headers: JSON_HEADERS,
    body: JSON.stringify({ message }),
    signal,
  })
  if (!res.ok) {
    const detail = await res.text().catch(() => '')
    throw new Error(`請求失敗（HTTP ${res.status}）${detail ? `：${detail}` : ''}`)
  }
  return (await res.json()) as ChatResponse
}

/**
 * 取得歷史回覆（由新到舊）。對應 GET /api/chat/history。
 * 注意：目前後端 ChatResponse 只含 reply，不含使用者原句，
 *       因此歷史無法重建完整對話——這是待補的後端缺口之一。
 */
export async function fetchHistory(signal?: AbortSignal): Promise<ChatResponse[]> {
  const res = await fetch('/api/chat/history', { signal })
  if (!res.ok) throw new Error(`載入歷史失敗（HTTP ${res.status}）`)
  return (await res.json()) as ChatResponse[]
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
  const res = await fetch('/api/chat/stream', {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      Accept: 'text/event-stream, application/json',
    },
    body: JSON.stringify({ message }),
    signal,
  })
  if (!res.ok || !res.body) {
    const detail = await res.text().catch(() => '')
    throw new Error(`串流請求失敗（HTTP ${res.status}）${detail ? `：${detail}` : ''}`)
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
