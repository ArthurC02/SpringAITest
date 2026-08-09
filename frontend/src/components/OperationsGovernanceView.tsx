import { useRef, useState, type ReactNode } from 'react'
import {
  applyRollout,
  getLegacyInventory,
  getOperationsMetrics,
  getVersionComparison,
  listEvalRuns,
  overrideRegression,
  recordRegression,
  sumOrUnknown,
} from '../api/operations'
import { listOrchestrators } from '../api/orchestrators'
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
import CatalogPicker from './CatalogPicker'

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
          根執行 · {metrics.childRuns} 個子執行({metrics.childSuccess} 成功)
        </div>
      </div>
      <div className="card">
        <div className="card__num">
          {metrics.aggregation.completed} / {metrics.aggregation.partialOrFailed}
        </div>
        <div className="card__label">已完成 / 部分失敗或失敗的根執行</div>
      </div>
      <div className="card">
        <div className="card__num">{metrics.aggregation.averageLatencyMs} ms</div>
        <div className="card__label">根執行平均延遲</div>
      </div>
      <div className="card">
        <div className="card__num">{totalUsage === null ? <Observed value={null} /> : totalUsage}</div>
        <div className="card__label">已量測用量單位(Agent)</div>
      </div>
      <div className="card">
        <div className="card__num">{totalCost === null ? <Observed value={null} /> : totalCost}</div>
        <div className="card__label">已量測成本單位(Agent)</div>
      </div>
      <div className="card">
        <div className="card__num">{gate.regressionPassed ? '通過' : '未通過'}</div>
        <div className="card__label">
          品質迴歸關卡{gate.overrideActive ? ' · 已啟用覆蓋' : ''} · {gate.auditEntries} 筆稽核紀錄
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
      empty="尚無 Agent 執行紀錄。"
      rowKey={(a) => `${a.agentId}:${a.revision}`}
      columns={[
        ['Agent', (a) => a.agentId],
        ['版本', (a) => `r${a.revision}`],
        ['執行次數', (a) => a.runs],
        ['已完成', (a) => a.completed],
        ['失敗', (a) => a.failed],
        ['平均延遲', (a) => `${a.averageLatencyMs} ms`],
        ['預留預算', (a) => a.reservedBudgetUnits],
        ['已量測用量', (a) => <Observed value={a.observedUsageUnits} />],
        ['已量測成本', (a) => <Observed value={a.observedCostUnits} />],
        ['已量測延遲', (a) => <Observed value={a.observedLatencyMs} unit=" ms" />],
      ]}
    />
  )
}

function SkillTable({ rows }: { rows: OperationsSkillMetric[] }) {
  return (
    <MetricsTable
      rows={rows}
      empty="尚無 Skill 用量紀錄。"
      rowKey={(s) => `${s.name}:${s.revision}`}
      columns={[
        ['Skill', (s) => s.name],
        ['版本', (s) => `r${s.revision}`],
        ['執行次數', (s) => s.runs],
        ['已量測延遲', (s) => <Observed value={s.observedLatencyMs} unit=" ms" />],
        ['已量測用量', (s) => <Observed value={s.observedUsageUnits} />],
        ['已量測成本', (s) => <Observed value={s.observedCostUnits} />],
        ['預留預算', (s) => s.reservedBudgetUnits],
      ]}
    />
  )
}

function ToolTable({ rows }: { rows: OperationsToolMetric[] }) {
  return (
    <MetricsTable
      rows={rows}
      empty="尚無工具用量紀錄。"
      rowKey={(t) => t.kind}
      columns={[
        ['工具', (t) => t.kind],
        ['次數', (t) => t.count],
        ['已量測延遲', (t) => <Observed value={t.observedLatencyMs} unit=" ms" />],
        ['已量測用量', (t) => <Observed value={t.observedUsageUnits} />],
        ['已量測成本', (t) => <Observed value={t.observedCostUnits} />],
        ['預留預算', (t) => t.reservedBudgetUnits],
      ]}
    />
  )
}

function NodeTable({ rows }: { rows: OperationsNodeMetric[] }) {
  return (
    <MetricsTable
      rows={rows}
      empty="尚無節點執行紀錄。"
      rowKey={(n) => n.nodeId}
      columns={[
        ['節點', (n) => n.nodeId],
        ['執行次數', (n) => n.executions],
        ['平均延遲', (n) => `${n.averageLatencyMs} ms`],
        ['最大延遲', (n) => `${n.maxLatencyMs} ms`],
      ]}
    />
  )
}

function RevisionComparison({ comparison }: { comparison: OperationsVersionComparison }) {
  const delta = comparison.selectedVsPrevious
  return (
    <section className="agent-block">
      <h3>版本比較</h3>
      <p className="muted">
        執行中的 run 一律保留自己 pinned 的不可變執行快照——上線只改變<strong>未來</strong>
        根執行要選哪個版本,絕不會編輯、遷移或取消已經在跑的工作。
      </p>
      {comparison.revisions.length === 0 ? (
        <p className="muted">尚無協作流程版本紀錄。</p>
      ) : (
        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr>
                <th>版本</th>
                <th>執行次數</th>
                <th>已完成</th>
                <th>失敗</th>
                <th>平均延遲</th>
                <th>預留預算</th>
                <th>執行中</th>
              </tr>
            </thead>
            <tbody>
              {comparison.revisions.map((r) => (
                <tr key={r.revision}>
                  <td>
                    {r.revision === comparison.selectedRevision ? (
                      <strong>r{r.revision}(目前選用)</strong>
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
          <div><dt>版本</dt><dd>r{delta.fromRevision} → r{delta.toRevision}</dd></div>
          <div><dt>執行次數變化</dt><dd>{delta.runDelta}</dd></div>
          <div><dt>已完成變化</dt><dd>{delta.completedDelta}</dd></div>
          <div><dt>平均延遲變化</dt><dd>{delta.averageLatencyDeltaMs} ms</dd></div>
          <div><dt>預留預算變化</dt><dd>{delta.reservedBudgetDeltaUnits}</dd></div>
        </dl>
      )}
      <p className="muted">
        {comparison.newRootsOnly
          ? '已套用上線設定——租戶已釘選此版本,僅套用於未來的根執行。'
          : '尚未套用任何上線設定——租戶目前走舊有/預設路由。'}{' '}
        {comparison.activeRunsKeepImmutableSnapshot
          ? '所有執行中的 run 仍持有完整的不可變執行快照。'
          : '至少有一個執行中的 run 缺少不可變執行快照。'}
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
  // W5(規格 §5.1 補充項):Eval run ID 手輸 GUID → 下拉,資料源沿用 EvaluationPanel 已用過的
  // listEvalRuns();目錄載入失敗或為空,CatalogPicker 自動優雅退回手動輸入(GUID_PATTERN 驗證仍保留)。
  const evalRunsRes = useResource(listEvalRuns)
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
        success: '已記錄品質迴歸結果。',
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
        '確定要覆蓋這個失敗的品質迴歸關卡嗎?這是緊急覆蓋動作,全程留痕稽核。',
        { danger: true, confirmLabel: '覆蓋' },
      ))
    )
      return
    const identity = [trimmed] as const
    const key = attempts.keyFor(identity)
    setBusy(true)
    await runWithToast(toast, () => overrideRegression(trimmed, key), {
      success: '已記錄覆蓋。',
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
      <h3>品質迴歸證據與覆蓋</h3>
      <p>
        目前關卡狀態:<strong>{gate.regressionPassed ? '通過' : '未通過'}</strong>
        {gate.overrideActive && <span className="chip chip--warn"> 已啟用覆蓋</span>}
        {' '}· {gate.auditEntries} 筆稽核紀錄
      </p>
      <div className="field">
        <label htmlFor="ops-suite">測試組合</label>
        <input id="ops-suite" value={suite} onChange={(e) => setSuite(e.target.value)} disabled={busy} />
      </div>
      <div className="field">
        <label htmlFor="ops-evidence">證據參照</label>
        <input
          id="ops-evidence"
          value={evidenceRef}
          onChange={(e) => setEvidenceRef(e.target.value)}
          disabled={busy}
        />
      </div>
      <CatalogPicker
        id="ops-eval-run-id"
        label="評測執行 ID(選填)"
        value={evalRunId}
        onChange={onEvalRunIdChange}
        disabled={busy}
        items={evalRunsRes.data}
        itemsError={evalRunsRes.error}
        itemsLoading={evalRunsRes.loading}
        optionValue={(r) => r.id}
        optionLabel={(r) => `${r.suiteId} r${r.suiteRevision} · ${r.id.slice(0, 8)}`}
        invalid={evalRunIdInvalid}
        invalidHint="評測執行 ID 必須是合法的 GUID(例如 3fa85f64-5717-4562-b3fc-2c963f66afa6)。"
        hint="留白代表記錄呼叫端自行提供的結果;選定一個已完成的評測執行,則由伺服器依儲存的案例結果重新計算通過/未通過(可信路徑)。"
      />
      <label>
        <input
          type="checkbox"
          checked={passed}
          onChange={(e) => setPassed(e.target.checked)}
          disabled={busy || evalRunIdTrusted}
        />{' '}
        通過{evalRunIdTrusted ? '(由伺服器依評測結果計算)' : ''}
      </label>
      <div>
        <button
          className="btn btn--info"
          type="button"
          disabled={busy || !suite.trim() || !evidenceRef.trim() || evalRunIdInvalid}
          onClick={() => void submitRegression()}
        >
          記錄品質迴歸結果
        </button>
      </div>

      {!gate.regressionPassed && (
        <>
          <div className="field">
            <label htmlFor="ops-override-reason">覆蓋原因(至少 8 個字元)</label>
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
            {gate.overrideActive ? '已記錄覆蓋' : '覆蓋失敗的關卡'}
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
  // W5(規格 §5.1):Rollout 的 Orchestrator ID 手輸 GUID → 下拉,資料源 listOrchestrators();
  // 目錄載入失敗(例如 WORKFLOW_DESIGNER_ENABLED 關閉時 404)或為空,CatalogPicker 自動優雅
  // 退回手動輸入,不壞頁(GUID_PATTERN 驗證仍保留)。
  const orchestratorsRes = useResource(listOrchestrators)
  const trimmedOrchestratorId = orchestratorId.trim()
  const orchestratorIdInvalid =
    trimmedOrchestratorId.length > 0 && !GUID_PATTERN.test(trimmedOrchestratorId)

  async function submit() {
    if (busy || orchestratorIdInvalid) return
    const parsedRevision = revision.trim() ? Number(revision.trim()) : null
    if (enabled && (!orchestratorId.trim() || !parsedRevision)) {
      toast('啟用上線設定需要提供 Orchestrator ID 與明確指定的版本。', 'error')
      return
    }
    if (
      !(await confirm(
        enabled
          ? `將協作流程 ${orchestratorId.trim()} r${parsedRevision} 上線到未來的根執行?`
          : '回退到舊有/預設路由,套用於未來的根執行?執行中的 run 不受影響。',
        { confirmLabel: enabled ? '上線' : '回退' },
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
        success: enabled ? '已上線——僅套用於未來的根執行。' : '已回退——僅套用於未來的根執行。',
        onSuccess: onChanged,
      },
    )
    setBusy(false)
  }

  return (
    <section className="agent-block" aria-busy={busy}>
      <h3>上線 / 回退</h3>
      <p className="muted">
        僅改變租戶未來根執行的預設協作流程綁定。執行中的 run 保留自己 pinned 的不可變快照,不會被這個動作編輯或取消。
      </p>
      <label>
        <input type="checkbox" checked={enabled} onChange={(e) => setEnabled(e.target.checked)} disabled={busy} />{' '}
        已啟用(取消勾選以回退到舊有/預設路由)
      </label>
      <CatalogPicker
        id="ops-orch-id"
        label="Orchestrator ID"
        value={orchestratorId}
        onChange={(id) => {
          setOrchestratorId(id)
          const picked = orchestratorsRes.data?.find((o) => o.id === id)
          if (picked?.published_revision != null && !revision.trim()) {
            setRevision(String(picked.published_revision))
          }
        }}
        disabled={busy}
        items={orchestratorsRes.data}
        itemsError={orchestratorsRes.error}
        itemsLoading={orchestratorsRes.loading}
        optionValue={(o) => o.id}
        optionLabel={(o) => `${o.name}${o.published_revision != null ? ` (r${o.published_revision})` : ''}`}
        invalid={orchestratorIdInvalid}
        invalidHint="Orchestrator ID 必須是合法的 GUID(例如 3fa85f64-5717-4562-b3fc-2c963f66afa6)。"
      />
      <div className="field">
        <label htmlFor="ops-orch-rev">版本</label>
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
        <label htmlFor="ops-canary">Canary 使用者 ID(以逗號分隔)</label>
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
        {enabled ? '套用上線' : '套用回退'}
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
      <h2>營運治理</h2>
      <p className="muted">
        僅呈現彙總、已去識別化的上線資料。伺服器端授權(workflow.manage)、功能旗標與冪等性判斷不受此畫面影響。
      </p>
      <ErrorText msg={error} />

      {loading && !data ? (
        <Skeleton rows={6} />
      ) : !data ? null : (
        <>
          <SummaryCards metrics={data.metrics} />

          <h3>Agent 用量</h3>
          <AgentTable rows={data.metrics.agents} />
          <h3>Skill 用量</h3>
          <SkillTable rows={data.metrics.skills} />
          <h3>工具用量</h3>
          <ToolTable rows={data.metrics.tools} />
          <h3>節點用量</h3>
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
            <h3>舊制清單</h3>
            {data.inventory.length === 0 ? (
              <p className="muted">尚無舊制項目紀錄。</p>
            ) : (
              <div className="table-wrap">
                <table className="table">
                  <thead>
                    <tr>
                      <th>項目</th>
                      <th>處置方式</th>
                      <th>觸發條件</th>
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

          {/* ponytail: W5 結構化表單覆蓋了上方所有欄位,這份原始 JSON 除錯區塊語意上與
              04-operations-trigger-plan §10「Structured cockpit 上線後移除 raw JSON
              production view」的既定清理項一致；保留作為除錯逃生口,列為後續清理輪的候選,
              不在本輪一併移除(見 01-plan §6 舊碼盤點)。 */}
          <details>
            <summary>原始 JSON(除錯用)</summary>
            <pre>{JSON.stringify(data, null, 2)}</pre>
          </details>
        </>
      )}
    </section>
  )
}
