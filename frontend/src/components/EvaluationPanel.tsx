import { useEffect, useRef, useState } from 'react'
import {
  createEvalRun,
  diffEvalRuns,
  getEvalRun,
  getEvalSuite,
  listEvalRuns,
  listEvalSuites,
} from '../api/operations'
import { isNotFound } from '../api/http'
import { newIdempotencyKey } from '../api/agentRuns'
import { getSessionStorage, LogicalAttemptKey, OPERATIONS_ATTEMPT_STORAGE_PREFIX } from '../logicalAttemptKey'
import type { EvalCaseDelta, EvalCaseDeltaStatus, EvalRun, EvalSuite, EvalSuiteDetail } from '../types'
import { useResource } from '../hooks/useResource'
import { useConfirm } from './ConfirmDialog'
import { runWithToast, useToast } from './Toast'
import ErrorText from './ErrorText'
import Skeleton from './Skeleton'
import { fmtDate } from '../format'
import { Observed } from './Observed'

const EVAL_RUN_ATTEMPT_KEY = `${OPERATIONS_ATTEMPT_STORAGE_PREFIX}eval-run-idempotency`
// backend EvalController.MaxBudgetMs — model binding for `budget_ms:int?` rejects a
// non-integer/out-of-range value with an opaque 400, so reject it client-side first.
const MAX_BUDGET_MS = 300_000

interface EvalData {
  /** false = `RUN_EVAL_ENABLED` off(404 from every eval-suites/eval-runs proxy route). */
  enabled: boolean
  suites: EvalSuite[]
  runs: EvalRun[]
}

async function loadEvalData(): Promise<EvalData> {
  try {
    const [suites, runs] = await Promise.all([listEvalSuites(), listEvalRuns()])
    return { enabled: true, suites, runs }
  } catch (e) {
    if (isNotFound(e)) return { enabled: false, suites: [], runs: [] }
    throw e
  }
}

function VerdictBadge({ verdict }: { verdict: string }) {
  const cls = verdict === 'PASS' ? 'chip--ready' : verdict === 'FAIL' ? 'chip--failed' : 'chip--warn'
  return <span className={`chip ${cls}`}>{verdict || '未知'}</span>
}

const DELTA_LABEL: Record<EvalCaseDeltaStatus, { cls: string; label: string }> = {
  regressed: { cls: 'chip--failed', label: 'Regressed (PASS→FAIL)' },
  improved: { cls: 'chip--ready', label: 'Improved (FAIL→PASS)' },
  changed: { cls: 'chip--warn', label: 'Changed' },
  unchanged: { cls: 'chip--skip', label: 'Unchanged' },
  added: { cls: 'chip--warn', label: 'New case' },
  removed: { cls: 'chip--warn', label: 'Removed case' },
}

function DeltaBadge({ status }: { status: EvalCaseDeltaStatus }) {
  const { cls, label } = DELTA_LABEL[status]
  return <span className={`chip ${cls}`}>{label}</span>
}

/** Shared "expand to load" state machine for a drill-down row: fetch only fires on
 * open, short-circuits if already open/loading, and both row components below use it. */
function useLazyDetail<T>(fetcher: () => Promise<T>) {
  const [open, setOpen] = useState(false)
  const [detail, setDetail] = useState<T | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function toggle() {
    const next = !open
    setOpen(next)
    if (!next || detail || loading) return
    setLoading(true)
    setError(null)
    try {
      setDetail(await fetcher())
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setLoading(false)
    }
  }

  return { open, detail, loading, error, toggle }
}

/** SHA/case count only exist on the detail response — fetched lazily on expand (same
 * drill-down shape as `EvalRunRow` below), never eagerly for every suite in the list. */
function EvalSuiteRow({ suite }: { suite: EvalSuite }) {
  const {
    open,
    detail,
    loading: detailLoading,
    error: detailError,
    toggle,
  } = useLazyDetail<EvalSuiteDetail>(() => getEvalSuite(suite.suiteId))

  const current = detail?.revisions.find((r) => r.revision === detail.currentRevision) ?? null

  return (
    <>
      <tr>
        <td>
          <button className="btn" type="button" onClick={() => void toggle()}>
            {open ? '▼' : '▶'} {suite.suiteId}
          </button>
        </td>
        <td>r{suite.currentRevision}</td>
      </tr>
      {open && (
        <tr>
          <td colSpan={2}>
            {detailLoading && <Skeleton rows={2} />}
            <ErrorText msg={detailError} />
            {detail && (
              <dl className="agent-test-console__summary">
                <div>
                  <dt>SHA</dt>
                  <dd>
                    {current?.casesSha256 ? (
                      <code>{current.casesSha256.slice(0, 12)}</code>
                    ) : (
                      <Observed value={null} />
                    )}
                  </dd>
                </div>
                <div>
                  <dt>Cases</dt>
                  <dd>{current ? current.caseCount : <Observed value={null} />}</dd>
                </div>
              </dl>
            )}
          </td>
        </tr>
      )}
    </>
  )
}

function EvalSuiteTable({ suites }: { suites: EvalSuite[] }) {
  if (suites.length === 0) return <p className="muted">No eval suites recorded yet.</p>
  return (
    <div className="table-wrap">
      <table className="table">
        <thead>
          <tr>
            <th>Suite</th>
            <th>Current revision</th>
          </tr>
        </thead>
        <tbody>
          {suites.map((s) => (
            <EvalSuiteRow key={s.suiteId} suite={s} />
          ))}
        </tbody>
      </table>
    </div>
  )
}

function EvalRunRow({
  run,
  onUseForRegressionGate,
}: {
  run: EvalRun
  onUseForRegressionGate: (runId: string) => void
}) {
  const {
    open,
    detail,
    loading: detailLoading,
    error: detailError,
    toggle,
  } = useLazyDetail<EvalRun>(() => getEvalRun(run.id))

  return (
    <>
      <tr>
        <td>
          <button className="btn" type="button" onClick={() => void toggle()}>
            {open ? '▼' : '▶'} <code>{run.id.slice(0, 8)}</code>
          </button>
        </td>
        <td>
          {run.suiteId} r{run.suiteRevision}
        </td>
        <td>{run.startedAt ? fmtDate(run.startedAt) : <Observed value={null} />}</td>
        <td>{run.completedAt ? fmtDate(run.completedAt) : <Observed value={null} />}</td>
        <td>
          <span className="chip chip--ready">{run.passCount} PASS</span>{' '}
          <span className="chip chip--failed">{run.failCount} FAIL</span>{' '}
          <span className="chip chip--warn">{run.errorCount} ERROR</span>
        </td>
        <td>{run.runnerVersion ?? <Observed value={null} />}</td>
      </tr>
      {open && (
        <tr>
          <td colSpan={6}>
            {detailLoading && <Skeleton rows={2} />}
            <ErrorText msg={detailError} />
            {detail && (
              <>
                <button
                  className="btn btn--info"
                  type="button"
                  onClick={() => onUseForRegressionGate(run.id)}
                >
                  Use for regression gate
                </button>
                <div className="table-wrap">
                  <table className="table">
                    <thead>
                      <tr>
                        <th>Case</th>
                        <th>Verdict</th>
                        <th>Latency</th>
                        <th>Failure reason</th>
                        <th>Canonical identity</th>
                      </tr>
                    </thead>
                    <tbody>
                      {(detail.cases ?? []).map((c) => (
                        <tr key={c.caseId}>
                          <td>{c.caseId}</td>
                          <td><VerdictBadge verdict={c.verdict} /></td>
                          <td><Observed value={c.latencyMs} unit=" ms" /></td>
                          <td>{c.failureReason ?? '—'}</td>
                          <td>
                            {c.canonicalIdentity ? (
                              <code>{c.canonicalIdentity.slice(0, 12)}</code>
                            ) : (
                              <Observed value={null} />
                            )}
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              </>
            )}
          </td>
        </tr>
      )}
    </>
  )
}

function EvalRunTable({
  runs,
  onUseForRegressionGate,
}: {
  runs: EvalRun[]
  onUseForRegressionGate: (runId: string) => void
}) {
  if (runs.length === 0) return <p className="muted">No eval runs recorded yet.</p>
  return (
    <div className="table-wrap">
      <table className="table">
        <thead>
          <tr>
            <th>Run</th>
            <th>Suite / revision</th>
            <th>Started</th>
            <th>Completed</th>
            <th>Cases</th>
            <th>Runner version</th>
          </tr>
        </thead>
        <tbody>
          {runs.map((r) => (
            <EvalRunRow key={r.id} run={r} onUseForRegressionGate={onUseForRegressionGate} />
          ))}
        </tbody>
      </table>
    </div>
  )
}

function EvalTriggerForm({
  suites,
  onCreated,
}: {
  suites: EvalSuite[]
  onCreated: () => void
}) {
  const toast = useToast()
  const confirm = useConfirm()
  const [suiteId, setSuiteId] = useState('')
  const [candidateName, setCandidateName] = useState('')
  const [budgetMs, setBudgetMs] = useState('')
  const [busy, setBusy] = useState(false)
  const attempts = useRef(new LogicalAttemptKey(newIdempotencyKey, getSessionStorage(), EVAL_RUN_ATTEMPT_KEY))
  const selectedSuite = suites.find((s) => s.suiteId === suiteId) ?? null
  const trimmedBudget = budgetMs.trim()
  const parsedBudget = trimmedBudget ? Number(trimmedBudget) : undefined
  // "1.5" passes Number.isFinite but backend's `budget_ms:int?` model binding rejects it
  // with an opaque 400 — require a whole number within the backend MaxBudgetMs cap.
  const budgetInvalid =
    trimmedBudget.length > 0 &&
    (!Number.isInteger(parsedBudget) || (parsedBudget as number) <= 0 || (parsedBudget as number) > MAX_BUDGET_MS)

  async function submit() {
    const name = candidateName.trim()
    const revision = selectedSuite?.currentRevision ?? null
    if (busy || !selectedSuite || !revision || !name || budgetInvalid) return
    if (
      !(await confirm(`Run suite "${suiteId}" r${revision} against skill "${name}"?`, {
        confirmLabel: 'Run',
      }))
    )
      return
    const identity = [suiteId, revision, name, parsedBudget ?? null] as const
    const key = attempts.current.keyFor(identity)
    setBusy(true)
    await runWithToast(
      toast,
      () => createEvalRun(suiteId, revision, { kind: 'skill', ref: { name } }, key, parsedBudget),
      {
        success: 'Eval run completed.',
        onSuccess: () => {
          attempts.current.consume(identity, key)
          setCandidateName('')
          setBudgetMs('')
          onCreated()
        },
      },
    )
    setBusy(false)
  }

  return (
    <section className="agent-block" aria-busy={busy}>
      <h4>Trigger eval run</h4>
      <div className="field">
        <label htmlFor="eval-suite">Suite</label>
        <select
          id="eval-suite"
          value={suiteId}
          onChange={(e) => setSuiteId(e.target.value)}
          disabled={busy}
        >
          <option value="">Select a suite…</option>
          {suites.map((s) => (
            <option key={s.suiteId} value={s.suiteId}>
              {s.suiteId} (r{s.currentRevision})
            </option>
          ))}
        </select>
      </div>
      <div className="field">
        <label htmlFor="eval-candidate-name">Candidate skill name</label>
        <input
          id="eval-candidate-name"
          value={candidateName}
          onChange={(e) => setCandidateName(e.target.value)}
          disabled={busy}
        />
      </div>
      <div className="field">
        <label htmlFor="eval-budget">Budget (ms, optional)</label>
        <input
          id="eval-budget"
          type="number"
          min={1}
          max={MAX_BUDGET_MS}
          value={budgetMs}
          onChange={(e) => setBudgetMs(e.target.value)}
          disabled={busy}
          aria-invalid={budgetInvalid}
          aria-describedby={budgetInvalid ? 'eval-budget-err' : undefined}
        />
        {budgetInvalid && (
          <span className="field-error" id="eval-budget-err" role="alert">
            Budget (ms) must be a whole number between 1 and {MAX_BUDGET_MS}.
          </span>
        )}
      </div>
      <button
        className="btn btn--info"
        type="button"
        disabled={busy || !suiteId || !candidateName.trim() || budgetInvalid}
        onClick={() => void submit()}
      >
        Run eval
      </button>
    </section>
  )
}

function EvalDeltaCompare({ runs }: { runs: EvalRun[] }) {
  const [baselineId, setBaselineId] = useState('')
  const [candidateId, setCandidateId] = useState('')
  const [baseline, setBaseline] = useState<EvalRun | null>(null)
  const [candidate, setCandidate] = useState<EvalRun | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false
    if (!baselineId || !candidateId) {
      setBaseline(null)
      setCandidate(null)
      // A request may already be in flight (select changed back to empty mid-fetch) —
      // clear its loading/error too, or the Skeleton would be stuck showing forever.
      setLoading(false)
      setError(null)
      return
    }
    setLoading(true)
    setError(null)
    Promise.all([getEvalRun(baselineId), getEvalRun(candidateId)])
      .then(([b, c]) => {
        if (!cancelled) {
          setBaseline(b)
          setCandidate(c)
        }
      })
      .catch((e: unknown) => {
        if (!cancelled) setError((e as Error).message)
      })
      .finally(() => {
        if (!cancelled) setLoading(false)
      })
    return () => {
      cancelled = true
    }
  }, [baselineId, candidateId])

  const delta: EvalCaseDelta[] | null = baseline && candidate ? diffEvalRuns(baseline, candidate) : null

  return (
    <section className="agent-block">
      <h4>Baseline / candidate delta</h4>
      <p className="muted">Client-side per-case verdict comparison only — no server-side gate is affected.</p>
      <div className="field">
        <label htmlFor="eval-baseline-run">Baseline run</label>
        <select id="eval-baseline-run" value={baselineId} onChange={(e) => setBaselineId(e.target.value)}>
          <option value="">Select…</option>
          {runs.map((r) => (
            <option key={r.id} value={r.id}>
              {r.id.slice(0, 8)} · {r.suiteId} r{r.suiteRevision}
            </option>
          ))}
        </select>
      </div>
      <div className="field">
        <label htmlFor="eval-candidate-run">Candidate run</label>
        <select id="eval-candidate-run" value={candidateId} onChange={(e) => setCandidateId(e.target.value)}>
          <option value="">Select…</option>
          {runs.map((r) => (
            <option key={r.id} value={r.id}>
              {r.id.slice(0, 8)} · {r.suiteId} r{r.suiteRevision}
            </option>
          ))}
        </select>
      </div>
      {loading && <Skeleton rows={2} />}
      <ErrorText msg={error} />
      {baseline && candidate && baseline.suiteId !== candidate.suiteId && (
        <p className="muted">
          Selected runs are from different suites ({baseline.suiteId} vs {candidate.suiteId}); case IDs
          may not correspond.
        </p>
      )}
      {delta && (
        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr>
                <th>Case</th>
                <th>Baseline</th>
                <th>Candidate</th>
                <th>Change</th>
              </tr>
            </thead>
            <tbody>
              {delta.map((d) => (
                <tr key={d.caseId}>
                  <td>{d.caseId}</td>
                  <td>{d.baselineVerdict ?? '—'}</td>
                  <td>{d.candidateVerdict ?? '—'}</td>
                  <td><DeltaBadge status={d.status} /></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  )
}

/**
 * E4 Evaluation cockpit(規格 §7):eval-suites/eval-runs 唯讀瀏覽 + 觸發 run + baseline/candidate
 * delta(純前端比對)。`RUN_EVAL_ENABLED` 關閉時整段 proxy 回 404 —— 優雅顯示「未啟用」空狀態,
 * 不當成錯誤噴 toast(讀取失敗一律不觸發 toast,只有寫入動作才用 runWithToast)。
 */
export default function EvaluationPanel({
  onUseForRegressionGate,
}: {
  onUseForRegressionGate: (runId: string) => void
}) {
  const { data, loading, error, reload } = useResource(loadEvalData)

  return (
    <section className="agent-block">
      <h3>Evaluation</h3>
      <ErrorText msg={error} />
      {loading && !data ? (
        <Skeleton rows={4} />
      ) : !data ? null : !data.enabled ? (
        <p className="muted">Evaluation is not enabled for this tenant (RUN_EVAL_ENABLED off).</p>
      ) : (
        <>
          <EvalSuiteTable suites={data.suites} />
          <EvalTriggerForm suites={data.suites} onCreated={() => void reload()} />
          <h4>Runs</h4>
          <EvalRunTable runs={data.runs} onUseForRegressionGate={onUseForRegressionGate} />
          <EvalDeltaCompare runs={data.runs} />
        </>
      )}
    </section>
  )
}
