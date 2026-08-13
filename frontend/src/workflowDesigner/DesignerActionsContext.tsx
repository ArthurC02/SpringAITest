import { createContext, useContext } from 'react'

/**
 * 節點/連線元件要用的動作。**不放進 node `data`**——放 data 會造成 stale closure，
 * 而且每次動作變動都讓整張圖重繪。`data` 只留投影用的純資料。
 */
export interface DesignerActions {
  disabled: boolean
  focusInspector: (nodeId: string) => void
  duplicateNode: (nodeId: string) => void
  deleteNode: (nodeId: string) => void
  insertNodeOnEdge: (edgeId: string, at: { x: number; y: number }) => void
  deleteEdge: (edgeId: string) => void
}

/** provider 之外一律 fail-closed：唯讀且所有動作皆為 no-op。 */
const LOCKED: DesignerActions = {
  disabled: true,
  focusInspector: () => {},
  duplicateNode: () => {},
  deleteNode: () => {},
  insertNodeOnEdge: () => {},
  deleteEdge: () => {},
}

export const DesignerActionsContext = createContext<DesignerActions>(LOCKED)

export const useDesignerActions = () => useContext(DesignerActionsContext)
