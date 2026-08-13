import { useEffect, useMemo, useRef, useState, type KeyboardEvent } from 'react'
import type { WorkflowNodeType } from '../types'
import { useOverlay } from './overlay'

/** catalog `kind` 顯示字；未知值原樣顯示（比照 WorkflowNode 的 TRACE_STATUS_LABEL 慣例）。 */
const KIND_LABEL: Record<string, string> = { control: '流程控制', data: '資料' }

interface NodePickerProps {
  title: string
  /** 一律由呼叫端先過 `catalogForKind`（fail-closed），必要時再過相容性過濾。 */
  items: WorkflowNodeType[]
  x: number
  y: number
  onPick: (type: WorkflowNodeType) => void
  onClose: () => void
}

const optionId = (type: WorkflowNodeType) => `node-picker-${type.type}-${type.version}`

/**
 * 單一節點選擇器，服務五個入口：`Tab`、palette「新增節點」、拉線到空白處、
 * edge 上的「＋」中插、畫布右鍵選單。禁止複製出第二份實作。
 */
export default function NodePicker({ title, items, x, y, onPick, onClose }: NodePickerProps) {
  const [query, setQuery] = useState('')
  const [active, setActive] = useState(0)
  const { ref, style } = useOverlay<HTMLDivElement>(x, y, onClose)
  const listRef = useRef<HTMLDivElement>(null)

  const groups = useMemo(() => {
    const needle = query.trim().toLowerCase()
    const matched = items.filter((item) => !needle
      || item.title.toLowerCase().includes(needle) || item.type.toLowerCase().includes(needle))
    const byKind = new Map<string, WorkflowNodeType[]>()
    for (const item of matched) {
      const bucket = byKind.get(item.kind)
      if (bucket) bucket.push(item)
      else byKind.set(item.kind, [item])
    }
    return [...byKind.entries()]
  }, [items, query])
  const flat = useMemo(() => groups.flatMap(([, list]) => list), [groups])
  const index = flat.length === 0 ? -1 : Math.min(active, flat.length - 1)

  useEffect(() => {
    listRef.current?.querySelector('[aria-selected="true"]')?.scrollIntoView({ block: 'nearest' })
  }, [index])

  function onKeyDown(event: KeyboardEvent) {
    if (event.key === 'ArrowDown') { event.preventDefault(); setActive((i) => Math.min(i + 1, flat.length - 1)) }
    else if (event.key === 'ArrowUp') { event.preventDefault(); setActive((i) => Math.max(i - 1, 0)) }
    else if (event.key === 'Enter') { event.preventDefault(); if (index >= 0) onPick(flat[index]) }
    else if (event.key === 'Escape') { event.preventDefault(); onClose() }
    // 浮層是 modal：Tab 收斂回搜尋框（選項不可聚焦），焦點不得帶出仍開著的 picker；
    // 離開的方式是 Escape 或選一個型別。
    else if (event.key === 'Tab') { event.preventDefault() }
  }

  return (
    <div className="workflow-picker" ref={ref} style={style} role="dialog" aria-modal="true" aria-label={title}
      onKeyDown={onKeyDown}>
      <p className="workflow-picker__title">{title}</p>
      {/* 浮層開啟即接手輸入是 n8n 的既定體感；關閉時 useOverlay 會把焦點還給觸發元素。 */}
      <input
        className="input" autoFocus type="search" placeholder="搜尋節點（名稱或型別）"
        role="combobox" aria-expanded="true" aria-autocomplete="list"
        aria-label="搜尋節點" aria-controls="workflow-picker-list" aria-activedescendant={index >= 0 ? optionId(flat[index]) : undefined}
        value={query} onChange={(event) => { setQuery(event.target.value); setActive(0) }}
      />
      <div className="workflow-picker__list" id="workflow-picker-list" role="listbox" aria-label={title} ref={listRef}>
        {flat.length === 0 && <p className="muted">沒有可用的相容節點型別。</p>}
        {groups.map(([kind, list]) => (
          <div key={kind} role="group" aria-label={KIND_LABEL[kind] ?? kind}>
            <p className="workflow-picker__group">{KIND_LABEL[kind] ?? kind}</p>
            {list.map((item) => (
              <div
                key={optionId(item)} id={optionId(item)} role="option" aria-selected={flat[index] === item}
                className={`workflow-picker__option${flat[index] === item ? ' workflow-picker__option--active' : ''}`}
                onMouseEnter={() => setActive(flat.indexOf(item))}
                onClick={() => onPick(item)}
              >
                <strong>{item.title}</strong>
                <span className="muted">{item.type}@{item.version}</span>
              </div>
            ))}
          </div>
        ))}
      </div>
    </div>
  )
}
