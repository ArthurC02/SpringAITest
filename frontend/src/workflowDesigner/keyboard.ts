const TEXT_ENTRY_TAGS = new Set(['INPUT', 'TEXTAREA', 'SELECT'])

/**
 * 快捷鍵焦點守衛：焦點在輸入元素/contenteditable/對話框內時，畫布快捷鍵一律不觸發
 * （否則在 inspector 欄位打字按 Ctrl+Z 會誤觸畫布 undo，Backspace 會刪節點）。
 */
export function isTextEntryTarget(target: EventTarget | null): boolean {
  const element = target as Element | null
  if (!element || element.nodeType !== 1) return false
  return TEXT_ENTRY_TAGS.has(element.nodeName)
    || (element as HTMLElement).isContentEditable
    || !!element.closest('dialog, [contenteditable="true"]')
}
