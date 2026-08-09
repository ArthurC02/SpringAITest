import { useEffect, useRef, useState } from 'react'
import {
  createEvalRun,
  diffEvalRuns,
  getEvalRun,
  getEvalSuite,
  listEvalRuns,
  listEvalSuites,
} from '../api/operations'
import { listSkillCatalog } from '../api/skills'
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
import CatalogPicker from './CatalogPicker'

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
  regressed: { cls: 'chip--failed', label: '退步(通過→失敗)' },
  improved: { cls: 'chip--ready', label: '進步(失敗→通過)' },
  changed: { cls: 'chip--warn', label: '有變化' },
  unchanged: { cls: 'chip--skip', label: '無變化' },
  added: { cls: 'chip--warn', label: '新案例' },
  removed: { cls: 'chip--warn', label: '已移除案例' },
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
                  <dt>案例數</dt>
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
  if (suites.length === 0) return <p className="muted">尚無評測組合紀錄。</p>
  return (
    <div className="table-wrap">
      <table className="table">
        <thead>
          <tr>
            <th>組合</th>
            <th>目前版本</th>
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
          <span className="chip chip--ready">{run.passCount} 通過</span>{' '}
          <span className="chip chip--failed">{run.failCount} 失敗</span>{' '}
          <span className="chip chip--warn">{run.errorCount} 錯誤</span>
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
                  套用到品質迴歸關卡
                </button>
                <div className="table-wrap">
                  <table className="table">
                    <thead>
                      <tr>
                        <th>案例</th>
                        <th>判定</th>
                        <th>延遲</th>
                        <th>失敗原因</th>
                        <th>正規化身分</th>
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
  if (runs.length === 0) return <p className="muted">尚無評測執行紀錄。</p>
  return (
    <div className="table-wrap">
      <table className="table">
        <thead>
          <tr>
            <th>執行</th>
            <th>組合 / 版本</th>
            <th>開始時間</th>
            <th>完成時間</th>
            <th>案例</th>
            <th>執行器版本</th>
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
  // W5(規格 §5.1):candidate skill name 手輸改下拉,資料源沿用 AgentEditor 已用過的
  // listSkillCatalog();目錄載入失敗或為空,CatalogPicker 自動優雅退回手動輸入。
  const skillsRes = useResource(listSkillCatalog)
  // 懶初始化:useRef(new X()) 每次 render 都會建構(並讀 sessionStorage),只有第一顆會被留下。
  const attemptRef = useRef<LogicalAttemptKey | null>(null)
  const attempts = (attemptRef.current ??= new LogicalAttemptKey(
    newIdempotencyKey,
    getSessionStorage(),
    EVAL_RUN_ATTEMPT_KEY,
  ))
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
      !(await confirm(`對 Skill「${name}」執行組合「${suiteId}」r${revision}?`, {
        confirmLabel: '執行',
      }))
    )
      return
    const identity = [suiteId, revision, name, parsedBudget ?? null] as const
    const key = attempts.keyFor(identity)
    setBusy(true)
    await runWithToast(
      toast,
      () => createEvalRun(suiteId, revision, { kind: 'skill', ref: { name } }, key, parsedBudget),
      {
        success: '評測執行已完成。',
        onSuccess: () => {
          attempts.consume(identity, key)
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
      <h4>觸發評測執行</h4>
      <div className="field">
        <label htmlFor="eval-suite">組合</label>
        <select
          id="eval-suite"
          value={suiteId}
          onChange={(e) => setSuiteId(e.target.value)}
          disabled={busy}
        >
          <option value="">請選擇組合…</option>
          {suites.map((s) => (
            <option key={s.suiteId} value={s.suiteId}>
              {s.suiteId} (r{s.currentRevision})
            </option>
          ))}
        </select>
      </div>
      <CatalogPicker
        id="eval-candidate-name"
        label="候選 Skill 名稱"
        value={candidateName}
        onChange={setCandidateName}
        disabled={busy}
        items={skillsRes.data}
        itemsError={skillsRes.error}
        itemsLoading={skillsRes.loading}
        optionValue={(s) => s.name}
        optionLabel={(s) => `${s.name}${s.revision != null ? ` (r${s.revision})` : ''}`}
      />
      <div className="field">
        <label htmlFor="eval-budget">預算(毫秒,選填)</label>
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
            預算(毫秒)必須是 1 到 {MAX_BUDGET_MS} 之間的整數。
          </span>
        )}
      </div>
      <button
        className="btn btn--info"
        type="button"
        disabled={busy || !suiteId || !candidateName.trim() || budgetInvalid}
        onClick={() => void submit()}
      >
        執行評測
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
      <h4>基準 / 候選差異比較</h4>
      <p className="muted">僅前端逐案例判定比對,不影響任何伺服器端關卡。</p>
      <div className="field">
        <label htmlFor="eval-baseline-run">基準執行</label>
        <select id="eval-baseline-run" value={baselineId} onChange={(e) => setBaselineId(e.target.value)}>
          <option value="">請選擇…</option>
          {runs.map((r) => (
            <option key={r.id} value={r.id}>
              {r.id.slice(0, 8)} · {r.suiteId} r{r.suiteRevision}
            </option>
          ))}
        </select>
      </div>
      <div className="field">
        <label htmlFor="eval-candidate-run">候選執行</label>
        <select id="eval-candidate-run" value={candidateId} onChange={(e) => setCandidateId(e.target.value)}>
          <option value="">請選擇…</option>
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
          所選執行來自不同組合({baseline.suiteId} 對 {candidate.suiteId}),案例 ID 可能無法對應。
        </p>
      )}
      {delta && (
        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr>
                <th>案例</th>
                <th>基準</th>
                <th>候選</th>
                <th>變化</th>
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
      <h3>評測</h3>
      <ErrorText msg={error} />
      {loading && !data ? (
        <Skeleton rows={4} />
      ) : !data ? null : !data.enabled ? (
        <p className="muted">此系統目前未開放評測功能。</p>
      ) : (
        <>
          <EvalSuiteTable suites={data.suites} />
          <EvalTriggerForm suites={data.suites} onCreated={() => void reload()} />
          <h4>執行紀錄</h4>
          <EvalRunTable runs={data.runs} onUseForRegressionGate={onUseForRegressionGate} />
          <EvalDeltaCompare runs={data.runs} />
        </>
      )}
    </section>
  )
}
