// 所有需要 JWT 的 API 都走 apiFetch：帶 Bearer、統一解析 ApiError、401 觸發登出。
// 與 chat.ts 一樣走相對路徑 /api（Vite proxy → :8080，正式環境 nginx 反代）。
import { getSession } from './auth'

/**
 * 後端錯誤 body 的統一形狀 `{timestamp,status,message,fieldErrors}`。
 * 用一個 Error 子類承載，呼叫端可 `instanceof ApiError` 取 message / fieldErrors 顯示。
 */
export class ApiError extends Error {
  status: number
  fieldErrors?: Record<string, string>
  constructor(status: number, message: string, fieldErrors?: Record<string, string>) {
    super(message)
    this.name = 'ApiError'
    this.status = status
    this.fieldErrors = fieldErrors
  }
}

/** 409/412 都是樂觀併發衝突:草稿已被他人更新或發布內容與已驗證版本不符,需重新載入。 */
export function isConflict(error: unknown): boolean {
  return error instanceof ApiError && (error.status === 409 || error.status === 412)
}

/** 404 常代表 fail-closed 的 feature flag 關閉(例:RUN_EVAL_ENABLED=false),
 * 呼叫端可藉此顯示「未啟用」空狀態而非當成一般錯誤噴出。 */
export function isNotFound(error: unknown): boolean {
  return error instanceof ApiError && error.status === 404
}

// 401 全域處理：useAuth 掛上登出函式；任一 API 收到 401 就呼叫它清 session 回登入頁。
let logoutHandler: (() => void) | null = null
export function setLogoutHandler(fn: (() => void) | null): void {
  logoutHandler = fn
}

/**
 * 走與「收到 401」相同的全域登出路徑（顯示 session 過期提示、回登入頁）。
 * 供不走 apiFetch 的手寫 fetch（例如 chat.ts 的 SSE 串流）在自行偵測到 session 失效時呼叫，
 * 不讓它們繞過既有機制自己清 localStorage。
 */
export function triggerLogout(): void {
  sessionExpired = true
  logoutHandler?.()
}

// 「已登入卻被 401 踢出」的一次性旗標，AuthPage 讀一次即清，用來顯示過期提示。
// 登入帳密錯誤的 401 不會設（當下沒有 session）。module 變數即可：
// 401 踢出不會整頁重載，App 只是 re-render 到 AuthPage。
let sessionExpired = false
export function consumeSessionExpired(): boolean {
  const was = sessionExpired
  sessionExpired = false
  return was
}

/**
 * 追蹤編號的有界化：trim 後 1..128 字元且不含控制字元才採用，與 backend
 * `Common/IdentityHeaders.SingleBoundedValue` 的邊界一致。不符就當作沒有，
 * 不把未消毒的字串顯示給使用者。
 */
function boundedCorrelationId(raw: unknown): string | null {
  if (typeof raw !== 'string') return null
  const id = raw.trim()
  if (id.length === 0 || id.length > 128) return null
  for (let i = 0; i < id.length; i += 1) {
    const code = id.charCodeAt(i)
    if (code < 0x20 || code === 0x7f) return null
  }
  return id
}

/**
 * 從錯誤回應 body 解析出訊息：body 是 ApiError JSON 且有字串 message 就用它，
 * 否則用呼叫端給的 fallback 文案。`request()`（apiFetch/apiFetchBlob 共用）與
 * chat.ts 的 streamChat()（不走 apiFetch，SSE 需要原始 ReadableStream）共用這個解析。
 *
 * 5xx 另外把追蹤編號併進 message 字串（而非另開 ApiError 欄位）：呼叫端普遍以
 * `(e as Error).message` 存成 string state，單點併入即可一次覆蓋 toast / ErrorText /
 * 聊天錯誤氣泡三條顯示路徑，零呼叫端改動。只對 5xx 附加——4xx 的訊息本身已是可行動
 * 的人話，附編號只是噪音；且 404 偽裝契約要求「旗標關閉」與「路由不存在」不可區分，
 * 不得在該路徑上引入任何額外資訊。
 */
export async function parseErrorMessage(res: Response, fallback: string): Promise<string> {
  const data = await res.json().catch(() => null)
  const message = data && typeof data.message === 'string' ? data.message : fallback
  if (res.status < 500) return message
  const id =
    boundedCorrelationId(data?.correlationId) ??
    boundedCorrelationId(res.headers.get('X-Correlation-Id'))
  return id ? `${message}(追蹤編號:${id})` : message
}

/**
 * 共用請求核心：自動帶 Authorization、對 JSON body 補 Content-Type、
 * 錯誤一律轉成 ApiError（401 觸發全域登出）。成功時回傳原始 Response，
 * 由呼叫端決定解析成 JSON（apiFetch）或 blob（apiFetchBlob）。
 */
async function request(path: string, options: RequestInit): Promise<Response> {
  const session = getSession()
  const requestSessionToken = session?.token ?? null
  const headers = new Headers(options.headers)
  // FormData（multipart 上傳，例：agentic package import）不可硬設 Content-Type，
  // 否則會蓋掉瀏覽器自動帶的 multipart boundary。其餘 body 一律補 application/json。
  if (options.body && !(options.body instanceof FormData) && !headers.has('Content-Type')) {
    headers.set('Content-Type', 'application/json')
  }
  if (session) headers.set('Authorization', `Bearer ${session.token}`)

  const res = await fetch(path, { ...options, headers })

  if (!res.ok) {
    // 錯誤 body 一律是 ApiError JSON（blob 端點也不例外）。body 只能消費一次，
    // clone 一份給 parseErrorMessage 取 message，原始 res 留著再讀一次取 fieldErrors。
    const message = await parseErrorMessage(res.clone(), `請求失敗（HTTP ${res.status}）`)
    const data = await res.json().catch(() => null)
    const fieldErrors =
      data && data.fieldErrors && typeof data.fieldErrors === 'object'
        ? (data.fieldErrors as Record<string, string>)
        : undefined
    // 401：token 過期 → 全域登出回登入頁。仍沿用後端 message（登入頁的帳密錯誤
    // 也是 401，需顯示真正原因，而非蓋成「session 過期」）。
    if (res.status === 401) {
      // Response parsing can outlive the session that sent the request. Never
      // let an old account's late 401 log out a newer account.
      if (requestSessionToken && getSession()?.token === requestSessionToken) {
        sessionExpired = true
        logoutHandler?.()
      }
    }
    throw new ApiError(res.status, message, fieldErrors)
  }
  return res
}

/** fetch 薄封裝。回傳已解析的 JSON；204（無 body）回 undefined。 */
export async function apiFetch<T = unknown>(
  path: string,
  options: RequestInit = {},
): Promise<T> {
  const res = await request(path, options)
  if (res.status === 204) {
    return undefined as T
  }
  return (await res.json().catch(() => null)) as T
}

/**
 * 同 apiFetch，但一併回傳 `ETag` response header——供 Agent draft 的 If-Match 樂觀併發。
 * apiFetch 只吐 JSON，拿不到 header，故 GET 需要 ETag 的端點走這支。
 */
export async function apiFetchWithEtag<T = unknown>(
  path: string,
  options: RequestInit = {},
): Promise<{ data: T; etag: string | null }> {
  const res = await request(path, options)
  const etag = res.headers.get('ETag')
  const data = (res.status === 204 ? undefined : await res.json().catch(() => null)) as T
  return { data, etag }
}

/** 同 apiFetch，但回傳二進位 body（檔案下載，例：skill export zip）。 */
export async function apiFetchBlob(
  path: string,
  options: RequestInit = {},
): Promise<Blob> {
  return (await request(path, options)).blob()
}
