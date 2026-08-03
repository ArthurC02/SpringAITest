import {
  createContext,
  useCallback,
  useContext,
  useState,
  type ReactNode,
} from 'react'
import { isConflict } from '../api/http'

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
 *
 * **fn 不得自行吞掉錯誤**：吞掉等於回報成功，會噴出假的成功 toast。樂觀併發衝突
 * （409/412）用 `onConflict` 處理——由本層判斷並改為鎖定編輯器（呼叫端自己的
 * blocking banner + 重新載入路徑就是使用者的出口），成功 toast 一定不會出現，也
 * 不再另外噴錯誤 toast(避免與 banner 重複播報)；沒給 onConflict 的呼叫端，衝突
 * 就走一般錯誤 toast。
 */
export async function runWithToast<T>(
  toast: ToastFn,
  fn: () => Promise<T>,
  opts: {
    success?: string
    onSuccess?: (result: T) => void | Promise<void>
    onConflict?: () => void
  } = {},
): Promise<void> {
  try {
    const result = await fn()
    if (opts.success) toast(opts.success, 'success')
    await opts.onSuccess?.(result)
  } catch (e) {
    if (opts.onConflict && isConflict(e)) opts.onConflict()
    else toast((e as Error).message, 'error')
  }
}

/**
 * 寫入前提（已載入的資料、ETag）不成立時的守衛。**必須拋錯，不可靜默 return**：靜默
 * return 在 runWithToast 眼中就是成功，會噴出假的成功 toast。這些守衛今天不可達只因
 * 按鈕 disabled 恰好重複了同一組條件，disabled 一失守就必須是明確錯誤。
 */
export function requireLoaded<T>(value: T | null | undefined, label: string): T {
  if (!value) throw new Error(`${label}尚未載入完成，請重新載入後再試。`)
  return value
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
