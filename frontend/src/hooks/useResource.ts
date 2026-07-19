import { useCallback, useEffect, useRef, useState } from 'react'

/**
 * 唯讀資源載入的共用狀態機:data / loading(初值 true) / error + reload。
 * fetcher 必須是穩定參照(模組層函式直接傳;需要參數/組合的用 useCallback 包),
 * 否則 reload 每次 render 都變、effect 會無限重跑。
 */
export function useResource<T>(fetcher: () => Promise<T>): {
  data: T | null
  loading: boolean
  error: string | null
  reload: () => Promise<void>
} {
  const [data, setData] = useState<T | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const mountedRef = useRef(false)
  const requestGenerationRef = useRef(0)

  useEffect(() => {
    mountedRef.current = true
    return () => {
      mountedRef.current = false
      requestGenerationRef.current += 1
    }
  }, [])

  const reload = useCallback(async () => {
    if (!mountedRef.current) return
    const generation = ++requestGenerationRef.current
    const isCurrent = () => mountedRef.current && generation === requestGenerationRef.current

    setLoading(true)
    setError(null)
    try {
      const nextData = await fetcher()
      if (isCurrent()) setData(nextData)
    } catch (e) {
      if (isCurrent()) setError((e as Error).message)
    } finally {
      if (isCurrent()) setLoading(false)
    }
  }, [fetcher])

  useEffect(() => {
    void reload()
  }, [reload])

  return { data, loading, error, reload }
}
