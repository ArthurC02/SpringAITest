import { apiFetch } from './http'
import { TERMINAL_RUN_STATUSES } from '../agentRunDisplay'
import { integer as finiteInteger, object, pick, text } from '../wire'
import type {
  AgentRun,
  AgentRunEvent,
  AgentRunEventPage,
  AgentRunPinnedSkill,
} from '../types'

function pinnedSkills(value: unknown): AgentRunPinnedSkill[] {
  if (Array.isArray(value)) {
    return value.flatMap((item) => {
      const entry = object(item)
      const name = text(pick(entry, 'name', 'skill', 'skillName', 'skill_name'))
      if (!name) return []
      return [{ name, revision: finiteInteger(pick(entry, 'revision', 'skillRevision', 'skill_revision')) }]
    })
  }
  const entries = object(value)
  return Object.entries(entries).flatMap(([name, revision]) => {
    const parsed = finiteInteger(revision)
    return parsed === null ? [] : [{ name, revision: parsed }]
  })
}

function safeBudget(value: unknown): Record<string, string | number | boolean | null> {
  const source = object(value)
  const result: Record<string, string | number | boolean | null> = {}
  for (const [key, item] of Object.entries(source)) {
    if (
      item === null ||
      typeof item === 'string' ||
      typeof item === 'number' ||
      typeof item === 'boolean'
    ) {
      result[key] = item
    }
  }
  return result
}

export function normalizeAgentRun(value: unknown): AgentRun {
  const source = object(value)
  const pinned = object(pick(source, 'pinned', 'snapshot', 'executionSnapshot', 'execution_snapshot'))
  const pending = object(pick(source, 'pendingInput', 'pending_input', 'waitingInput', 'waiting_input'))
  const runId = text(pick(source, 'runId', 'run_id', 'id'))
  if (!runId) throw new Error('Run 回應缺少 runId。')
  const rawStatus = text(pick(source, 'status')) ?? 'unknown'
  const cancelRequestedAt = text(pick(source, 'cancelRequestedAt', 'cancel_requested_at'))
  const status =
    cancelRequestedAt && !TERMINAL_RUN_STATUSES.has(rawStatus) ? 'cancelling' : rawStatus

  return {
    runId,
    status,
    stateVersion: finiteInteger(pick(source, 'stateVersion', 'state_version')),
    checkpointVersion: finiteInteger(
      pick(source, 'checkpointVersion', 'checkpoint_version', 'expectedCheckpointVersion'),
    ),
    latestEventSequence:
      finiteInteger(
        pick(source, 'latestEventSequence', 'latest_event_sequence', 'lastEventSequence'),
      ) ?? 0,
    pinnedAgentRevision: finiteInteger(
      pick(
        source,
        'pinnedAgentRevision',
        'pinned_agent_revision',
        'agentRevision',
        'agent_revision',
      ) ?? pick(pinned, 'agentRevision', 'agent_revision'),
    ),
    pinnedWorkflowRevision: finiteInteger(
      pick(
        source,
        'pinnedWorkflowRevision',
        'pinned_workflow_revision',
        'workflowRevision',
        'workflow_revision',
      ) ?? pick(pinned, 'workflowRevision', 'workflow_revision'),
    ),
    pinnedSkills: pinnedSkills(
      pick(
        source,
        'pinnedSkills',
        'pinned_skills',
        'skillRevisions',
        'skill_revisions',
        'skillBindings',
        'skill_bindings',
      ) ??
        pick(pinned, 'skills', 'skillRevisions', 'skill_revisions'),
    ),
    budget: safeBudget(
      pick(
        source,
        'budget',
        'budgets',
        'budgetUsage',
        'budget_usage',
        'runtimeLimits',
        'runtime_limits',
      ),
    ),
    pendingInputMessage:
      text(pick(source, 'pendingInputMessage', 'pending_input_message', 'clarification')) ??
      text(pick(pending, 'message', 'question', 'prompt')),
    output: pick(source, 'output', 'result', 'finalAnswer', 'final_answer'),
    error: text(pick(source, 'error', 'errorMessage', 'error_message', 'failureReason')),
    createdAt: text(pick(source, 'createdAt', 'created_at')),
    updatedAt: text(pick(source, 'updatedAt', 'updated_at')),
  }
}

function normalizeAgentRunEvent(value: unknown): AgentRunEvent | null {
  const source = object(value)
  const sequence = finiteInteger(pick(source, 'sequence', 'eventSequence', 'event_sequence'))
  if (sequence === null || sequence < 0) return null
  return {
    sequence,
    eventType: text(pick(source, 'eventType', 'event_type', 'type')) ?? 'unknown',
    createdAt: text(pick(source, 'createdAt', 'created_at', 'timestamp')),
    payload: pick(source, 'payload', 'data', 'details'),
  }
}

export function normalizeAgentRunEventPage(value: unknown): AgentRunEventPage {
  const source = object(value)
  const rawEvents = Array.isArray(value)
    ? value
    : (pick(source, 'events', 'items') as unknown[] | undefined) ?? []
  const events = rawEvents.flatMap((item) => {
    const event = normalizeAgentRunEvent(item)
    return event ? [event] : []
  })
  const newest = events.reduce((latest, event) => Math.max(latest, event.sequence), 0)
  return {
    events,
    latestEventSequence:
      finiteInteger(
        pick(source, 'latestEventSequence', 'latest_event_sequence', 'nextSequence', 'next_sequence'),
      ) ?? newest,
  }
}

export function newIdempotencyKey(): string {
  return globalThis.crypto?.randomUUID?.() ?? `run-${Date.now()}-${Math.random().toString(16).slice(2)}`
}

function commandHeaders(idempotencyKey: string): HeadersInit {
  return { 'Idempotency-Key': idempotencyKey }
}

export async function startAgentTestRun(
  agentId: string,
  message: string,
  idempotencyKey: string,
): Promise<AgentRun> {
  const response = await apiFetch<unknown>(
    `/api/agents/${encodeURIComponent(agentId)}/runs`,
    {
      method: 'POST',
      headers: commandHeaders(idempotencyKey),
      body: JSON.stringify({ message }),
    },
  )
  return normalizeAgentRun(response)
}

export async function getAgentRun(runId: string): Promise<AgentRun> {
  return normalizeAgentRun(
    await apiFetch<unknown>(`/api/runs/${encodeURIComponent(runId)}`),
  )
}

export async function getAgentRunEvents(
  runId: string,
  afterSequence: number,
): Promise<AgentRunEventPage> {
  const query = new URLSearchParams({
    afterSequence: String(Math.max(0, afterSequence)),
    limit: '100',
  })
  return normalizeAgentRunEventPage(
    await apiFetch<unknown>(`/api/runs/${encodeURIComponent(runId)}/events?${query}`),
  )
}

export async function resumeAgentRun(
  runId: string,
  message: string,
  expectedCheckpointVersion: number,
  idempotencyKey: string,
): Promise<AgentRun> {
  return normalizeAgentRun(
    await apiFetch<unknown>(`/api/runs/${encodeURIComponent(runId)}/resume`, {
      method: 'POST',
      headers: commandHeaders(idempotencyKey),
      body: JSON.stringify({
        input: { message },
        expectedCheckpointVersion,
      }),
    }),
  )
}

export async function cancelAgentRun(
  runId: string,
  idempotencyKey: string,
): Promise<AgentRun> {
  return normalizeAgentRun(
    await apiFetch<unknown>(`/api/runs/${encodeURIComponent(runId)}/cancel`, {
      method: 'POST',
      headers: commandHeaders(idempotencyKey),
    }),
  )
}
