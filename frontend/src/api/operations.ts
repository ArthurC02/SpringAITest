import { apiFetch } from './http'
import { integer, number, object, pick, text } from '../wire'
import type {
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

export interface RolloutInput {
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
