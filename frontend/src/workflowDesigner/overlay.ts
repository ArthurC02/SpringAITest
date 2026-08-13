import { useEffect, useLayoutEffect, useRef, useState, type CSSProperties } from 'react'

const MARGIN = 8

/**
 * 浮層（NodePicker / ContextMenu）共用的三件事：以滑鼠座標定位並在超出視窗時翻轉、
 * 點擊外部關閉、卸載時把焦點還給觸發元素。兩個浮層共用同一份實作，不各寫一套。
 */
export function useOverlay<T extends HTMLElement>(x: number, y: number, onClose: () => void) {
  const ref = useRef<T>(null)
  const closeRef = useRef(onClose)
  closeRef.current = onClose
  // 觸發元素必須在 render 階段捕捉（比照 Modal.tsx 開啟時的記錄時機）：`<input autoFocus>` 的
  // 聚焦發生在 React commit，早於 passive effect，在 effect 裡抓到的會是浮層自己的搜尋框，
  // 卸載時它已 isConnected === false，焦點就掉回 <body>。
  const triggerRef = useRef<HTMLElement | null>(null)
  if (triggerRef.current === null) triggerRef.current = document.activeElement as HTMLElement | null
  const [style, setStyle] = useState<CSSProperties>({ position: 'fixed', left: x, top: y })

  // useLayoutEffect：翻轉在瀏覽器繪製前完成，所以不需要先隱藏再顯示
  // （隱藏會讓浮層在 mount 當下不可聚焦，方向鍵/自動聚焦就會失效）。
  useLayoutEffect(() => {
    const rect = ref.current?.getBoundingClientRect()
    if (!rect) return
    const left = x + rect.width > window.innerWidth ? Math.max(MARGIN, x - rect.width) : x
    const top = y + rect.height > window.innerHeight ? Math.max(MARGIN, y - rect.height) : y
    setStyle({ position: 'fixed', left, top })
  }, [x, y])

  useEffect(() => {
    const trigger = triggerRef.current
    const onPointerDown = (event: MouseEvent) => {
      if (!ref.current?.contains(event.target as globalThis.Node)) closeRef.current()
    }
    document.addEventListener('mousedown', onPointerDown)
    return () => {
      document.removeEventListener('mousedown', onPointerDown)
      // 只有在沒人接手焦點時才還原：右鍵選單收起同時開啟 picker 時，
      // 還原會把焦點從 picker 的搜尋框搶走。
      const active = document.activeElement
      if (active && active !== document.body) return
      if (trigger && trigger !== document.body && trigger.isConnected) trigger.focus()
    }
  }, [])

  return { ref, style }
}
