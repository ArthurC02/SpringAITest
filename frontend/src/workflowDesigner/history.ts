import { useCallback, useState } from 'react'
import type { WorkflowDraft, WorkflowUiMetadata } from '../types'

/**
 * D4 草稿的 undo/redo 歷史。快照 = `{definition, ui_metadata}` 的**參考**（immutable 狀態，
 * 不深拷貝）；純 viewport 變更（平移/縮放）只換 present、不進 past，否則捲一下滑鼠就把歷史灌爆。
 *
 * ponytail: 上限 50 筆、以整份快照存放（不是 patch），超過丟最舊；未持久化，重整即重來。
 * 需要更長/可跨重整的歷史時，改成差異式 patch + IndexedDB 持久化。
 */
export const HISTORY_LIMIT = 50

export interface DraftHistory {
  past: WorkflowDraft[]
  present: WorkflowDraft
  future: WorkflowDraft[]
}

/** ponytail: 以 JSON.stringify 當結構比較——本檔所有寫入路徑都用 spread 保持鍵順序，
 * 故鍵序差異造成的偽陽性（多存一筆歷史）在此不會發生；真要嚴謹得換 deep-equal。 */
const sameJson = (a: unknown, b: unknown) => JSON.stringify(a) === JSON.stringify(b)

/** ui_metadata 只有 positions/viewport/groups/collapsed 四個欄位（D4 契約），此處明列非 viewport 的三個。 */
const withoutViewport = (metadata: WorkflowUiMetadata) => ({
  positions: metadata.positions, groups: metadata.groups, collapsed: metadata.collapsed,
})

export function isSameDraft(a: WorkflowDraft, b: WorkflowDraft): boolean {
  return sameJson(a.definition, b.definition) && sameJson(a.ui_metadata, b.ui_metadata)
}

/** 只有 viewport 不同 = 平移/縮放，不可被 undo。 */
export function isViewportOnlyChange(a: WorkflowDraft, b: WorkflowDraft): boolean {
  return !isSameDraft(a, b)
    && sameJson(a.definition, b.definition)
    && sameJson(withoutViewport(a.ui_metadata), withoutViewport(b.ui_metadata))
}

export function historyReset(present: WorkflowDraft): DraftHistory {
  return { past: [], present, future: [] }
}

/** `coalesce`：與上一筆合併成同一筆歷史（連續方向鍵微調用），仍算一次真實編輯故清空 future。
 * future 非空 = 剛 undo 過，此時不得合併：合併會同時吃掉 redo 分支又不留 undo 點，
 * 使用者「微調 → Ctrl+Z → 微調」後再按 Ctrl+Z 會一次跳過兩步。 */
export function historyPush(state: DraftHistory, next: WorkflowDraft, coalesce = false): DraftHistory {
  if (isSameDraft(state.present, next)) return state
  if (isViewportOnlyChange(state.present, next)) return { ...state, present: next }
  if (coalesce && state.past.length > 0 && state.future.length === 0) return { ...state, present: next, future: [] }
  return { past: [...state.past, state.present].slice(-HISTORY_LIMIT), present: next, future: [] }
}

export function historyUndo(state: DraftHistory): DraftHistory {
  if (state.past.length === 0) return state
  return {
    past: state.past.slice(0, -1),
    present: state.past[state.past.length - 1],
    future: [state.present, ...state.future].slice(0, HISTORY_LIMIT),
  }
}

export function historyRedo(state: DraftHistory): DraftHistory {
  if (state.future.length === 0) return state
  return {
    past: [...state.past, state.present].slice(-HISTORY_LIMIT),
    present: state.future[0],
    future: state.future.slice(1),
  }
}

/** 草稿真相 + 歷史；`reset` 用於重新載入草稿（新基準線，歷史清空）。 */
export function useDraftHistory() {
  const [state, setState] = useState<DraftHistory | null>(null)
  return {
    draft: state?.present ?? null,
    canUndo: !!state && state.past.length > 0,
    canRedo: !!state && state.future.length > 0,
    reset: useCallback((draft: WorkflowDraft) => setState(historyReset(draft)), []),
    commit: useCallback(
      (draft: WorkflowDraft, coalesce = false) =>
        setState((prev) => prev ? historyPush(prev, draft, coalesce) : historyReset(draft)),
      [],
    ),
    undo: useCallback(() => setState((prev) => prev ? historyUndo(prev) : prev), []),
    redo: useCallback(() => setState((prev) => prev ? historyRedo(prev) : prev), []),
  }
}
