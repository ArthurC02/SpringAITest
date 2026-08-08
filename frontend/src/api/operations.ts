import { apiFetch } from './http'
import { integer, number, object, pick, text } from '../wire'
import type {
  EvalCaseDelta,
  EvalCaseResult,
  EvalRun,
  EvalRunCandidate,
  EvalSuite,
  EvalSuiteDetail,
  EvalSuiteRevision,
  OperationsAgentMetric,
  OperationsAggregateMetric,
  OperationsLegacyInventoryItem,
  OperationsMetrics,
  OperationsNodeMetric,
  OperationsReleaseGate,
  OperationsRevisionDelta,
  OperationsRevisionMetric,
  OperationsSkillMetric,
  OperationsToolMetric,
  OperationsVersionComparison,
} from '../types'

// D7 metrics 混用命名(見 types.ts 開頭註解):release_gate/multi_agent 等外層鍵是後端顯式
// snake_case,agents/skills/tools/nodes/aggregation 內層鍵是 C# record 預設 camelCase。
// 每個欄位用 pick() 同時試兩種別名,不假設單一命名(比照 agentRuns.ts 的既有作法)。
function releaseGate(value: unknown): OperationsReleaseGate {
  const source = object(value)
  return {
    regressionPassed: pick(source, 'regression_passed', 'regressionPassed') === true,
    overrideActive: pick(source, 'override_active', 'overrideActive') === true,
    auditEntries: integer(pick(source, 'audit_entries', 'auditEntries')) ?? 0,
  }
}

function array(value: unknown): unknown[] {
  return Array.isArray(value) ? value : []
}

function agentMetric(value: unknown): OperationsAgentMetric {
  const source = object(value)
  return {
    agentId: text(pick(source, 'agentId', 'agent_id')) ?? '',
    revision: integer(pick(source, 'revision')) ?? 0,
    runs: integer(pick(source, 'runs')) ?? 0,
    completed: integer(pick(source, 'completed')) ?? 0,
    failed: integer(pick(source, 'failed')) ?? 0,
    averageLatencyMs: integer(pick(source, 'averageLatencyMs', 'average_latency_ms')) ?? 0,
    reservedBudgetUnits: integer(pick(source, 'reservedBudgetUnits', 'reserved_budget_units')) ?? 0,
    observedUsageUnits: integer(pick(source, 'observedUsageUnits', 'observed_usage_units')),
    observedCostUnits: number(pick(source, 'observedCostUnits', 'observed_cost_units')),
    observedLatencyMs: integer(pick(source, 'observedLatencyMs', 'observed_latency_ms')),
  }
}

function skillMetric(value: unknown): OperationsSkillMetric {
  const source = object(value)
  return {
    name: text(pick(source, 'name')) ?? '',
    revision: integer(pick(source, 'revision')) ?? 0,
    runs: integer(pick(source, 'runs')) ?? 0,
    observedLatencyMs: integer(pick(source, 'observedLatencyMs', 'observed_latency_ms')),
    observedUsageUnits: integer(pick(source, 'observedUsageUnits', 'observed_usage_units')),
    observedCostUnits: number(pick(source, 'observedCostUnits', 'observed_cost_units')),
    reservedBudgetUnits: integer(pick(source, 'reservedBudgetUnits', 'reserved_budget_units')) ?? 0,
  }
}

function toolMetric(value: unknown): OperationsToolMetric {
  const source = object(value)
  return {
    kind: text(pick(source, 'kind')) ?? '',
    count: integer(pick(source, 'count')) ?? 0,
    observedLatencyMs: integer(pick(source, 'observedLatencyMs', 'observed_latency_ms')),
    observedUsageUnits: integer(pick(source, 'observedUsageUnits', 'observed_usage_units')),
    observedCostUnits: number(pick(source, 'observedCostUnits', 'observed_cost_units')),
    reservedBudgetUnits: integer(pick(source, 'reservedBudgetUnits', 'reserved_budget_units')) ?? 0,
  }
}

function nodeMetric(value: unknown): OperationsNodeMetric {
  const source = object(value)
  return {
    nodeId: text(pick(source, 'nodeId', 'node_id')) ?? '',
    executions: integer(pick(source, 'executions')) ?? 0,
    averageLatencyMs: integer(pick(source, 'averageLatencyMs', 'average_latency_ms')) ?? 0,
    maxLatencyMs: integer(pick(source, 'maxLatencyMs', 'max_latency_ms')) ?? 0,
  }
}

function aggregateMetric(value: unknown): OperationsAggregateMetric {
  const source = object(value)
  return {
    completed: integer(pick(source, 'completed')) ?? 0,
    partialOrFailed: integer(pick(source, 'partialOrFailed', 'partial_or_failed')) ?? 0,
    averageFanOut: integer(pick(source, 'averageFanOut', 'average_fan_out')) ?? 0,
    averageLatencyMs: integer(pick(source, 'averageLatencyMs', 'average_latency_ms')) ?? 0,
  }
}

function revisionMetric(value: unknown): OperationsRevisionMetric {
  const source = object(value)
  return {
    revision: integer(pick(source, 'revision')) ?? 0,
    runs: integer(pick(source, 'runs')) ?? 0,
    completed: integer(pick(source, 'completed')) ?? 0,
    failed: integer(pick(source, 'failed')) ?? 0,
    averageLatencyMs: integer(pick(source, 'averageLatencyMs', 'average_latency_ms')) ?? 0,
    reservedBudgetUnits: integer(pick(source, 'reservedBudgetUnits', 'reserved_budget_units')) ?? 0,
    activeRuns: integer(pick(source, 'activeRuns', 'active_runs')) ?? 0,
  }
}

function revisionDelta(value: unknown): OperationsRevisionDelta | null {
  if (value === null || value === undefined) return null
  const source = object(value)
  return {
    fromRevision: integer(pick(source, 'fromRevision', 'from_revision')) ?? 0,
    toRevision: integer(pick(source, 'toRevision', 'to_revision')) ?? 0,
    runDelta: integer(pick(source, 'runDelta', 'run_delta')) ?? 0,
    completedDelta: integer(pick(source, 'completedDelta', 'completed_delta')) ?? 0,
    averageLatencyDeltaMs: integer(pick(source, 'averageLatencyDeltaMs', 'average_latency_delta_ms')) ?? 0,
    reservedBudgetDeltaUnits: integer(pick(source, 'reservedBudgetDeltaUnits', 'reserved_budget_delta_units')) ?? 0,
  }
}

function legacyInventoryItem(value: unknown): OperationsLegacyInventoryItem {
  const source = object(value)
  return {
    id: text(pick(source, 'id')) ?? '',
    disposition: text(pick(source, 'disposition')) ?? '',
    trigger: text(pick(source, 'trigger')) ?? '',
  }
}

export function normalizeMetrics(value: unknown): OperationsMetrics {
  const source = object(value)
  const multi = object(pick(source, 'multi_agent', 'multiAgent'))
  return {
    releaseGate: releaseGate(pick(source, 'release_gate', 'releaseGate')),
    rolloutEvents: integer(pick(multi, 'rolloutEvents', 'rollout_events')) ?? 0,
    rootRuns: integer(pick(multi, 'rootRuns', 'root_runs')) ?? 0,
    childRuns: integer(pick(multi, 'childRuns', 'child_runs')) ?? 0,
    childSuccess: integer(pick(multi, 'childSuccess', 'child_success')) ?? 0,
    verifierReject: integer(pick(multi, 'verifierReject', 'verifier_reject')) ?? 0,
    repairRounds: integer(pick(multi, 'repairRounds', 'repair_rounds')) ?? 0,
    writeEffects: integer(pick(multi, 'writeEffects', 'write_effects')) ?? 0,
    agents: array(pick(multi, 'agents')).map(agentMetric),
    skills: array(pick(multi, 'skills')).map(skillMetric),
    tools: array(pick(multi, 'tools')).map(toolMetric),
    nodes: array(pick(multi, 'nodes')).map(nodeMetric),
    aggregation: aggregateMetric(pick(multi, 'aggregation')),
  }
}

function normalizeVersionComparison(value: unknown): OperationsVersionComparison {
  const source = object(value)
  return {
    selectedRevision: integer(pick(source, 'selectedRevision', 'selected_revision')),
    rolloutEvents: integer(pick(source, 'rolloutEvents', 'rollout_events')) ?? 0,
    newRootsOnly: pick(source, 'newRootsOnly', 'new_roots_only') === true,
    activeRunsKeepImmutableSnapshot:
      pick(source, 'activeRunsKeepImmutableSnapshot', 'active_runs_keep_immutable_snapshot') === true,
    revisions: array(pick(source, 'revisions')).map(revisionMetric),
    selectedVsPrevious: revisionDelta(pick(source, 'selectedVsPrevious', 'selected_vs_previous')),
  }
}

export async function getOperationsMetrics(): Promise<OperationsMetrics> {
  return normalizeMetrics(await apiFetch<unknown>('/api/admin/operations/metrics'))
}

export async function getVersionComparison(): Promise<OperationsVersionComparison> {
  return normalizeVersionComparison(await apiFetch<unknown>('/api/admin/operations/version-comparison'))
}

export async function getLegacyInventory(): Promise<OperationsLegacyInventoryItem[]> {
  return array(await apiFetch<unknown>('/api/admin/operations/legacy-inventory')).map(legacyInventoryItem)
}

/**
 * POST /api/admin/operations/regressions — 記錄一次 regression 結果,回傳最新 gate。
 * `evalRunId`(E3 可信路徑)給定時,backend 會依 stored eval case results 自行重算 `passed`,
 * 忽略呼叫端送的 `passed` 值;省略時維持既有 caller-supplied `passed` 行為不變。
 */
export async function recordRegression(
  suite: string,
  passed: boolean,
  evidenceRef: string,
  evalRunId?: string,
): Promise<OperationsReleaseGate> {
  return releaseGate(
    await apiFetch<unknown>('/api/admin/operations/regressions', {
      method: 'POST',
      body: JSON.stringify({
        suite,
        passed,
        evidence_ref: evidenceRef,
        ...(evalRunId ? { eval_run_id: evalRunId } : {}),
      }),
    }),
  )
}

/**
 * POST /api/admin/operations/regression-overrides — break-glass 覆寫失敗的 gate。
 * Idempotency-Key 必要;同 key + 同 reason 會 replay 回同一筆結果,不重複寫入稽核。
 */
export async function overrideRegression(
  reason: string,
  idempotencyKey: string,
): Promise<OperationsReleaseGate> {
  return releaseGate(
    await apiFetch<unknown>('/api/admin/operations/regression-overrides', {
      method: 'POST',
      headers: { 'Idempotency-Key': idempotencyKey },
      body: JSON.stringify({ reason }),
    }),
  )
}

interface RolloutInput {
  enabled: boolean
  orchestratorId: string | null
  revision: number | null
  canaryUserIds: string[]
}

/** PUT /api/admin/operations/rollout — 只改變「未來 root」選哪個 revision,絕不動作用中 run。 */
export async function applyRollout(input: RolloutInput): Promise<void> {
  await apiFetch<unknown>('/api/admin/operations/rollout', {
    method: 'PUT',
    body: JSON.stringify({
      enabled: input.enabled,
      orchestrator_id: input.orchestratorId,
      revision: input.revision,
      canary_user_ids: input.canaryUserIds,
    }),
  })
}

/** 任一分量未知就整體回未知,不用部分加總假裝完整(未知/估計語意見規格)。 */
export function sumOrUnknown(values: Array<number | null>): number | null {
  return values.some((v) => v === null) ? null : values.reduce<number>((total, v) => total + (v ?? 0), 0)
}

// ── E4 Evaluation cockpit:eval-suites/eval-runs 是 EvalController.cs 顯式定義的
// snake_case 契約(見 EvalDtos.cs 的 JsonPropertyName),但仍用 pick() 雙別名,
// 和本檔其餘正規化函式風格一致、對未來命名調整有防禦(比照 releaseGate() 等)。────

function evalSuite(value: unknown): EvalSuite {
  const source = object(value)
  return {
    suiteId: text(pick(source, 'suite_id', 'suiteId')) ?? '',
    currentRevision: integer(pick(source, 'current_revision', 'currentRevision')) ?? 0,
    createdAt: text(pick(source, 'created_at', 'createdAt')),
    updatedAt: text(pick(source, 'updated_at', 'updatedAt')),
  }
}

function evalSuiteRevision(value: unknown): EvalSuiteRevision {
  const source = object(value)
  return {
    revision: integer(pick(source, 'revision')) ?? 0,
    casesSha256: text(pick(source, 'cases_sha256', 'casesSha256')),
    caseCount: integer(pick(source, 'case_count', 'caseCount')) ?? 0,
    createdBy: text(pick(source, 'created_by', 'createdBy')),
    createdAt: text(pick(source, 'created_at', 'createdAt')),
  }
}

export function normalizeEvalSuiteDetail(value: unknown): EvalSuiteDetail {
  const source = object(value)
  return {
    ...evalSuite(source),
    revisions: array(pick(source, 'revisions')).map(evalSuiteRevision),
  }
}

function evalCaseResult(value: unknown): EvalCaseResult {
  const source = object(value)
  const metrics = object(pick(source, 'metrics'))
  return {
    caseId: text(pick(source, 'case_id', 'caseId')) ?? '',
    canonicalIdentity: text(pick(source, 'canonical_identity', 'canonicalIdentity')),
    verdict: text(pick(source, 'verdict')) ?? '',
    latencyMs: integer(pick(metrics, 'latency_ms', 'latencyMs')),
    failureReason: text(pick(source, 'failure_reason', 'failureReason')),
  }
}

function evalRunCandidate(value: unknown): EvalRunCandidate {
  const source = object(value)
  return {
    kind: text(pick(source, 'kind')) ?? '',
    identitySha256: text(pick(source, 'identity_sha256', 'identitySha256')) ?? '',
  }
}

export function normalizeEvalRun(value: unknown): EvalRun {
  const source = object(value)
  const casesRaw = pick(source, 'cases')
  return {
    id: text(pick(source, 'id')) ?? '',
    suiteId: text(pick(source, 'suite_id', 'suiteId')) ?? '',
    suiteRevision: integer(pick(source, 'suite_revision', 'suiteRevision')) ?? 0,
    candidate: evalRunCandidate(pick(source, 'candidate')),
    runnerVersion: text(pick(source, 'runner_version', 'runnerVersion')),
    startedAt: text(pick(source, 'started_at', 'startedAt')),
    completedAt: text(pick(source, 'completed_at', 'completedAt')),
    passCount: integer(pick(source, 'pass_count', 'passCount')) ?? 0,
    failCount: integer(pick(source, 'fail_count', 'failCount')) ?? 0,
    errorCount: integer(pick(source, 'error_count', 'errorCount')) ?? 0,
    cases: Array.isArray(casesRaw) ? casesRaw.map(evalCaseResult) : null,
  }
}

export async function listEvalSuites(): Promise<EvalSuite[]> {
  return array(await apiFetch<unknown>('/api/admin/operations/eval-suites')).map(evalSuite)
}

export async function getEvalSuite(suiteId: string): Promise<EvalSuiteDetail> {
  return normalizeEvalSuiteDetail(
    await apiFetch<unknown>(`/api/admin/operations/eval-suites/${encodeURIComponent(suiteId)}`),
  )
}

export async function listEvalRuns(): Promise<EvalRun[]> {
  return array(await apiFetch<unknown>('/api/admin/operations/eval-runs')).map(normalizeEvalRun)
}

export async function getEvalRun(runId: string): Promise<EvalRun> {
  return normalizeEvalRun(
    await apiFetch<unknown>(`/api/admin/operations/eval-runs/${encodeURIComponent(runId)}`),
  )
}

/** POST body 的 candidate 部分;本階段 kind 固定 `"skill"`,ref 依 workflow 慣例是 `{name}`
 * (見 workflow/app/evals/api.py `_resolve_candidate_skill`)。 */
interface EvalRunCandidateInput {
  kind: 'skill'
  ref: { name: string }
}

/**
 * POST /api/admin/operations/eval-runs — 觸發一次 eval run。`Idempotency-Key` 必要;
 * 同 key + 同 suite/revision/candidate identity 會 replay 回同一筆結果(見 EvalController.CreateRun)。
 */
export async function createEvalRun(
  suiteId: string,
  revision: number,
  candidate: EvalRunCandidateInput,
  idempotencyKey: string,
  budgetMs?: number,
): Promise<EvalRun> {
  return normalizeEvalRun(
    await apiFetch<unknown>('/api/admin/operations/eval-runs', {
      method: 'POST',
      headers: { 'Idempotency-Key': idempotencyKey },
      body: JSON.stringify({
        suite_id: suiteId,
        revision,
        candidate,
        ...(budgetMs ? { budget_ms: budgetMs } : {}),
      }),
    }),
  )
}

/**
 * 純前端 baseline/candidate 逐 case verdict 比對(規格 §7:「client-side 逐 case 比對,
 * 不加後端 API」)。case 以 `caseId` 對齊;只有 PASS↔非 PASS 的翻轉算 regressed/improved,
 * 其餘 verdict 變動(例如 FAIL→ERROR)算 changed,兩邊都沒出現的 case 不會列出。
 */
export function diffEvalRuns(baseline: EvalRun, candidate: EvalRun): EvalCaseDelta[] {
  const baselineCases = new Map((baseline.cases ?? []).map((c) => [c.caseId, c] as const))
  const candidateCases = new Map((candidate.cases ?? []).map((c) => [c.caseId, c] as const))
  const caseIds = [...new Set([...baselineCases.keys(), ...candidateCases.keys()])].sort()
  return caseIds.map((caseId) => {
    const b = baselineCases.get(caseId) ?? null
    const c = candidateCases.get(caseId) ?? null
    let status: EvalCaseDelta['status']
    if (!b) status = 'added'
    else if (!c) status = 'removed'
    else if (b.verdict === c.verdict) status = 'unchanged'
    else if (b.verdict === 'PASS') status = 'regressed'
    else if (c.verdict === 'PASS') status = 'improved'
    else status = 'changed'
    return { caseId, baselineVerdict: b?.verdict ?? null, candidateVerdict: c?.verdict ?? null, status }
  })
}
