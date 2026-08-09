import { apiFetch } from './http'
import { integer, number, object, pick, text } from '../wire'
import type { RunChildProgress, RunDiscoveryPage, RunSummaryItem } from '../types'

/** 只有 6 個固定整數欄位有值才視為真的 child_progress;wire 上非協作 root 列是 null。 */
function normalizeChildProgress(value: unknown): RunChildProgress | null {
  const source = object(value)
  const total = integer(pick(source, 'total'))
  if (total === null) return null
  return {
    total,
    queued: integer(pick(source, 'queued')) ?? 0,
    running: integer(pick(source, 'running')) ?? 0,
    completed: integer(pick(source, 'completed')) ?? 0,
    failed: integer(pick(source, 'failed')) ?? 0,
    cancelled: integer(pick(source, 'cancelled')) ?? 0,
  }
}

/** 白名單投影:只接受契約列出的欄位,未列出的一律丟棄、不渲染。 */
export function normalizeRunSummaryItem(value: unknown): RunSummaryItem | null {
  const source = object(value)
  const id = text(pick(source, 'id'))
  if (!id) return null
  return {
    id,
    kind: text(pick(source, 'kind')) ?? 'unknown',
    status: text(pick(source, 'status')) ?? 'unknown',
    orchestratorRootRunId: text(pick(source, 'orchestrator_root_run_id')),
    taskId: text(pick(source, 'task_id')),
    agentId: text(pick(source, 'agent_id')),
    agentRevision: integer(pick(source, 'agent_revision')),
    orchestratorId: text(pick(source, 'orchestrator_id')),
    orchestratorRevision: integer(pick(source, 'orchestrator_revision')),
    workflowId: text(pick(source, 'workflow_id')) ?? '',
    workflowRevision: integer(pick(source, 'workflow_revision')) ?? 0,
    cancelRequested: pick(source, 'cancel_requested') === true,
    budgetSummary: pick(source, 'budget_summary') ?? null,
    lastEventType: text(pick(source, 'last_event_type')),
    lastEventAt: text(pick(source, 'last_event_at')),
    childProgress: normalizeChildProgress(pick(source, 'child_progress')),
    errorClass: text(pick(source, 'error_class')),
    pendingApproval: pick(source, 'pending_approval') === true,
    needsRecovery: pick(source, 'needs_recovery') === true,
    startedAt: text(pick(source, 'started_at')),
    createdAt: text(pick(source, 'created_at')) ?? '',
    updatedAt: text(pick(source, 'updated_at')) ?? '',
    completedAt: text(pick(source, 'completed_at')),
    elapsedSeconds: number(pick(source, 'elapsed_seconds')) ?? 0,
  }
}

export function normalizeRunDiscoveryPage(value: unknown): RunDiscoveryPage {
  const source = object(value)
  const raw = Array.isArray(value) ? value : (source.items as unknown[] | undefined) ?? []
  const items = raw.flatMap((item) => {
    const run = normalizeRunSummaryItem(item)
    return run ? [run] : []
  })
  return {
    items,
    hasMore: pick(source, 'has_more') === true,
    cursor: text(pick(source, 'cursor', 'next_cursor')),
  }
}

/**
 * GET /api/runs。RunDiscoveryController.cs 有 10 個獨立過濾維度,這裡只轉發畫面實際
 * 用到的 kind/status(規格 §「過濾」只要求狀態/種類下拉);其餘維度(agent_id、
 * created_from…)目前沒有呼叫端,YAGNI——要用再加,後端契約已就緒。
 */
export async function listRuns(
  options: { kind?: string; status?: string; cursor?: string | null; limit?: number } = {},
): Promise<RunDiscoveryPage> {
  const query = new URLSearchParams()
  if (options.kind) query.set('kind', options.kind)
  if (options.status) query.set('status', options.status)
  if (options.cursor) query.set('cursor', options.cursor)
  if (options.limit) query.set('limit', String(options.limit))
  const qs = query.toString()
  return normalizeRunDiscoveryPage(await apiFetch<unknown>(`/api/runs${qs ? `?${qs}` : ''}`))
}
