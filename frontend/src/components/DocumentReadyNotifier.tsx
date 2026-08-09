import { useEffect, useRef } from 'react'
import type { DocumentInfo } from '../types'
import { useToast } from './Toast'

export interface DocumentSettledEvent {
  key: string
  doc: DocumentInfo
}

interface Props {
  events: DocumentSettledEvent[]
}

/**
 * 文件轉為 ready/failed 時跳 toast（WS1-b）。刻意是 ToastProvider 的常駐子孫元件、
 * 掛在視圖切換不會卸載的位置（見 AppShell），這樣使用者不論停留在哪個視圖都能看到通知，
 * 而不是只有留在文件頁才會跳。轉態偵測本身在 useDocuments 內完成，這裡只負責把
 * 已發生的事件轉成 toast（不重新輪詢、不重覆判斷狀態）。
 */
export default function DocumentReadyNotifier({ events }: Props) {
  const toast = useToast()
  const seenRef = useRef<Set<string>>(new Set())

  useEffect(() => {
    for (const event of events) {
      if (seenRef.current.has(event.key)) continue
      seenRef.current.add(event.key)
      if (event.doc.status === 'failed') {
        toast(`文件「${event.doc.title}」處理失敗`, 'error')
      } else if (event.doc.status === 'ready') {
        toast(`文件「${event.doc.title}」已就緒，可以開始提問了`, 'success')
      }
    }
  }, [events, toast])

  return null
}
