import { describe, expect, it } from 'vitest'
import {
  createChatChunkBatcher,
  type ChatChunkScheduler,
} from '../src/chatChunkBatcher'

function controlledScheduler() {
  let frame: (() => void) | null = null
  let deadline: (() => void) | null = null
  const scheduler: ChatChunkScheduler = {
    requestFrame(callback) { frame = callback; return 1 },
    cancelFrame() { frame = null },
    setDeadline(callback) { deadline = callback; return 1 as ReturnType<typeof setTimeout> },
    clearDeadline() { deadline = null },
  }
  return {
    scheduler,
    fireFrame() { const callback = frame; frame = null; callback?.() },
    fireDeadline() { const callback = deadline; deadline = null; callback?.() },
  }
}

describe('createChatChunkBatcher', () => {
  it.each([500, 5000])('coalesces %i chunks into one frame update', (count) => {
    const clock = controlledScheduler()
    const flushed: string[] = []
    const batcher = createChatChunkBatcher(() => true, (content) => flushed.push(content), clock.scheduler)

    for (let index = 0; index < count; index += 1) batcher.append('x')
    expect(flushed).toEqual([])

    clock.fireFrame()
    expect(flushed).toEqual(['x'.repeat(count)])
  })

  it('flushes at the 50ms deadline when no animation frame runs', () => {
    const clock = controlledScheduler()
    const flushed: string[] = []
    const batcher = createChatChunkBatcher(() => true, (content) => flushed.push(content), clock.scheduler)

    batcher.append('partial')
    clock.fireDeadline()

    expect(flushed).toEqual(['partial'])
  })

  it('synchronously flushes completion/error/abort content and drops stale ownership', () => {
    const clock = controlledScheduler()
    const flushed: string[] = []
    let current = true
    const batcher = createChatChunkBatcher(() => current, (content) => flushed.push(content), clock.scheduler)

    batcher.append('render before terminal callback')
    batcher.flush()
    expect(flushed).toEqual(['render before terminal callback'])

    batcher.append('stale')
    current = false
    batcher.flush()
    clock.fireFrame()
    expect(flushed).toEqual(['render before terminal callback'])
  })
})
