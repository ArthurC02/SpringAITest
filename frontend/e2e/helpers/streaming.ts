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
            // SSE permits one optional space after the colon; retain all content after it.
            data.push(line.slice(5).replace(/^ /, ''))
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
