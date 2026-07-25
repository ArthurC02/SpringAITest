import type { AgentRunEvent } from './types'

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
