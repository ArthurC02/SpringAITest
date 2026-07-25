import type { AgentRun, AgentRunEvent } from './types'

type JsonObject = Record<string, unknown>

export interface OrchestratorTraceChild {
  childId: string | null
  taskId: string | null
  attempt: number | null
  kind: string | null
  agentId: string | null
  agentRevision: number | null
  status: string | null
  verdict: string | null
  citations: Array<{ id: string; title: string | null }>
}

export interface OrchestratorTraceEvent {
  sequence: number
  eventType: string
  rootStatus: string | null
  child: OrchestratorTraceChild | null
}

const BUDGET_KEYS = new Set([
  'maxTasks', 'max_tasks', 'maxChildRuns', 'max_child_runs', 'maxConcurrency', 'max_concurrency',
  'maxRepairRounds', 'max_repair_rounds', 'tokenBudget', 'token_budget', 'timeoutSeconds', 'timeout_seconds',
  'tasksUsed', 'tasks_used', 'childRunsUsed', 'child_runs_used', 'tokensUsed', 'tokens_used',
])

function object(value: unknown): JsonObject { return value !== null && typeof value === 'object' && !Array.isArray(value) ? value as JsonObject : {} }
function text(value: unknown): string | null { return typeof value === 'string' && value.length > 0 && value.length <= 160 ? value : null }
function integer(value: unknown): number | null { return typeof value === 'number' && Number.isSafeInteger(value) ? value : null }
function first(source: JsonObject, ...keys: string[]): unknown { for (const key of keys) if (Object.hasOwn(source, key)) return source[key]; return undefined }

/** Only scalar budget allowlist values are projected into the UI. */
export function safeOrchestratorBudget(run: AgentRun): Array<[string, string | number | boolean | null]> {
  return Object.entries(run.budget).filter(([key, value]) => BUDGET_KEYS.has(key) && (value === null || typeof value === 'string' || typeof value === 'number' || typeof value === 'boolean'))
}

function citations(value: unknown): Array<{ id: string; title: string | null }> {
  if (!Array.isArray(value)) return []
  return value.slice(0, 20).flatMap((candidate) => {
    const item = object(candidate)
    const id = text(first(item, 'id', 'citation_id', 'citationId', 'source_id', 'sourceId'))
    return id ? [{ id, title: text(first(item, 'title', 'name')) }] : []
  })
}

/**
 * Projects event payloads into a small, auditable trace model. Deliberately omit
 * root input/context/output, arbitrary event data, error details, snippets and URLs.
 */
export function toOrchestratorTraceEvent(event: AgentRunEvent): OrchestratorTraceEvent {
  const payload = object(event.payload)
  const childSource = object(first(payload, 'child', 'child_run', 'childRun'))
  const candidate = Object.keys(childSource).length > 0 ? childSource : payload
  const childId = text(first(candidate, 'child_id', 'childId', 'child_run_id', 'childRunId', 'run_id', 'runId'))
  const taskId = text(first(candidate, 'task_id', 'taskId'))
  const hasChild = childId !== null || taskId !== null || event.eventType.includes('child')
  const rootStatus = event.eventType === 'root_terminal' || event.eventType === 'run_cancelled'
    ? text(first(payload, 'status'))
    : null
  return {
    sequence: event.sequence,
    eventType: event.eventType,
    rootStatus,
    child: hasChild ? {
      childId,
      taskId,
      attempt: integer(first(candidate, 'attempt', 'attempt_number', 'attemptNumber')),
      kind: text(first(candidate, 'run_kind', 'runKind', 'kind')),
      agentId: text(first(candidate, 'agent_id', 'agentId')),
      agentRevision: integer(first(candidate, 'agent_revision', 'agentRevision')),
      status: text(first(candidate, 'status')),
      verdict: text(first(candidate, 'verdict')),
      citations: citations(first(candidate, 'citations')),
    } : null,
  }
}
