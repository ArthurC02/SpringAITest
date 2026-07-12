import { useEffect, useState, type FormEvent } from 'react'
import { ApiError, consumeSessionExpired } from '../api/http'
import type { Session } from '../types'

interface Props {
  login: (username: string, password: string) => Promise<Session>
  register: (
    username: string,
    password: string,
    tenantCode: string,
    inviteCode: string,
  ) => Promise<unknown>
}

/** 未登入入口：登入 / 註冊切換。成功登入後 App 因 session 改變自動切到 AppShell。 */
export default function AuthPage({ login, register }: Props) {
  const [mode, setMode] = useState<'login' | 'register'>('login')
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [tenantCode, setTenantCode] = useState('')
  const [inviteCode, setInviteCode] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({})
  const [notice, setNotice] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  // 被 401 踢出時顯示一次過期提示（旗標讀後即清；StrictMode 二次執行時
  // 旗標已清但 state 保留，行為一致）。使用者一提交就清掉。
  useEffect(() => {
    if (consumeSessionExpired()) setError('session 已過期，請重新登入。')
  }, [])

  async function onSubmit(e: FormEvent) {
    e.preventDefault()
    setError(null)
    setFieldErrors({})
    setNotice(null)
    setBusy(true)
    try {
      if (mode === 'login') {
        await login(username, password)
      } else {
        await register(username, password, tenantCode, inviteCode)
        setNotice('註冊成功，請用剛才的帳密登入。')
        setMode('login')
        setPassword('')
      }
    } catch (err) {
      if (err instanceof ApiError) {
        setError(err.message)
        if (err.fieldErrors) setFieldErrors(err.fieldErrors)
      } else {
        setError((err as Error).message)
      }
    } finally {
      setBusy(false)
    }
  }

  function switchMode() {
    setMode((m) => (m === 'login' ? 'register' : 'login'))
    setError(null)
    setFieldErrors({})
    setNotice(null)
  }

  const isRegister = mode === 'register'

  return (
    <div className="auth">
      <form className="auth__card" onSubmit={onSubmit}>
        <h1 className="auth__title">{isRegister ? '註冊' : '登入'}</h1>
        <p className="muted" style={{ marginTop: 0 }}>Spring AI 資料檢索平台</p>

        <div className="field">
          <label htmlFor="username">帳號</label>
          <input
            id="username"
            className="input"
            value={username}
            onChange={(e) => setUsername(e.target.value)}
            autoComplete="username"
          />
          {fieldErrors.username && <span className="field-error">{fieldErrors.username}</span>}
        </div>

        <div className="field">
          <label htmlFor="password">密碼{isRegister ? '（至少 8 碼）' : ''}</label>
          <input
            id="password"
            type="password"
            className="input"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            autoComplete={isRegister ? 'new-password' : 'current-password'}
          />
          {fieldErrors.password && <span className="field-error">{fieldErrors.password}</span>}
        </div>

        {isRegister && (
          <>
            <div className="field">
              <label htmlFor="tenantCode">租戶代碼</label>
              <input
                id="tenantCode"
                className="input"
                value={tenantCode}
                onChange={(e) => setTenantCode(e.target.value)}
              />
              {fieldErrors.tenantCode && <span className="field-error">{fieldErrors.tenantCode}</span>}
            </div>
            <div className="field">
              <label htmlFor="inviteCode">邀請碼</label>
              <input
                id="inviteCode"
                className="input"
                value={inviteCode}
                onChange={(e) => setInviteCode(e.target.value)}
              />
              {fieldErrors.inviteCode && <span className="field-error">{fieldErrors.inviteCode}</span>}
            </div>
          </>
        )}

        {error && <p className="error-text">{error}</p>}
        {notice && <p className="notice-text">{notice}</p>}

        <button className="btn btn--primary" type="submit" disabled={busy} style={{ width: '100%' }}>
          {busy ? '請稍候…' : isRegister ? '註冊' : '登入'}
        </button>

        <p className="muted" style={{ marginTop: 14, fontSize: 13 }}>
          {isRegister ? '已經有帳號?' : '還沒有帳號?'}{' '}
          <button type="button" className="auth__switch" onClick={switchMode}>
            {isRegister ? '改為登入' : '註冊'}
          </button>
        </p>

        <div className="auth__seed">
          種子帳號（密碼 password123）:<br />
          admin-a · user-a（租戶 demo-a，邀請碼 demo-a-invite）<br />
          user-b（租戶 demo-b，邀請碼 demo-b-invite）
        </div>
      </form>
    </div>
  )
}
