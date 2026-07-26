import type { Page } from '@playwright/test'

export interface BrowserStreamRead {
  offsetMs: number
  bytes: number
}

/** A complete SSE event, timestamped when its terminating blank line arrives. */
export interface BrowserSseEvent {
  offsetMs: number
  event: string | null
  data: string
  /**
   * The literal text preceding the first `data` line's content — `data:` (no space) or
   * `data: ` (with space). Root AGENTS.md: the two SSE endpoints deliberately differ here
   * (`/api/chat/stream` vs AG-UI); this is what lets E-06 assert the byte-level framing
   * survived the browser fetch/ReadableStream path, not just curl's network-layer trace.
   */
  rawPrefix: string | null
}

export interface BrowserStreamEvidence {
  status: number
  responseAvailableMs: number
  completedMs: number
  chunks: BrowserStreamRead[]
  events: BrowserSseEvent[]
}

export interface BrowserStreamRequest {
  /** Same-origin path, normally the frontend evidence proxy or `/api/chat/stream`. */
  path: string
  method: string
  headers?: Record<string, string>
  body?: string
  /** Reads the already-authenticated browser session only in page memory; never writes it. */
  includeSessionBearer?: boolean
}

/** One event extracted from a raw SSE buffer by {@link parseSseBuffer}. */
export interface ParsedSseEvent {
  event: string | null
  data: string
  rawPrefix: string | null
}

/**
 * Pure SSE buffer parser (no DOM/fetch): given accumulated raw text, extracts every complete
 * event (terminated by a blank line, CRLF or LF) and returns whatever partial text is left over.
 * `readBrowserStream`'s `page.evaluate` callback below runs the identical algorithm inline —
 * Playwright serializes that callback into the browser realm, so it cannot import this function
 * directly, but the two must be kept in sync. This standalone copy exists so chunk-boundary,
 * CRLF-vs-LF and multi-line `data:` edge cases can be covered by a plain Node unit test instead
 * of only indirectly through the full three-service E-06 evidence gate.
 */
export function parseSseBuffer(buffer: string): { events: ParsedSseEvent[]; remaining: string } {
  let pending = buffer
  const events: ParsedSseEvent[] = []
  for (;;) {
    const separator = pending.match(/\r?\n\r?\n/)
    if (!separator || separator.index === undefined) break

    const rawEvent = pending.slice(0, separator.index)
    pending = pending.slice(separator.index + separator[0].length)
    const data: string[] = []
    let event: string | null = null
    let rawPrefix: string | null = null
    for (const line of rawEvent.split(/\r?\n/)) {
      if (line.startsWith('data:')) {
        const afterColon = line.slice(5)
        const content = afterColon.replace(/^ /, '')
        if (rawPrefix === null) rawPrefix = afterColon.length === content.length ? 'data:' : 'data: '
        data.push(content)
      } else if (line.startsWith('event:')) {
        event = line.slice(6).replace(/^ /, '')
      }
    }
    if (data.length > 0) events.push({ event, data: data.join('\n'), rawPrefix })
  }
  return { events, remaining: pending }
}

/**
 * Reads an SSE response in the browser execution context instead of relying on headers or
 * response completion. Events are emitted only after their terminating blank line arrives;
 * E-06 uses those completed-event times to detect proxy buffering.
 */
export async function readBrowserStream(
  page: Page,
  request: BrowserStreamRequest,
): Promise<BrowserStreamEvidence> {
  return page.evaluate(async ({ path, method, headers, body: requestBody, includeSessionBearer }) => {
    if (!path.startsWith('/')) {
      throw new Error('Evidence stream path must be same-origin and start with /')
    }
    const targetUrl = new URL(path, window.location.origin)
    if (targetUrl.origin !== window.location.origin) {
      throw new Error('Evidence stream must remain same-origin')
    }

    const requestHeaders = new Headers(headers)
    if (includeSessionBearer) {
      const rawSession = localStorage.getItem('springai:session')
      if (!rawSession) throw new Error('A browser session is required for this evidence stream')
      const { token } = JSON.parse(rawSession) as { token: string }
      requestHeaders.set('Authorization', `Bearer ${token}`)
    }

    const startedAt = performance.now()
    const response = await fetch(targetUrl, {
      method,
      body: requestBody,
      headers: requestHeaders,
    })
    const responseAvailableMs = performance.now() - startedAt
    if (!response.body) throw new Error('Evidence stream response has no readable body')

    const reader = response.body.getReader()
    const decoder = new TextDecoder()
    const chunks: BrowserStreamRead[] = []
    let pending = ''
    const events: BrowserSseEvent[] = []

    // Inline copy of parseSseBuffer() above (see its doc comment for why it can't be imported
    // here) — keep both in sync.
    const takeCompleteEvents = (receivedAtMs: number) => {
      for (;;) {
        const separator = pending.match(/\r?\n\r?\n/)
        if (!separator || separator.index === undefined) return

        const rawEvent = pending.slice(0, separator.index)
        pending = pending.slice(separator.index + separator[0].length)
        const data: string[] = []
        let event: string | null = null
        let rawPrefix: string | null = null
        for (const line of rawEvent.split(/\r?\n/)) {
          if (line.startsWith('data:')) {
            // SSE permits one optional space after the colon; retain all content after it,
            // but remember which exact prefix this line actually used.
            const afterColon = line.slice(5)
            const content = afterColon.replace(/^ /, '')
            if (rawPrefix === null) rawPrefix = afterColon.length === content.length ? 'data:' : 'data: '
            data.push(content)
          } else if (line.startsWith('event:')) {
            event = line.slice(6).replace(/^ /, '')
          }
        }
        if (data.length > 0) events.push({ offsetMs: receivedAtMs, event, data: data.join('\n'), rawPrefix })
      }
    }

    for (;;) {
      const { value, done } = await reader.read()
      if (done) break
      const receivedAtMs = performance.now() - startedAt
      chunks.push({ offsetMs: receivedAtMs, bytes: value.byteLength })
      pending += decoder.decode(value, { stream: true })
      takeCompleteEvents(receivedAtMs)
    }
    pending += decoder.decode()
    takeCompleteEvents(performance.now() - startedAt)
    return {
      status: response.status,
      responseAvailableMs,
      completedMs: performance.now() - startedAt,
      chunks,
      events,
    }
  }, request)
}
