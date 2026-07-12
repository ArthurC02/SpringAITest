import {
  createContext,
  useCallback,
  useContext,
  useState,
  type ReactNode,
} from 'react'

type ToastKind = 'success' | 'error'
interface ToastItem {
  id: string
  message: string
  kind: ToastKind
}
type ToastFn = (message: string, kind?: ToastKind) => void

const ToastContext = createContext<ToastFn | null>(null)

/** 取得 toast 觸發函式；必須在 <ToastProvider> 內使用。 */
export function useToast(): ToastFn {
  const fn = useContext(ToastContext)
  if (!fn) throw new Error('useToast 必須在 ToastProvider 內使用')
  return fn
}

/**
 * 極簡 toast：右下角固定位、3 秒自動消失、可點 × 關閉。
 * 容器 aria-live="polite"，成功用 role="status"、錯誤用 role="alert"。
 */
export function ToastProvider({ children }: { children: ReactNode }) {
  const [toasts, setToasts] = useState<ToastItem[]>([])

  const remove = useCallback((id: string) => {
    setToasts((t) => t.filter((x) => x.id !== id))
  }, [])

  const toast = useCallback<ToastFn>(
    (message, kind = 'success') => {
      const id = crypto.randomUUID()
      setToasts((t) => [...t, { id, message, kind }])
      window.setTimeout(() => remove(id), 3000)
    },
    [remove],
  )

  return (
    <ToastContext.Provider value={toast}>
      {children}
      <div className="toast-container" aria-live="polite">
        {toasts.map((t) => (
          <div
            key={t.id}
            className={`toast toast--${t.kind}`}
            role={t.kind === 'error' ? 'alert' : 'status'}
          >
            <span>{t.message}</span>
            <button
              className="toast__close"
              aria-label="關閉"
              onClick={() => remove(t.id)}
            >
              ×
            </button>
          </div>
        ))}
      </div>
    </ToastContext.Provider>
  )
}
