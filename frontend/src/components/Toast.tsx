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
 * 收斂「呼叫 API → 成功 toast(可選) → 錯誤 toast」樣板。成功後續的 state 轉移
 * （例如 setEditing/setAdvanced）透過 onSuccess 回呼帶入，呼叫端保留完整控制；
 * onSuccess 若拋錯也會被同一個 catch 吃下並轉成錯誤 toast（等同原本把後續步驟
 * 一起包在 try 裡的行為）。
 */
export async function runWithToast<T>(
  toast: ToastFn,
  fn: () => Promise<T>,
  opts: { success?: string; onSuccess?: (result: T) => void | Promise<void> } = {},
): Promise<void> {
  try {
    const result = await fn()
    if (opts.success) toast(opts.success, 'success')
    await opts.onSuccess?.(result)
  } catch (e) {
    toast((e as Error).message, 'error')
  }
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
