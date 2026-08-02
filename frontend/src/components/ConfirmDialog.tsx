import {
  createContext,
  useCallback,
  useContext,
  useRef,
  useState,
  type ReactNode,
} from 'react'
import Modal from './Modal'

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
 * 一次只顯示一個（破壞性操作皆為單點觸發，不需佇列）。底層用 Modal（原生 <dialog>），
 * focus-trap／Escape 關閉／觸發焦點還原都交給 Modal。
 */
export function ConfirmProvider({ children }: { children: ReactNode }) {
  const [pending, setPending] = useState<Pending | null>(null)
  const resolveRef = useRef<((v: boolean) => void) | null>(null)
  const cancelRef = useRef<HTMLButtonElement | null>(null)

  const confirm = useCallback<ConfirmFn>((message, opts) => {
    return new Promise<boolean>((resolve) => {
      resolveRef.current = resolve
      setPending({ message, ...opts })
    })
  }, [])

  const settle = useCallback((result: boolean) => {
    resolveRef.current?.(result)
    resolveRef.current = null
    setPending(null)
  }, [])

  // memo 化：Modal 的原生 cancel/close listener 以 onClose 為依賴，行內箭頭會讓它每次重繪都解綁重綁。
  const dismiss = useCallback(() => settle(false), [settle])

  return (
    <ConfirmContext.Provider value={confirm}>
      {children}
      <Modal
        open={!!pending}
        onClose={dismiss}
        initialFocusRef={cancelRef}
        className="modal-host"
        aria-label="確認操作"
      >
        {pending && (
          <div className="confirm-dialog">
            <p className="confirm-dialog__message">{pending.message}</p>
            <div className="confirm-dialog__actions">
              <button ref={cancelRef} className="btn" onClick={() => settle(false)}>
                取消
              </button>
              <button
                className={`btn ${pending.danger ? 'confirm-dialog__confirm--danger' : 'btn--primary'}`}
                onClick={() => settle(true)}
              >
                {pending.confirmLabel ?? '確認'}
              </button>
            </div>
          </div>
        )}
      </Modal>
    </ConfirmContext.Provider>
  )
}
