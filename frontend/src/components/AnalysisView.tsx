import { useCallback, useEffect, useState } from 'react'
import { useCopilotReadable } from '@copilotkit/react-core'
import { getSummary } from '../api/analysis'
import type { AnalysisSummary } from '../types'

/** 分析視圖:兩張數字卡 + 最近文件條列 + 重新整理。 */
export default function AnalysisView() {
  const [summary, setSummary] = useState<AnalysisSummary | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)

  const load = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      setSummary(await getSummary())
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    load()
  }, [load])

  // 分析摘要餵給副駕:只在此視圖掛載期間有效(value 為 null 時 CopilotKit 會略過)。
  useCopilotReadable(
    { description: '租戶分析摘要（文件數 / 片段數 / 最近文件標題）', value: summary },
    [summary],
  )

  return (
    <div className="view">
      <div className="view__head">
        <h2 className="view__title">分析</h2>
        <button className="btn" onClick={load} disabled={loading}>
          {loading ? '載入中…' : '重新整理'}
        </button>
      </div>

      {error && <p className="error-text">{error}</p>}

      {summary && (
        <>
          <div className="cards">
            <div className="card">
              <div className="card__num">{summary.document_count}</div>
              <div className="card__label">文件數</div>
            </div>
            <div className="card">
              <div className="card__num">{summary.chunk_count}</div>
              <div className="card__label">片段數</div>
            </div>
          </div>

          <h3 style={{ marginTop: 24 }}>最近文件</h3>
          {summary.latest_titles.length === 0 ? (
            <p className="muted">尚無文件。</p>
          ) : (
            <ul>
              {summary.latest_titles.map((t, i) => (
                <li key={i}>{t}</li>
              ))}
            </ul>
          )}
        </>
      )}
    </div>
  )
}
