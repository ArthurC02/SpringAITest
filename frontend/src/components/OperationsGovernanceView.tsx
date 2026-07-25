import { useEffect, useState } from 'react'
import { getLegacyInventory, getOperationsMetrics, getVersionComparison } from '../api/operations'

/** D7 read-only redacted operations view; rollout writes remain explicit server-governed APIs. */
export default function OperationsGovernanceView() {
  const [data, setData] = useState<Record<string, unknown> | null>(null)
  const [inventory, setInventory] = useState<unknown[]>([])
  const [error, setError] = useState<string | null>(null)
  useEffect(() => { void Promise.all([getOperationsMetrics(), getVersionComparison(), getLegacyInventory()]).then(([metrics, comparison, items]) => { setData({ metrics, comparison }); setInventory(items) }).catch((e: Error) => setError(e.message)) }, [])
  if (error) return <p className="field-error" role="alert">{error}</p>
  return <section className="agent-block"><h2>Operations governance</h2><p className="muted">Aggregate, redacted release data only.</p><pre>{data ? JSON.stringify(data, null, 2) : 'Loading…'}</pre><h3>Legacy inventory</h3><pre>{JSON.stringify(inventory, null, 2)}</pre></section>
}
