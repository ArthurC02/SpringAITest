import { ApiError } from './api/http'
import type { AgentRun, AgentRunEvent } from './types'

/** Run 只輪詢、不串流（D3/D5 契約），兩個主控台共用同一節奏。 */
export const POLL_MS = 1500
export const ACTIVE_RUN_STATUSES = new Set([
  'queued', 'pending', 'starting', 'running', 'resuming', 'cancelling',
])
export const TERMINAL_RUN_STATUSES = new Set(['completed', 'failed', 'cancelled', 'timed_out'])

/**
 * 非 ApiError（網路中斷）或 5xx 時，指令是否已被伺服器接受並不確定，
 * 必須沿用同一把 idempotency key 重試，不能換新 key。
 */
export function isAmbiguousFailure(error: unknown): boolean {
  return !(error instanceof ApiError) || error.status >= 500
}

/** 取消已被接受但伺服器尚未進終態時，顯示樂觀的 cancelling。 */
export function withAcceptedCancelStatus(run: AgentRun, accepted: boolean): AgentRun {
  return accepted && !TERMINAL_RUN_STATUSES.has(run.status) ? { ...run, status: 'cancelling' } : run
}

const SENSITIVE_KEY =
  /authorization|cookie|credential|password|secret|token|api[_-]?key|connection[_-]?string/i

/** Trace/API payload defense-in-depth. The server remains responsible for authorization and redaction. */
export function sanitizeRunDisplay(value: unknown, depth = 0): unknown {
  if (depth > 5) return '[內容過深，已省略]'
  if (typeof value === 'string') {
    return value.length > 2000 ? `${value.slice(0, 2000)}…` : value
  }
  if (value === null || typeof value === 'number' || typeof value === 'boolean') return value
  if (Array.isArray(value)) {
    return value.slice(0, 50).map((item) => sanitizeRunDisplay(item, depth + 1))
  }
  if (typeof value === 'object') {
    const result: Record<string, unknown> = {}
    for (const [key, item] of Object.entries(value as Record<string, unknown>).slice(0, 50)) {
      result[key] = SENSITIVE_KEY.test(key) ? '[已遮罩]' : sanitizeRunDisplay(item, depth + 1)
    }
    return result
  }
  return String(value)
}

export function mergeRunEvents(
  current: AgentRunEvent[],
  incoming: AgentRunEvent[],
): AgentRunEvent[] {
  const bySequence = new Map(current.map((event) => [event.sequence, event]))
  for (const event of incoming) bySequence.set(event.sequence, event)
  return [...bySequence.values()].sort((left, right) => left.sequence - right.sequence)
}
