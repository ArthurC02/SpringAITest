import { useEffect, useState, type FormEvent } from 'react'
import { ApiError, consumeSessionExpired } from '../api/http'
import type { Session } from '../types'
import ErrorText from './ErrorText'
import FormField from './FormField'

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
  const [clientErrors, setClientErrors] = useState<Record<string, string>>({})
  const [notice, setNotice] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const isRegister = mode === 'register'

  // 被 401 踢出時顯示一次過期提示（旗標讀後即清；StrictMode 二次執行時
  // 旗標已清但 state 保留，行為一致）。使用者一提交就清掉。
  useEffect(() => {
    if (consumeSessionExpired()) setError('session 已過期，請重新登入。')
  }, [])

  // 前端驗證規則：說「怎麼修」而非只說「錯了」。伺服器端錯誤另走 fieldErrors，不動。
  function validate(field: string, val: string): string {
    const v = val.trim()
    switch (field) {
      case 'username':
        return v ? '' : '請輸入帳號'
      case 'password':
        if (!v) return '請輸入密碼'
        if (isRegister && val.length < 8) return '密碼至少 8 個字元'
        return ''
      case 'tenantCode':
        return v ? '' : '請輸入租戶代碼'
      case 'inviteCode':
        return v ? '' : '請輸入邀請碼'
      default:
        return ''
    }
  }

  // blur 首驗。
  function onBlur(field: string, val: string) {
    setClientErrors((e) => ({ ...e, [field]: validate(field, val) }))
  }

  // 已錯欄位 onChange 即時複驗：欄位目前有錯才在輸入時重新驗（避免打字途中一直冒錯）。
  function onChange(field: string, val: string, set: (v: string) => void) {
    set(val)
    if (clientErrors[field]) {
      setClientErrors((e) => ({ ...e, [field]: validate(field, val) }))
    }
  }

  async function onSubmit(e: FormEvent) {
    e.preventDefault()
    const fields = isRegister
      ? ['username', 'password', 'tenantCode', 'inviteCode']
      : ['username', 'password']
    const vals: Record<string, string> = { username, password, tenantCode, inviteCode }
    const errs: Record<string, string> = {}
    for (const f of fields) {
      const m = validate(f, vals[f])
      if (m) errs[f] = m
    }
    setClientErrors(errs)
    if (Object.keys(errs).length) return

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
    setClientErrors({})
    setNotice(null)
  }

  // 顯示用：前端驗證錯誤優先，否則伺服器欄位錯誤。
  const usernameErr = clientErrors.username || fieldErrors.username
  const passwordErr = clientErrors.password || fieldErrors.password
  const tenantErr = clientErrors.tenantCode || fieldErrors.tenantCode
  const inviteErr = clientErrors.inviteCode || fieldErrors.inviteCode

  return (
    <div className="auth" data-testid="auth-page">
      <form className="auth__card" onSubmit={onSubmit} noValidate>
        <h1 className="auth__title">{isRegister ? '註冊' : '登入'}</h1>
        <p className="muted auth__subtitle">資料分析平台</p>

        <FormField
          id="username"
          testId="auth-username"
          label="帳號"
          value={username}
          onChange={(v) => onChange('username', v, setUsername)}
          onBlur={(v) => onBlur('username', v)}
          autoComplete="username"
          required
          error={usernameErr}
        />

        <FormField
          id="password"
          testId="auth-password"
          type="password"
          label={`密碼${isRegister ? '（至少 8 碼）' : ''}`}
          value={password}
          onChange={(v) => onChange('password', v, setPassword)}
          onBlur={(v) => onBlur('password', v)}
          autoComplete={isRegister ? 'new-password' : 'current-password'}
          minLength={isRegister ? 8 : undefined}
          required
          error={passwordErr}
        />

        {isRegister && (
          <>
            <FormField
              id="tenantCode"
              testId="auth-tenant-code"
              label="租戶代碼"
              value={tenantCode}
              onChange={(v) => onChange('tenantCode', v, setTenantCode)}
              onBlur={(v) => onBlur('tenantCode', v)}
              required
              error={tenantErr}
            />
            <FormField
              id="inviteCode"
              testId="auth-invite-code"
              label="邀請碼"
              value={inviteCode}
              onChange={(v) => onChange('inviteCode', v, setInviteCode)}
              onBlur={(v) => onBlur('inviteCode', v)}
              required
              error={inviteErr}
            />
          </>
        )}

        <ErrorText msg={error} />
        {notice && (
          <p className="notice-text" role="status">
            {notice}
          </p>
        )}

        <button
          className="btn btn--primary btn--block"
          data-testid="auth-submit"
          type="submit"
          disabled={busy}
        >
          {busy ? '請稍候…' : isRegister ? '註冊' : '登入'}
        </button>

        <p className="muted auth__alt">
          {isRegister ? '已經有帳號?' : '還沒有帳號?'}{' '}
          <button
            type="button"
            className="auth__switch"
            data-testid="auth-switch-mode"
            onClick={switchMode}
          >
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
