export interface GuardedChatRequest {
  controller: AbortController
}

interface GuardedChatRequestHandlers {
  onToken: (chunk: string) => void
  onAbort: () => void
  onError: (error: Error) => void
  onFinish: () => void
}

/** Keep late stream callbacks from mutating state after the request loses ownership. */
export async function runGuardedChatRequest(
  request: GuardedChatRequest,
  isCurrent: () => boolean,
  stream: (onToken: (chunk: string) => void) => Promise<void>,
  handlers: GuardedChatRequestHandlers,
): Promise<void> {
  try {
    await stream((chunk) => {
      if (isCurrent()) handlers.onToken(chunk)
    })
  } catch (cause) {
    if (!isCurrent()) return

    const error = cause instanceof Error ? cause : new Error(String(cause))
    if (request.controller.signal.aborted || error.name === 'AbortError') {
      handlers.onAbort()
    } else {
      handlers.onError(error)
    }
  } finally {
    if (isCurrent()) handlers.onFinish()
  }
}
