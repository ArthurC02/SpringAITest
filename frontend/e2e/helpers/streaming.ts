import type { Page } from '@playwright/test'

export interface BrowserStreamRead {
  offsetMs: number
  bytes: number
}

/** A complete SSE event, timestamped when its terminating blank line arrives. */
export interface BrowserSseEvent {
  offsetMs: number
  event: string | null
  /**
   * The literal text immediately following `data:` for this line, joined with `\n` across
   * multi-line frames — never stripped of a leading space. SSE permits an optional single space
   * after `data:`, so a `data:` (no space) frame and a `data: ` (with space) frame are only
   * distinguishable by that leading byte; but a legitimate token can *itself* start with a space
   * (real tokenizer output, e.g. `infra/evidence/evidence_model.py`'s `" model"`), which makes
   * `data: model` genuinely ambiguous in isolation — `data:` + `" model"` and `data: ` + `"model"`
   * are byte-identical. This field therefore reports the raw text as-is and leaves resolving the
   * ambiguity to a caller that knows, from its own request, which literal prefix the endpoint it
   * called is contractually required to use (see `concatFramedContent` below).
   */
  data: string
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
    for (const line of rawEvent.split(/\r?\n/)) {
      if (line.startsWith('data:')) {
        data.push(line.slice(5))
      } else if (line.startsWith('event:')) {
        event = line.slice(6).replace(/^ /, '')
      }
    }
    if (data.length > 0) events.push({ event, data: data.join('\n') })
  }
  return { events, remaining: pending }
}

/**
 * Concatenates each frame's literal `data:`-suffix content, in order, under the exact framing
 * the caller declares for its own endpoint — never inferred from a frame's own bytes (see
 * {@link BrowserSseEvent.data}). `chat` strips nothing (`/api/chat/stream` writes
 * `data:<content>`, no separator space). `agui` strips exactly one leading character
 * unconditionally (AG-UI writes `data: <json>`); if the server omitted that space, this
 * corrupts the JSON on purpose so `JSON.parse` fails loudly instead of silently tolerating the
 * wrong framing. Callers pass only already-selected controlled-content frames (root
 * AGENTS.md's two SSE formats) — lifecycle/error/`[DONE]` frames must be filtered out first.
 */
export function concatFramedContent(
  frames: { data: string }[],
  streamKind: 'chat' | 'agui',
): string | null {
  const parts: string[] = []
  for (const frame of frames) {
    if (streamKind === 'chat') {
      parts.push(frame.data)
      continue
    }
    try {
      const payload = JSON.parse(frame.data.slice(1)) as { delta?: unknown }
      if (typeof payload.delta !== 'string') return null
      parts.push(payload.delta)
    } catch {
      return null
    }
  }
  return parts.join('')
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
        for (const line of rawEvent.split(/\r?\n/)) {
          if (line.startsWith('data:')) {
            // SSE permits one optional space after the colon; retain all content after it as-is
            // (never strip a leading space here — see BrowserSseEvent.data doc comment).
            data.push(line.slice(5))
          } else if (line.startsWith('event:')) {
            event = line.slice(6).replace(/^ /, '')
          }
        }
        if (data.length > 0) events.push({ offsetMs: receivedAtMs, event, data: data.join('\n') })
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
