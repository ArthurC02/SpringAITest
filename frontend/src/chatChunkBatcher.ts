export interface ChatChunkScheduler {
  requestFrame(callback: () => void): number
  cancelFrame(id: number): void
  setDeadline(callback: () => void, delayMs: number): ReturnType<typeof setTimeout>
  clearDeadline(id: ReturnType<typeof setTimeout>): void
}

export interface ChatChunkBatcher {
  append(chunk: string): void
  flush(): void
  discard(): void
}

const browserScheduler: ChatChunkScheduler = {
  requestFrame: (callback) => window.requestAnimationFrame(callback),
  cancelFrame: (id) => window.cancelAnimationFrame(id),
  setDeadline: (callback, delayMs) => setTimeout(callback, delayMs),
  clearDeadline: (id) => clearTimeout(id),
}

/** Coalesces token bursts into at most one render per frame, with a 50ms fallback. */
export function createChatChunkBatcher(
  isCurrent: () => boolean,
  onFlush: (content: string) => void,
  scheduler: ChatChunkScheduler = browserScheduler,
): ChatChunkBatcher {
  let buffer = ''
  let frameId: number | null = null
  let deadlineId: ReturnType<typeof setTimeout> | null = null

  function cancelScheduled() {
    if (frameId !== null) scheduler.cancelFrame(frameId)
    if (deadlineId !== null) scheduler.clearDeadline(deadlineId)
    frameId = null
    deadlineId = null
  }

  function flush() {
    cancelScheduled()
    if (!buffer) return
    const content = buffer
    buffer = ''
    if (isCurrent()) onFlush(content)
  }

  return {
    append(chunk) {
      if (!chunk || !isCurrent()) return
      buffer += chunk
      if (frameId !== null || deadlineId !== null) return
      frameId = scheduler.requestFrame(flush)
      deadlineId = scheduler.setDeadline(flush, 50)
    },
    flush,
    discard() {
      cancelScheduled()
      buffer = ''
    },
  }
}
