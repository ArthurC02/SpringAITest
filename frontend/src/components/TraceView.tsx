import type { TraceEntry } from '../types'
import { fmtDate } from '../format'

/**
 * 節點軌跡：資料直接取自 invoke 回應的 output.trace，不另外呼叫 API（AT4-15）。
 * 每列可展開看 input/output 鍵名摘要與 failure_codes；摘要只有鍵名沒有值，
 * 這是引擎的稽核設計（trace 不落內容），不要「補上」實際值。
 */
function traceOf(output: Record<string, unknown>): TraceEntry[] {
  const t = output.trace
  if (!Array.isArray(t)) return []
  return t.filter((e): e is TraceEntry => !!e && typeof e === 'object' && 'node_name' in e)
}

const CHIP: Record<string, string> = {
  ok: 'chip--ready',
  error: 'chip--failed',
  skipped: 'chip--skip',
}

function ms(v: number): string {
  return Number.isFinite(v) ? `${Math.round(v)}ms` : '—'
}

export default function TraceView({ output }: { output: Record<string, unknown> }) {
  const trace = traceOf(output)
  if (trace.length === 0) return null

  return (
    <div className="trace">
      <h4 className="trace__title">節點軌跡（trace）</h4>
      {trace.map((e, i) => (
        <details className="trace__row" key={`${e.node_name}-${i}`}>
          <summary className="trace__head">
            <span className="trace__node">{e.node_name}</span>
            <span className="trace__ms">{ms(e.latency_ms)}</span>
            <span className={`chip ${CHIP[e.status] ?? 'chip--skip'}`}>{e.status}</span>
            {e.error_code && <span className="trace__code">{e.error_code}</span>}
          </summary>
          <dl className="trace__detail">
            <dt>input（鍵名）</dt>
            <dd>{e.input_summary || '—'}</dd>
            <dt>output（鍵名）</dt>
            <dd>{e.output_summary || '—'}</dd>
            {e.failure_codes && e.failure_codes.length > 0 && (
              <>
                <dt>failure_codes</dt>
                <dd>{e.failure_codes.join('、')}</dd>
              </>
            )}
            {e.component_version && (
              <>
                <dt>版本</dt>
                <dd>{e.component_version}</dd>
              </>
            )}
            <dt>時間</dt>
            <dd className="muted">
              {fmtDate(e.start_time)} → {fmtDate(e.end_time)}
            </dd>
          </dl>
        </details>
      ))}
    </div>
  )
}
