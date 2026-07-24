import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useRef,
  useState,
  type ReactNode,
} from 'react'

interface ConfirmOpts {
  danger?: boolean
  confirmLabel?: string
}
type ConfirmFn = (message: string, opts?: ConfirmOpts) => Promise<boolean>

const ConfirmContext = createContext<ConfirmFn | null>(null)

/** 取得確認對話框觸發函式；必須在 <ConfirmProvider> 內使用。 */
export function useConfirm(): ConfirmFn {
  const fn = useContext(ConfirmContext)
  if (!fn) throw new Error('useConfirm 必須在 ConfirmProvider 內使用')
  return fn
}

interface Pending extends ConfirmOpts {
  message: string
}

/**
 * 自製確認對話框，取代 window.confirm。比照 Toast 的 context provider 模式：
 * useConfirm() 回傳 (message, opts?) => Promise<boolean>，同一 provider 內渲染宿主。
 * 一次只顯示一個（破壞性操作皆為單點觸發，不需佇列）。
 */
export function ConfirmProvider({ children }: { children: ReactNode }) {
  const [pending, setPending] = useState<Pending | null>(null)
  const resolveRef = useRef<((v: boolean) => void) | null>(null)
  const triggerRef = useRef<HTMLElement | null>(null)
  const cancelRef = useRef<HTMLButtonElement | null>(null)
  const confirmRef = useRef<HTMLButtonElement | null>(null)

  const confirm = useCallback<ConfirmFn>((message, opts) => {
    // 在重繪前先記住觸發元素，關閉後把焦點還原回去。
    triggerRef.current = document.activeElement as HTMLElement | null
    return new Promise<boolean>((resolve) => {
      resolveRef.current = resolve
      setPending({ message, ...opts })
    })
  }, [])

  const settle = useCallback((result: boolean) => {
    resolveRef.current?.(result)
    resolveRef.current = null
    setPending(null)
    triggerRef.current?.focus?.()
    triggerRef.current = null
  }, [])

  // 開啟時把焦點移到「取消」鈕（Enter 不會誤觸確認）；Escape = 取消。
  // 焦點循環見 onKeyDown：Tab/Shift+Tab 只在取消／確認兩鈕之間繞，不外洩到背景。
  useEffect(() => {
    if (!pending) return
    cancelRef.current?.focus()
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        e.preventDefault()
        settle(false)
      }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [pending, settle])

  return (
    <ConfirmContext.Provider value={confirm}>
      {children}
      {pending && (
        <div className="confirm-overlay" onClick={() => settle(false)}>
          <div
            className="confirm-dialog"
            role="dialog"
            aria-modal="true"
            aria-label="確認操作"
            onClick={(e) => e.stopPropagation()}
            onKeyDown={(e) => {
              // 最小 focus-trap：只有兩顆鈕，Tab/Shift+Tab 在兩者間循環，不讓焦點離開對話框。
              if (e.key !== 'Tab') return
              e.preventDefault()
              const next = document.activeElement === cancelRef.current ? confirmRef : cancelRef
              next.current?.focus()
            }}
          >
            <p className="confirm-dialog__message">{pending.message}</p>
            <div className="confirm-dialog__actions">
              <button ref={cancelRef} className="btn" onClick={() => settle(false)}>
                取消
              </button>
              <button
                ref={confirmRef}
                className={`btn ${pending.danger ? 'confirm-dialog__confirm--danger' : 'btn--primary'}`}
                onClick={() => settle(true)}
              >
                {pending.confirmLabel ?? '確認'}
              </button>
            </div>
          </div>
        </div>
      )}
    </ConfirmContext.Provider>
  )
}
