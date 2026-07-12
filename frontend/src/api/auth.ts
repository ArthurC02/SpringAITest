// 登入/註冊 + session 的 localStorage 存取（單一 JSON key）。
// 註：http.ts 會 import 這裡的 getSession 取 token；本檔 import http 的 apiFetch，
// 兩者互為循環引用，但都只在函式執行時（runtime）互相呼叫，非模組載入期，故安全。
import { apiFetch } from './http'
import type { Session } from '../types'

const SESSION_KEY = 'springai:session'

export function getSession(): Session | null {
  try {
    const raw = localStorage.getItem(SESSION_KEY)
    return raw ? (JSON.parse(raw) as Session) : null
  } catch {
    return null
  }
}

export function saveSession(s: Session): void {
  localStorage.setItem(SESSION_KEY, JSON.stringify(s))
}

export function clearSession(): void {
  localStorage.removeItem(SESSION_KEY)
}

/** 登入成功回 {token,username,role,tenantCode}，同時寫入 localStorage。 */
export async function login(username: string, password: string): Promise<Session> {
  const s = await apiFetch<Session>('/api/auth/login', {
    method: 'POST',
    body: JSON.stringify({ username, password }),
  })
  saveSession(s)
  return s
}

/** 註冊回 {username,role,tenantCode}（不含 token；成功後仍需登入）。 */
export interface RegisterResult {
  username: string
  role: string
  tenantCode: string
}

export function register(
  username: string,
  password: string,
  tenantCode: string,
  inviteCode: string,
): Promise<RegisterResult> {
  return apiFetch<RegisterResult>('/api/auth/register', {
    method: 'POST',
    body: JSON.stringify({ username, password, tenantCode, inviteCode }),
  })
}
