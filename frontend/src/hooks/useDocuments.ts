import { useCallback, useEffect, useRef, useState } from 'react'
import { listDocuments, createDocument, deleteDocument } from '../api/documents'
import type { DocumentInfo } from '../types'

const POLL_MS = 2000
const POLL_MAX_MS = 60000

/**
 * 文件清單狀態 + 非同步處理的 UX 核心：
 * 建立回 202 → 樂觀插入一列 processing → 每 2 秒輪詢清單，
 * 直到沒有任何 processing 才停（上限 60 秒，逾時設 timedOut）。
 */
export function useDocuments() {
  const [docs, setDocs] = useState<DocumentInfo[]>([])
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [timedOut, setTimedOut] = useState(false)
  const pollRef = useRef<number | null>(null)
  const pollStartRef = useRef(0)
  // 最終一致性緩衝：202 回傳後、backend consumer 寫入 appdb 前，GET 清單還不含
  // 新文件。已 create 但尚未出現在伺服器清單的文件記在這裡，fetchList 合併回清單，
  // 避免樂觀插入的列被整包替換沖掉、輪詢又提早停止。
  const pendingRef = useRef<Map<string, DocumentInfo>>(new Map())

  const stopPolling = useCallback(() => {
    if (pollRef.current !== null) {
      clearInterval(pollRef.current)
      pollRef.current = null
    }
  }, [])

  const fetchList = useCallback(async (): Promise<DocumentInfo[]> => {
    const list = await listDocuments()
    // 已出現在伺服器清單的 id 從 pending 移除；剩下的（必不在 list 中）合併到前面。
    for (const d of list) pendingRef.current.delete(d.id)
    const merged = [...pendingRef.current.values(), ...list]
    setDocs(merged)
    return merged
  }, [])

  const startPolling = useCallback(() => {
    // 每次（重新）啟動都重置逾時視窗——第二次 create 時輪詢可能已在跑，
    // 逾時要從最新一份文件算起。
    pollStartRef.current = Date.now()
    setTimedOut(false)
    if (pollRef.current !== null) return // 已在輪詢，避免重複計時器
    pollRef.current = window.setInterval(async () => {
      try {
        const merged = await fetchList()
        // 尚未出現在伺服器清單的 pending 視同處理中：兩者皆空才停。
        if (
          !merged.some((d) => d.status === 'processing') &&
          pendingRef.current.size === 0
        ) {
          stopPolling()
        } else if (Date.now() - pollStartRef.current > POLL_MAX_MS) {
          stopPolling()
          setTimedOut(true)
        }
      } catch {
        // 輪詢失敗（如 401 已觸發登出）就停，不無限重試。
        stopPolling()
      }
    }, POLL_MS)
  }, [fetchList, stopPolling])

  const load = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      const list = await fetchList()
      if (list.some((d) => d.status === 'processing')) startPolling()
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setLoading(false)
    }
  }, [fetchList, startPolling])

  const create = useCallback(
    async (title: string, text: string): Promise<void> => {
      const created = await createDocument(title, text) // 202
      // 樂觀插入：POST 只回 {id,title,status}，其餘欄位補預設，等輪詢帶回真值。
      const optimistic: DocumentInfo = {
        id: created.id,
        title: created.title,
        status: created.status,
        chunk_count: 0,
        created_at: new Date().toISOString(),
      }
      pendingRef.current.set(optimistic.id, optimistic)
      setDocs((prev) => [optimistic, ...prev])
      startPolling()
    },
    [startPolling],
  )

  const remove = useCallback(async (id: string): Promise<void> => {
    await deleteDocument(id)
    pendingRef.current.delete(id)
    setDocs((prev) => prev.filter((d) => d.id !== id))
  }, [])

  useEffect(() => {
    load()
    return stopPolling // unmount 時清掉計時器
  }, [load, stopPolling])

  return { docs, loading, error, timedOut, create, remove }
}
