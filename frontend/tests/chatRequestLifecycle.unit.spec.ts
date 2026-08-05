import { describe, expect, it } from 'vitest'
import { runGuardedChatRequest, type GuardedChatRequest } from '../src/chatRequestLifecycle'

function deferredStream() {
  let onToken: (chunk: string) => void = () => {}
  let resolve!: () => void
  let reject!: (error: Error) => void
  const promise = new Promise<void>((done, fail) => {
    resolve = done
    reject = fail
  })

  return {
    stream: (handler: (chunk: string) => void) => {
      onToken = handler
      return promise
    },
    emit: (chunk: string) => onToken(chunk),
    resolve,
    reject,
  }
}

interface ObservedState {
  messages: string[]
  errors: string[]
  loading: boolean
}

function startRequest(
  request: GuardedChatRequest,
  getCurrent: () => GuardedChatRequest | null,
  stream: ReturnType<typeof deferredStream>,
  state: ObservedState,
) {
  return runGuardedChatRequest(
    request,
    () => getCurrent() === request,
    stream.stream,
    {
      onToken: (chunk) => state.messages.push(chunk),
      onAbort: () => state.errors.push('abort'),
      onError: (error) => state.errors.push(error.message),
      onFinish: () => {
        state.loading = false
      },
    },
  )
}

function initialState(): ObservedState {
  return { messages: [], errors: [], loading: true }
}

describe('runGuardedChatRequest', () => {
  it('ignores stale token and completion callbacks after a replacement starts', async () => {
    const oldRequest = { controller: new AbortController() }
    const newRequest = { controller: new AbortController() }
    let current: GuardedChatRequest | null = oldRequest
    const oldStream = deferredStream()
    const newStream = deferredStream()
    const state = initialState()

    const oldRun = startRequest(oldRequest, () => current, oldStream, state)
    current = newRequest
    const newRun = startRequest(newRequest, () => current, newStream, state)

    oldStream.emit('stale token')
    oldStream.resolve()
    await oldRun

    expect(state).toEqual(initialState())

    newStream.emit('current token')
    newStream.resolve()
    await newRun

    expect(state).toEqual({
      messages: ['current token'],
      errors: [],
      loading: false,
    })
  })

  it('ignores a stale catch and its finally callback after a replacement starts', async () => {
    const oldRequest = { controller: new AbortController() }
    const newRequest = { controller: new AbortController() }
    let current: GuardedChatRequest | null = oldRequest
    const stream = deferredStream()
    const state = initialState()
    const run = startRequest(oldRequest, () => current, stream, state)

    current = newRequest
    stream.reject(new Error('stale failure'))
    await run

    expect(state).toEqual(initialState())
  })

  it('ignores late token and completion callbacks after clear or unmount', async () => {
    const request = { controller: new AbortController() }
    let current: GuardedChatRequest | null = request
    const stream = deferredStream()
    const state = initialState()
    const run = startRequest(request, () => current, stream, state)

    current = null
    stream.emit('too late')
    stream.resolve()
    await run

    expect(state).toEqual(initialState())
  })
})
