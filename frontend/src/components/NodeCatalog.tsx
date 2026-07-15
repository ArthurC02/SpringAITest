import { useEffect, useMemo, useState } from 'react'
import { listNodes } from '../api/nodes'
import type { NodeInfo } from '../types'
import Skeleton from './Skeleton'

/**
 * 節點目錄（GET /api/nodes）。一個元件兩個用途：
 * - Tab 3：唯讀契約瀏覽（不給 onInsert）。
 * - Skill 編輯器左欄：點擊插入 YAML 樣板（給 onInsert）。
 */
export default function NodeCatalog({ onInsert }: { onInsert?: (n: NodeInfo) => void }) {
  const [nodes, setNodes] = useState<NodeInfo[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [q, setQ] = useState('')

  useEffect(() => {
    listNodes()
      .then(setNodes)
      .catch((e) => setError((e as Error).message))
      .finally(() => setLoading(false))
  }, [])

  const shown = useMemo(() => {
    const needle = q.trim().toLowerCase()
    if (!needle) return nodes
    return nodes.filter(
      (n) =>
        n.name.toLowerCase().includes(needle) ||
        n.description.toLowerCase().includes(needle),
    )
  }, [nodes, q])

  return (
    <div className="nodecat">
      <input
        className="input"
        type="search"
        value={q}
        onChange={(e) => setQ(e.target.value)}
        placeholder="搜尋節點…"
        aria-label="搜尋節點"
      />

      {error && (
        <p className="error-text" role="alert">
          {error}
        </p>
      )}

      {loading ? (
        <Skeleton rows={4} />
      ) : shown.length === 0 && !error ? (
        <p className="muted">{nodes.length === 0 ? '尚無節點。' : '沒有符合的節點。'}</p>
      ) : (
        shown.map((n) => (
          <details className="nodecat__item" key={`${n.name}@${n.version}`}>
            <summary className="nodecat__head">
              <span className="nodecat__name">{n.name}</span>
              <span className="badge badge--user">v{n.version}</span>
            </summary>
            <p className="nodecat__desc">{n.description}</p>
            <dl className="nodecat__io">
              <dt>reads</dt>
              <dd>{n.reads.join('、') || '—'}</dd>
              <dt>writes</dt>
              <dd>{n.writes.join('、') || '—'}</dd>
              <dt>tools</dt>
              <dd>{n.requires_tools.join('、') || '—'}</dd>
            </dl>
            {onInsert && (
              <button className="btn" type="button" onClick={() => onInsert(n)}>
                插入到 YAML
              </button>
            )}
          </details>
        ))
      )}
    </div>
  )
}
