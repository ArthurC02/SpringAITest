import { useCallback, useEffect, useRef, useState } from 'react'
import { listDocuments, createDocument, deleteDocument } from '../api/documents'
import type { DocumentInfo } from '../types'

const POLL_MS = 2000
const POLL_MAX_MS = 60000

/** Keeps optimistic 202 rows visible while the backend processes them. */
export function useDocuments() {
  const [docs, setDocs] = useState<DocumentInfo[]>([])
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [timedOut, setTimedOut] = useState(false)
  const pollRef = useRef<number | null>(null)
  const pollGenerationRef = useRef(0)
  const lifecycleGenerationRef = useRef(0)
  const pollStartRef = useRef(0)
  const pendingRef = useRef<Map<string, DocumentInfo>>(new Map())
  const deletedRef = useRef<Set<string>>(new Set())

  const stopPolling = useCallback(() => {
    pollGenerationRef.current += 1
    if (pollRef.current !== null && pollRef.current >= 0) {
      clearTimeout(pollRef.current)
    }
    pollRef.current = null
  }, [])

  const fetchList = useCallback(async (lifecycleGeneration: number): Promise<DocumentInfo[]> => {
    const list = (await listDocuments()).filter((document) => !deletedRef.current.has(document.id))
    if (lifecycleGenerationRef.current !== lifecycleGeneration) return []
    for (const document of list) pendingRef.current.delete(document.id)
    const merged = [...pendingRef.current.values(), ...list]
    setDocs(merged)
    return merged
  }, [])

  const startPolling = useCallback((lifecycleGeneration = lifecycleGenerationRef.current) => {
    if (lifecycleGenerationRef.current !== lifecycleGeneration) return
    pollStartRef.current = Date.now()
    setTimedOut(false)
    if (pollRef.current !== null) return
    const generation = ++pollGenerationRef.current

    const poll = async () => {
      pollRef.current = -1
      try {
        const merged = await fetchList(lifecycleGeneration)
        if (pollGenerationRef.current !== generation
            || lifecycleGenerationRef.current !== lifecycleGeneration) return
        pollRef.current = null
        if (!merged.some((document) => document.status === 'processing') && pendingRef.current.size === 0) return
        if (Date.now() - pollStartRef.current > POLL_MAX_MS) {
          setTimedOut(true)
          return
        }
        pollRef.current = window.setTimeout(poll, POLL_MS)
      } catch {
        // apiFetch owns global 401 handling; another load/create can start a fresh cycle.
        if (pollGenerationRef.current === generation) pollRef.current = null
      }
    }

    pollRef.current = window.setTimeout(poll, POLL_MS)
  }, [fetchList])

  const load = useCallback(async (lifecycleGeneration: number) => {
    setLoading(true)
    setError(null)
    try {
      const list = await fetchList(lifecycleGeneration)
      if (lifecycleGenerationRef.current === lifecycleGeneration
          && list.some((document) => document.status === 'processing')) {
        startPolling(lifecycleGeneration)
      }
    } catch (cause) {
      if (lifecycleGenerationRef.current === lifecycleGeneration) {
        setError((cause as Error).message)
      }
    } finally {
      if (lifecycleGenerationRef.current === lifecycleGeneration) setLoading(false)
    }
  }, [fetchList, startPolling])

  const create = useCallback(
    async (title: string, text: string, idempotencyKey: string = crypto.randomUUID()): Promise<void> => {
      const lifecycleGeneration = lifecycleGenerationRef.current
      const created = await createDocument(title, text, idempotencyKey)
      if (lifecycleGenerationRef.current !== lifecycleGeneration) return
      const optimistic: DocumentInfo = {
        id: created.id,
        title: created.title,
        status: created.status,
        chunk_count: 0,
        created_at: new Date().toISOString(),
      }
      deletedRef.current.delete(optimistic.id)
      pendingRef.current.set(optimistic.id, optimistic)
      setDocs((previous) => [optimistic, ...previous.filter((document) => document.id !== optimistic.id)])
      startPolling()
    },
    [startPolling],
  )

  const remove = useCallback(async (id: string): Promise<void> => {
    const lifecycleGeneration = lifecycleGenerationRef.current
    await deleteDocument(id)
    deletedRef.current.add(id)
    pendingRef.current.delete(id)
    if (lifecycleGenerationRef.current === lifecycleGeneration) {
      setDocs((previous) => previous.filter((document) => document.id !== id))
    }
  }, [])

  useEffect(() => {
    const lifecycleGeneration = ++lifecycleGenerationRef.current
    load(lifecycleGeneration)
    return () => {
      if (lifecycleGenerationRef.current === lifecycleGeneration) {
        lifecycleGenerationRef.current += 1
      }
      stopPolling()
    }
  }, [load, stopPolling])

  return { docs, loading, error, timedOut, create, remove }
}
