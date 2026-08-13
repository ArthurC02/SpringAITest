import { useEffect, useRef, type KeyboardEvent } from 'react'
import { useOverlay } from './overlay'

export interface ContextMenuItem {
  label: string
  onSelect: () => void
  disabled?: boolean
  /** disabled 時說明原因（例如「必要節點不可刪除」）。 */
  title?: string
}

interface ContextMenuProps {
  x: number
  y: number
  label: string
  items: ContextMenuItem[]
  onClose: () => void
}

/** 自製右鍵情境選單（無相依）：方向鍵巡覽、Esc 關閉並還原焦點、點擊外部關閉、超出視窗自動翻轉。 */
export default function ContextMenu({ x, y, label, items, onClose }: ContextMenuProps) {
  const { ref, style } = useOverlay<HTMLDivElement>(x, y, onClose)
  const itemsRef = useRef<(HTMLButtonElement | null)[]>([])

  useEffect(() => { itemsRef.current.find((item) => item && !item.disabled)?.focus() }, [])

  function move(step: number) {
    const buttons = itemsRef.current.filter((item): item is HTMLButtonElement => !!item && !item.disabled)
    if (buttons.length === 0) return
    const current = buttons.indexOf(document.activeElement as HTMLButtonElement)
    buttons[(current + step + buttons.length) % buttons.length].focus()
  }

  function onKeyDown(event: KeyboardEvent) {
    if (event.key === 'ArrowDown') { event.preventDefault(); move(1) }
    else if (event.key === 'ArrowUp') { event.preventDefault(); move(-1) }
    else if (event.key === 'Escape') { event.preventDefault(); onClose() }
  }

  return (
    <div className="workflow-menu" ref={ref} style={style} role="menu" aria-label={label} onKeyDown={onKeyDown}>
      {items.map((item, index) => (
        <button
          key={item.label} type="button" role="menuitem" className="workflow-menu__item"
          ref={(element) => { itemsRef.current[index] = element }}
          disabled={item.disabled} title={item.title}
          onClick={() => { item.onSelect(); onClose() }}
        >{item.label}</button>
      ))}
    </div>
  )
}
