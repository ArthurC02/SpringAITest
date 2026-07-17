import { useCallback, useEffect, useState } from 'react'
import {
  getSession,
  clearSession,
  login as apiLogin,
  register as apiRegister,
  type RegisterResult,
} from '../api/auth'
import { setLogoutHandler } from '../api/http'
import { CHAT_MESSAGES_KEY, CHAT_CONVERSATION_ID_KEY, CHAT_USER_ID_KEY } from '../storageKeys'
import type { Session } from '../types'

/**
 * 頂層身分狀態。App 依 session 有無決定顯示 AuthPage 或 AppShell。
 * 同時把 logout 掛成 apiFetch 的全域 401 處理：任何請求收到 401 → 自動登出回登入頁。
 */
export function useAuth() {
  const [session, setSession] = useState<Session | null>(getSession)

  const logout = useCallback(() => {
    clearSession()
    // 一併清掉聊天資料，避免共用瀏覽器時下一位使用者看到前一位的完整對話。
    localStorage.removeItem(CHAT_MESSAGES_KEY)
    localStorage.removeItem(CHAT_CONVERSATION_ID_KEY)
    localStorage.removeItem(CHAT_USER_ID_KEY)
    setSession(null)
  }, [])

  useEffect(() => {
    setLogoutHandler(logout)
    return () => setLogoutHandler(null)
  }, [logout])

  const login = useCallback(async (username: string, password: string): Promise<Session> => {
    const s = await apiLogin(username, password)
    setSession(s)
    return s
  }, [])

  const register = useCallback(
    (
      username: string,
      password: string,
      tenantCode: string,
      inviteCode: string,
    ): Promise<RegisterResult> => apiRegister(username, password, tenantCode, inviteCode),
    [],
  )

  return { session, login, logout, register }
}
