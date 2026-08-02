import { useEffect, useRef, type ComponentPropsWithoutRef, type RefObject } from 'react'

type DialogProps = Omit<ComponentPropsWithoutRef<'dialog'>, 'open' | 'onClose'>

interface Props extends DialogProps {
  open: boolean
  onClose: () => void
  /** true 時 Escape／背景點擊都不可關閉（例如發布中）。 */
  busy?: boolean
  /** 開啟後要接手焦點的元素（通常是「取消」鈕，比照原 confirm/preview 對話框行為）。 */
  initialFocusRef?: RefObject<HTMLButtonElement | null>
}

/**
 * 原生 `<dialog>` + `showModal()` 封裝，取代手刻的 overlay div + keydown Tab 循環 +
 * window Escape listener（免費拿到 focus-trap、Escape 關閉、top-layer overlay）。
 * 呼叫端只控制 open/busy；Modal 負責：開啟時記住觸發元素、關閉後還原焦點；
 * Escape 走原生 `cancel` 事件（busy 時 preventDefault 擋下，不呼叫 onClose);
 * 背景點擊比照 Escape（busy 時同樣不可關閉)。內容仍由呼叫端傳入（children）。
 */
export default function Modal({
  open,
  onClose,
  busy = false,
  initialFocusRef,
  className,
  children,
  ...rest
}: Props) {
  const ref = useRef<HTMLDialogElement>(null)
  const triggerRef = useRef<HTMLElement | null>(null)
  const restorePendingRef = useRef(false)

  const restoreTriggerFocus = () => {
    const trigger = triggerRef.current
    if (trigger?.isConnected) trigger.focus()
    triggerRef.current = null
    restorePendingRef.current = false
  }

  // open→關：呼叫端(例如 settle()/closePreview())已自行更新狀態，這裡只負責同步 DOM。
  // open→開：記住觸發元素，供關閉後還原焦點；並把焦點交給呼叫端指定的元素(通常是取消鈕)。
  useEffect(() => {
    const dialog = ref.current
    if (!dialog) return
    if (open && !dialog.open) {
      triggerRef.current = document.activeElement as HTMLElement | null
      dialog.showModal()
      initialFocusRef?.current?.focus()
    } else if (!open && dialog.open) {
      dialog.close()
    }
  }, [open, initialFocusRef])

  useEffect(() => {
    const dialog = ref.current
    if (!dialog) return
    const onCancel = (e: Event) => {
      // busy 時擋下 Escape 的預設關閉動作；不擋則放行，讓下面的 close 事件做還原焦點，
      // 並呼叫 onClose 讓呼叫端同步自己的狀態(見上方 open→關的註解)。
      if (busy) {
        e.preventDefault()
        return
      }
      onClose()
    }
    const onNativeClose = () => {
      if (busy) restorePendingRef.current = true
      else restoreTriggerFocus()
    }
    dialog.addEventListener('cancel', onCancel)
    dialog.addEventListener('close', onNativeClose)
    return () => {
      dialog.removeEventListener('cancel', onCancel)
      dialog.removeEventListener('close', onNativeClose)
    }
  }, [busy, onClose])

  useEffect(() => {
    if (!busy && restorePendingRef.current) restoreTriggerFocus()
  }, [busy])

  return (
    <dialog
      ref={ref}
      className={className}
      onClick={(e) => {
        if (!busy && e.target === ref.current) onClose()
      }}
      {...rest}
    >
      {children}
    </dialog>
  )
}
