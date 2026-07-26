import { integer, object, pick, shortText } from './wire'
import type { AgentRun, AgentRunEvent } from './types'

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

/** Only scalar budget allowlist values are projected into the UI. */
export function safeOrchestratorBudget(run: AgentRun): Array<[string, string | number | boolean | null]> {
  return Object.entries(run.budget).filter(([key, value]) => BUDGET_KEYS.has(key) && (value === null || typeof value === 'string' || typeof value === 'number' || typeof value === 'boolean'))
}

function citations(value: unknown): Array<{ id: string; title: string | null }> {
  if (!Array.isArray(value)) return []
  return value.slice(0, 20).flatMap((candidate) => {
    const item = object(candidate)
    const id = shortText(pick(item, 'id', 'citation_id', 'citationId', 'source_id', 'sourceId'))
    return id ? [{ id, title: shortText(pick(item, 'title', 'name')) }] : []
  })
}

/**
 * Projects event payloads into a small, auditable trace model. Deliberately omit
 * root input/context/output, arbitrary event data, error details, snippets and URLs.
 */
export function toOrchestratorTraceEvent(event: AgentRunEvent): OrchestratorTraceEvent {
  const payload = object(event.payload)
  const childSource = object(pick(payload, 'child', 'child_run', 'childRun'))
  const candidate = Object.keys(childSource).length > 0 ? childSource : payload
  const childId = shortText(pick(candidate, 'child_id', 'childId', 'child_run_id', 'childRunId', 'run_id', 'runId'))
  const taskId = shortText(pick(candidate, 'task_id', 'taskId'))
  const hasChild = childId !== null || taskId !== null || event.eventType.includes('child')
  const rootStatus = event.eventType === 'root_terminal' || event.eventType === 'run_cancelled'
    ? shortText(pick(payload, 'status'))
    : null
  return {
    sequence: event.sequence,
    eventType: event.eventType,
    rootStatus,
    child: hasChild ? {
      childId,
      taskId,
      attempt: integer(pick(candidate, 'attempt', 'attempt_number', 'attemptNumber')),
      kind: shortText(pick(candidate, 'run_kind', 'runKind', 'kind')),
      agentId: shortText(pick(candidate, 'agent_id', 'agentId')),
      agentRevision: integer(pick(candidate, 'agent_revision', 'agentRevision')),
      status: shortText(pick(candidate, 'status')),
      verdict: shortText(pick(candidate, 'verdict')),
      citations: citations(pick(candidate, 'citations')),
    } : null,
  }
}
