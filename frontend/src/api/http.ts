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

// 401 全域處理：useAuth 掛上登出函式；任一 API 收到 401 就呼叫它清 session 回登入頁。
let logoutHandler: (() => void) | null = null
export function setLogoutHandler(fn: (() => void) | null): void {
  logoutHandler = fn
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
 * fetch 薄封裝。自動帶 Authorization、對 JSON body 補 Content-Type、
 * 解析 ApiError、處理 204（無 body）。回傳已解析的 JSON。
 */
export async function apiFetch<T = unknown>(
  path: string,
  options: RequestInit = {},
): Promise<T> {
  const session = getSession()
  const headers = new Headers(options.headers)
  if (options.body && !headers.has('Content-Type')) {
    headers.set('Content-Type', 'application/json')
  }
  if (session) headers.set('Authorization', `Bearer ${session.token}`)

  const res = await fetch(path, { ...options, headers })

  if (res.status === 204) {
    return undefined as T
  }

  const data = await res.json().catch(() => null)
  if (!res.ok) {
    const message =
      data && typeof data.message === 'string'
        ? data.message
        : `請求失敗（HTTP ${res.status}）`
    const fieldErrors =
      data && data.fieldErrors && typeof data.fieldErrors === 'object'
        ? (data.fieldErrors as Record<string, string>)
        : undefined
    // 401：token 過期 → 全域登出回登入頁。仍沿用後端 message（登入頁的帳密錯誤
    // 也是 401，需顯示真正原因，而非蓋成「session 過期」）。
    if (res.status === 401) {
      if (session) sessionExpired = true // 有 session 才是「被踢出」，登入失敗不算
      logoutHandler?.()
    }
    throw new ApiError(res.status, message, fieldErrors)
  }
  return data as T
}
