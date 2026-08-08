import { useRef, useState, type ReactNode } from 'react'
import {
  applyRollout,
  getLegacyInventory,
  getOperationsMetrics,
  getVersionComparison,
  overrideRegression,
  recordRegression,
  sumOrUnknown,
} from '../api/operations'
import { newIdempotencyKey } from '../api/agentRuns'
import { getSessionStorage, LogicalAttemptKey, OPERATIONS_ATTEMPT_STORAGE_PREFIX } from '../logicalAttemptKey'
import type {
  OperationsAgentMetric,
  OperationsLegacyInventoryItem,
  OperationsMetrics,
  OperationsNodeMetric,
  OperationsReleaseGate,
  OperationsSkillMetric,
  OperationsToolMetric,
  OperationsVersionComparison,
} from '../types'
import { useResource } from '../hooks/useResource'
import { useConfirm } from './ConfirmDialog'
import { runWithToast, useToast } from './Toast'
import ErrorText from './ErrorText'
import Skeleton from './Skeleton'
import { Observed } from './Observed'
import EvaluationPanel from './EvaluationPanel'

const OVERRIDE_ATTEMPT_KEY = `${OPERATIONS_ATTEMPT_STORAGE_PREFIX}override-idempotency`

/** Backend binds Orchestrator ID and eval run ID as a Guid (see `RolloutRequest.OrchestratorId`,
 * `RegressionRequest.EvalRunId`) — a non-GUID value fails ASP.NET model binding with an opaque
 * error, so reject it client-side first. */
const GUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

interface OperationsData {
  metrics: OperationsMetrics
  comparison: OperationsVersionComparison
  inventory: OperationsLegacyInventoryItem[]
}

async function loadOperations(): Promise<OperationsData> {
  const [metrics, comparison, inventory] = await Promise.all([
    getOperationsMetrics(),
    getVersionComparison(),
    getLegacyInventory(),
  ])
  return { metrics, comparison, inventory }
}

function SummaryCards({ metrics }: { metrics: OperationsMetrics }) {
  const gate = metrics.releaseGate
  const totalUsage = sumOrUnknown(metrics.agents.map((a) => a.observedUsageUnits))
  const totalCost = sumOrUnknown(metrics.agents.map((a) => a.observedCostUnits))
  return (
    <div className="cards">
      <div className="card">
        <div className="card__num">{metrics.rootRuns}</div>
        <div className="card__label">
          Root runs · {metrics.childRuns} child ({metrics.childSuccess} succeeded)
        </div>
      </div>
      <div className="card">
        <div className="card__num">
          {metrics.aggregation.completed} / {metrics.aggregation.partialOrFailed}
        </div>
        <div className="card__label">Completed / partial-or-failed roots</div>
      </div>
      <div className="card">
        <div className="card__num">{metrics.aggregation.averageLatencyMs} ms</div>
        <div className="card__label">Average root latency</div>
      </div>
      <div className="card">
        <div className="card__num">{totalUsage === null ? <Observed value={null} /> : totalUsage}</div>
        <div className="card__label">Observed usage units (agents)</div>
      </div>
      <div className="card">
        <div className="card__num">{totalCost === null ? <Observed value={null} /> : totalCost}</div>
        <div className="card__label">Observed cost units (agents)</div>
      </div>
      <div className="card">
        <div className="card__num">{gate.regressionPassed ? 'PASS' : 'FAIL'}</div>
        <div className="card__label">
          Regression gate{gate.overrideActive ? ' · override active' : ''} · {gate.auditEntries} audit
          entries
        </div>
      </div>
    </div>
  )
}

/** 本檔四張指標表的共用外殼(僅此檔:欄位語彙、空狀態文案都是 Operations 專屬)。 */
function MetricsTable<T>({
  rows,
  empty,
  rowKey,
  columns,
}: {
  rows: T[]
  empty: string
  rowKey: (row: T) => string
  columns: [string, (row: T) => ReactNode][]
}) {
  if (rows.length === 0) return <p className="muted">{empty}</p>
  return (
    <div className="table-wrap">
      <table className="table">
        <thead>
          <tr>
            {columns.map(([label]) => (
              <th key={label}>{label}</th>
            ))}
          </tr>
        </thead>
        <tbody>
          {rows.map((row) => (
            <tr key={rowKey(row)}>
              {columns.map(([label, render]) => (
                <td key={label}>{render(row)}</td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

function AgentTable({ rows }: { rows: OperationsAgentMetric[] }) {
  return (
    <MetricsTable
      rows={rows}
      empty="No Agent runs recorded yet."
      rowKey={(a) => `${a.agentId}:${a.revision}`}
      columns={[
        ['Agent', (a) => a.agentId],
        ['Rev', (a) => `r${a.revision}`],
        ['Runs', (a) => a.runs],
        ['Completed', (a) => a.completed],
        ['Failed', (a) => a.failed],
        ['Avg latency', (a) => `${a.averageLatencyMs} ms`],
        ['Reserved budget', (a) => a.reservedBudgetUnits],
        ['Observed usage', (a) => <Observed value={a.observedUsageUnits} />],
        ['Observed cost', (a) => <Observed value={a.observedCostUnits} />],
        ['Observed latency', (a) => <Observed value={a.observedLatencyMs} unit=" ms" />],
      ]}
    />
  )
}

function SkillTable({ rows }: { rows: OperationsSkillMetric[] }) {
  return (
    <MetricsTable
      rows={rows}
      empty="No Skill telemetry recorded yet."
      rowKey={(s) => `${s.name}:${s.revision}`}
      columns={[
        ['Skill', (s) => s.name],
        ['Rev', (s) => `r${s.revision}`],
        ['Runs', (s) => s.runs],
        ['Observed latency', (s) => <Observed value={s.observedLatencyMs} unit=" ms" />],
        ['Observed usage', (s) => <Observed value={s.observedUsageUnits} />],
        ['Observed cost', (s) => <Observed value={s.observedCostUnits} />],
        ['Reserved budget', (s) => s.reservedBudgetUnits],
      ]}
    />
  )
}

function ToolTable({ rows }: { rows: OperationsToolMetric[] }) {
  return (
    <MetricsTable
      rows={rows}
      empty="No tool telemetry recorded yet."
      rowKey={(t) => t.kind}
      columns={[
        ['Tool', (t) => t.kind],
        ['Count', (t) => t.count],
        ['Observed latency', (t) => <Observed value={t.observedLatencyMs} unit=" ms" />],
        ['Observed usage', (t) => <Observed value={t.observedUsageUnits} />],
        ['Observed cost', (t) => <Observed value={t.observedCostUnits} />],
        ['Reserved budget', (t) => t.reservedBudgetUnits],
      ]}
    />
  )
}

function NodeTable({ rows }: { rows: OperationsNodeMetric[] }) {
  return (
    <MetricsTable
      rows={rows}
      empty="No node telemetry recorded yet."
      rowKey={(n) => n.nodeId}
      columns={[
        ['Node', (n) => n.nodeId],
        ['Executions', (n) => n.executions],
        ['Avg latency', (n) => `${n.averageLatencyMs} ms`],
        ['Max latency', (n) => `${n.maxLatencyMs} ms`],
      ]}
    />
  )
}

function RevisionComparison({ comparison }: { comparison: OperationsVersionComparison }) {
  const delta = comparison.selectedVsPrevious
  return (
    <section className="agent-block">
      <h3>Revision comparison</h3>
      <p className="muted">
        Active runs keep their pinned immutable execution snapshot — a rollout only changes which
        revision <strong>future</strong> roots select; it never edits, migrates, or cancels work already
        in flight.
      </p>
      {comparison.revisions.length === 0 ? (
        <p className="muted">No Orchestrator revisions recorded yet.</p>
      ) : (
        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr>
                <th>Revision</th>
                <th>Runs</th>
                <th>Completed</th>
                <th>Failed</th>
                <th>Avg latency</th>
                <th>Reserved budget</th>
                <th>Active runs</th>
              </tr>
            </thead>
            <tbody>
              {comparison.revisions.map((r) => (
                <tr key={r.revision}>
                  <td>
                    {r.revision === comparison.selectedRevision ? (
                      <strong>r{r.revision} (selected)</strong>
                    ) : (
                      `r${r.revision}`
                    )}
                  </td>
                  <td>{r.runs}</td>
                  <td>{r.completed}</td>
                  <td>{r.failed}</td>
                  <td>{r.averageLatencyMs} ms</td>
                  <td>{r.reservedBudgetUnits}</td>
                  <td>{r.activeRuns}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {delta && (
        <dl className="agent-test-console__summary">
          <div><dt>Revisions</dt><dd>r{delta.fromRevision} → r{delta.toRevision}</dd></div>
          <div><dt>Run delta</dt><dd>{delta.runDelta}</dd></div>
          <div><dt>Completed delta</dt><dd>{delta.completedDelta}</dd></div>
          <div><dt>Avg latency delta</dt><dd>{delta.averageLatencyDeltaMs} ms</dd></div>
          <div><dt>Reserved budget delta</dt><dd>{delta.reservedBudgetDeltaUnits}</dd></div>
        </dl>
      )}
      <p className="muted">
        {comparison.newRootsOnly
          ? 'A rollout is applied — the tenant is pinned to the selected revision for future roots only.'
          : 'No rollout is applied yet — the tenant is on legacy/default routing.'}{' '}
        {comparison.activeRunsKeepImmutableSnapshot
          ? 'All active runs still hold an intact immutable execution snapshot.'
          : 'At least one active run is missing its immutable execution snapshot.'}
      </p>
    </section>
  )
}

function RegressionPanel({
  gate,
  onChanged,
  evalRunId,
  onEvalRunIdChange,
}: {
  gate: OperationsReleaseGate
  onChanged: () => void
  evalRunId: string
  onEvalRunIdChange: (value: string) => void
}) {
  const toast = useToast()
  const confirm = useConfirm()
  const [suite, setSuite] = useState('')
  const [passed, setPassed] = useState(true)
  const [evidenceRef, setEvidenceRef] = useState('')
  const [reason, setReason] = useState('')
  const [busy, setBusy] = useState(false)
  // 懶初始化:useRef(new X()) 每次 render 都會建構(並讀 sessionStorage),只有第一顆會被留下。
  const attemptRef = useRef<LogicalAttemptKey | null>(null)
  const attempts = (attemptRef.current ??= new LogicalAttemptKey(
    newIdempotencyKey,
    getSessionStorage(),
    OVERRIDE_ATTEMPT_KEY,
  ))
  const trimmedEvalRunId = evalRunId.trim()
  const evalRunIdInvalid = trimmedEvalRunId.length > 0 && !GUID_PATTERN.test(trimmedEvalRunId)
  const evalRunIdTrusted = trimmedEvalRunId.length > 0 && !evalRunIdInvalid

  async function submitRegression() {
    if (busy || !suite.trim() || !evidenceRef.trim() || evalRunIdInvalid) return
    setBusy(true)
    await runWithToast(
      toast,
      () =>
        recordRegression(
          suite.trim(),
          passed,
          evidenceRef.trim(),
          evalRunIdTrusted ? trimmedEvalRunId : undefined,
        ),
      {
        success: 'Regression result recorded.',
        onSuccess: () => {
          setSuite('')
          setEvidenceRef('')
          onEvalRunIdChange('')
          onChanged()
        },
      },
    )
    setBusy(false)
  }

  async function submitOverride() {
    const trimmed = reason.trim()
    if (busy || trimmed.length < 8) return
    if (
      !(await confirm(
        'Override the failed regression gate? This is a break-glass action and is permanently audited.',
        { danger: true, confirmLabel: 'Override' },
      ))
    )
      return
    const identity = [trimmed] as const
    const key = attempts.keyFor(identity)
    setBusy(true)
    await runWithToast(toast, () => overrideRegression(trimmed, key), {
      success: 'Override recorded.',
      onSuccess: () => {
        attempts.consume(identity, key)
        setReason('')
        onChanged()
      },
    })
    setBusy(false)
  }

  return (
    <section className="agent-block" aria-busy={busy}>
      <h3>Regression evidence &amp; override</h3>
      <p>
        Current gate: <strong>{gate.regressionPassed ? 'PASS' : 'FAIL'}</strong>
        {gate.overrideActive && <span className="chip chip--warn"> override active</span>}
        {' '}· {gate.auditEntries} audit entries
      </p>
      <div className="field">
        <label htmlFor="ops-suite">Suite</label>
        <input id="ops-suite" value={suite} onChange={(e) => setSuite(e.target.value)} disabled={busy} />
      </div>
      <div className="field">
        <label htmlFor="ops-evidence">Evidence ref</label>
        <input
          id="ops-evidence"
          value={evidenceRef}
          onChange={(e) => setEvidenceRef(e.target.value)}
          disabled={busy}
        />
      </div>
      <div className="field">
        <label htmlFor="ops-eval-run-id">Eval run ID (optional)</label>
        <input
          id="ops-eval-run-id"
          value={evalRunId}
          onChange={(e) => onEvalRunIdChange(e.target.value)}
          disabled={busy}
          aria-invalid={evalRunIdInvalid}
          aria-describedby={evalRunIdInvalid ? 'ops-eval-run-id-err' : 'ops-eval-run-id-hint'}
        />
        {evalRunIdInvalid ? (
          <span className="field-error" id="ops-eval-run-id-err" role="alert">
            Eval run ID must be a valid GUID (e.g. 3fa85f64-5717-4562-b3fc-2c963f66afa6).
          </span>
        ) : (
          <span className="muted" id="ops-eval-run-id-hint">
            Leave blank to record a caller-supplied result, or pin a completed eval run so the
            server recomputes pass/fail from stored case results (trusted path).
          </span>
        )}
      </div>
      <label>
        <input
          type="checkbox"
          checked={passed}
          onChange={(e) => setPassed(e.target.checked)}
          disabled={busy || evalRunIdTrusted}
        />{' '}
        Passed{evalRunIdTrusted ? ' (computed by server from eval results)' : ''}
      </label>
      <div>
        <button
          className="btn btn--info"
          type="button"
          disabled={busy || !suite.trim() || !evidenceRef.trim() || evalRunIdInvalid}
          onClick={() => void submitRegression()}
        >
          Record regression result
        </button>
      </div>

      {!gate.regressionPassed && (
        <>
          <div className="field">
            <label htmlFor="ops-override-reason">Override reason (min. 8 characters)</label>
            <input
              id="ops-override-reason"
              value={reason}
              onChange={(e) => setReason(e.target.value)}
              disabled={busy}
            />
          </div>
          <button
            className="btn btn--danger"
            type="button"
            disabled={busy || reason.trim().length < 8 || gate.overrideActive}
            onClick={() => void submitOverride()}
          >
            {gate.overrideActive ? 'Override already recorded' : 'Override failed gate'}
          </button>
        </>
      )}
    </section>
  )
}

function RolloutPanel({ onChanged }: { onChanged: () => void }) {
  const toast = useToast()
  const confirm = useConfirm()
  const [enabled, setEnabled] = useState(true)
  const [orchestratorId, setOrchestratorId] = useState('')
  const [revision, setRevision] = useState('')
  const [canaryUserIds, setCanaryUserIds] = useState('')
  const [busy, setBusy] = useState(false)
  const trimmedOrchestratorId = orchestratorId.trim()
  const orchestratorIdInvalid =
    trimmedOrchestratorId.length > 0 && !GUID_PATTERN.test(trimmedOrchestratorId)

  async function submit() {
    if (busy || orchestratorIdInvalid) return
    const parsedRevision = revision.trim() ? Number(revision.trim()) : null
    if (enabled && (!orchestratorId.trim() || !parsedRevision)) {
      toast('Enabling a rollout requires an Orchestrator ID and a pinned revision.', 'error')
      return
    }
    if (
      !(await confirm(
        enabled
          ? `Roll out Orchestrator ${orchestratorId.trim()} r${parsedRevision} to future roots?`
          : 'Roll back to legacy/default routing for future roots? Active runs are unaffected.',
        { confirmLabel: enabled ? 'Roll out' : 'Roll back' },
      ))
    )
      return
    setBusy(true)
    await runWithToast(
      toast,
      () =>
        applyRollout({
          enabled,
          orchestratorId: orchestratorId.trim() || null,
          revision: parsedRevision,
          canaryUserIds: canaryUserIds
            .split(',')
            .map((s) => s.trim())
            .filter(Boolean),
        }),
      {
        success: enabled ? 'Rollout applied — future roots only.' : 'Rolled back — future roots only.',
        onSuccess: onChanged,
      },
    )
    setBusy(false)
  }

  return (
    <section className="agent-block" aria-busy={busy}>
      <h3>Rollout / rollback</h3>
      <p className="muted">
        Changes the tenant default Orchestrator binding for future roots only. Active runs keep their
        pinned immutable snapshot and are never edited or cancelled by this action.
      </p>
      <label>
        <input type="checkbox" checked={enabled} onChange={(e) => setEnabled(e.target.checked)} disabled={busy} />{' '}
        Enabled (uncheck to roll back to legacy/default routing)
      </label>
      <div className="field">
        <label htmlFor="ops-orch-id">Orchestrator ID</label>
        <input
          id="ops-orch-id"
          value={orchestratorId}
          onChange={(e) => setOrchestratorId(e.target.value)}
          disabled={busy}
          aria-invalid={orchestratorIdInvalid}
          aria-describedby={orchestratorIdInvalid ? 'ops-orch-id-err' : undefined}
        />
        {orchestratorIdInvalid && (
          <span className="field-error" id="ops-orch-id-err" role="alert">
            Orchestrator ID must be a valid GUID (e.g. 3fa85f64-5717-4562-b3fc-2c963f66afa6).
          </span>
        )}
      </div>
      <div className="field">
        <label htmlFor="ops-orch-rev">Revision</label>
        <input
          id="ops-orch-rev"
          type="number"
          min={1}
          value={revision}
          onChange={(e) => setRevision(e.target.value)}
          disabled={busy}
        />
      </div>
      <div className="field">
        <label htmlFor="ops-canary">Canary user IDs (comma-separated)</label>
        <input
          id="ops-canary"
          value={canaryUserIds}
          onChange={(e) => setCanaryUserIds(e.target.value)}
          disabled={busy}
        />
      </div>
      <button
        className={`btn ${enabled ? 'btn--info' : 'btn--danger'}`}
        type="button"
        disabled={busy || orchestratorIdInvalid}
        onClick={() => void submit()}
      >
        {enabled ? 'Apply rollout' : 'Apply rollback'}
      </button>
    </section>
  )
}

/**
 * D7 Operations/release cockpit(Phase O1)。純 UI 投影:重用既有 metrics/version-comparison/
 * legacy-inventory/regression/override/rollout API,不新增端點、不自行推導 release eligibility
 * ——所有寫入動作的授權、idempotency 與 audit 仍完全由 Backend 把關。
 */
export default function OperationsGovernanceView() {
  const { data, loading, error, reload } = useResource(loadOperations)
  const [regressionEvalRunId, setRegressionEvalRunId] = useState('')

  return (
    <section className="agent-block">
      <h2>Operations governance</h2>
      <p className="muted">
        Aggregate, redacted release data only. Server authorization (workflow.manage), feature flags,
        and idempotency are unchanged by this view.
      </p>
      <ErrorText msg={error} />

      {loading && !data ? (
        <Skeleton rows={6} />
      ) : !data ? null : (
        <>
          <SummaryCards metrics={data.metrics} />

          <h3>Agent usage</h3>
          <AgentTable rows={data.metrics.agents} />
          <h3>Skill usage</h3>
          <SkillTable rows={data.metrics.skills} />
          <h3>Tool usage</h3>
          <ToolTable rows={data.metrics.tools} />
          <h3>Node usage</h3>
          <NodeTable rows={data.metrics.nodes} />

          <RevisionComparison comparison={data.comparison} />
          <EvaluationPanel onUseForRegressionGate={setRegressionEvalRunId} />
          <RegressionPanel
            gate={data.metrics.releaseGate}
            onChanged={reload}
            evalRunId={regressionEvalRunId}
            onEvalRunIdChange={setRegressionEvalRunId}
          />
          <RolloutPanel onChanged={reload} />

          <section className="agent-block">
            <h3>Legacy inventory</h3>
            {data.inventory.length === 0 ? (
              <p className="muted">No legacy items recorded.</p>
            ) : (
              <div className="table-wrap">
                <table className="table">
                  <thead>
                    <tr>
                      <th>Item</th>
                      <th>Disposition</th>
                      <th>Trigger</th>
                    </tr>
                  </thead>
                  <tbody>
                    {data.inventory.map((item) => (
                      <tr key={item.id}>
                        <td>{item.id}</td>
                        <td>{item.disposition}</td>
                        <td>{item.trigger}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </section>

          <details>
            <summary>Raw JSON (debug)</summary>
            <pre>{JSON.stringify(data, null, 2)}</pre>
          </details>
        </>
      )}
    </section>
  )
}
