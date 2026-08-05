import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  capPersistedChatMessages,
  getChatPersistenceGeneration,
  loadPersistedChatMessages,
  MAX_PERSISTED_CHAT_BYTES,
  persistChatMessages,
} from '../src/chatPersistence'
import type { Message } from '../src/types'

function message(index: number, content = `message-${index}`): Message {
  return { id: `m-${index}`, role: index % 2 ? 'assistant' : 'user', content }
}

afterEach(() => {
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

describe('chat persistence governance', () => {
  it('loads/persists only the newest 100 of 1000 messages without mutating visible state', () => {
    const visible = Array.from({ length: 1000 }, (_, index) => message(index))
    const capped = capPersistedChatMessages(visible)

    expect(visible).toHaveLength(1000)
    expect(capped).toHaveLength(100)
    expect(capped[0].id).toBe('m-900')
    expect(capped.at(-1)?.id).toBe('m-999')
  })

  it('uses the byte boundary before the message-count boundary', () => {
    const almostHalfMiB = '界'.repeat(170_000)
    const capped = capPersistedChatMessages([
      message(1, almostHalfMiB),
      message(2, almostHalfMiB),
      message(3, almostHalfMiB),
    ])

    expect(capped.map((item) => item.id)).toEqual(['m-2', 'm-3'])
    expect(new TextEncoder().encode(JSON.stringify(capped)).byteLength)
      .toBeLessThanOrEqual(MAX_PERSISTED_CHAT_BYTES)
  })

  it('returns a content-free quota signal without throwing', () => {
    const setItem = vi.fn(() => { throw new DOMException('full', 'QuotaExceededError') })
    vi.stubGlobal('localStorage', { setItem })

    const result = persistChatMessages([message(1, 'secret')], getChatPersistenceGeneration())

    expect(result).toBe('quota_failed')
    expect(setItem).toHaveBeenCalledTimes(1)
  })

  it('rejects oversized raw cache before JSON.parse and removes it', () => {
    const raw = 'x'.repeat(MAX_PERSISTED_CHAT_BYTES + 1)
    const removeItem = vi.fn()
    vi.stubGlobal('localStorage', { getItem: () => raw, removeItem })
    const parse = vi.spyOn(JSON, 'parse')

    expect(loadPersistedChatMessages()).toEqual([])
    expect(parse).not.toHaveBeenCalled()
    expect(removeItem).toHaveBeenCalledTimes(1)
  })

  it('removes parsed cache with an invalid message shape', () => {
    const removeItem = vi.fn()
    vi.stubGlobal('localStorage', {
      getItem: () => JSON.stringify([{ id: 'm-1', role: 'system', content: 'invalid' }]),
      removeItem,
    })

    expect(loadPersistedChatMessages()).toEqual([])
    expect(removeItem).toHaveBeenCalledTimes(1)
  })

  it('accepts an exact UTF-8 byte boundary and rejects one multibyte character over it', () => {
    const emptyRaw = JSON.stringify([message(1, '')])
    const contentBytes = MAX_PERSISTED_CHAT_BYTES - new TextEncoder().encode(emptyRaw).byteLength
    const exactContent = `${'a'.repeat(contentBytes - 3)}界`
    const exactRaw = JSON.stringify([message(1, exactContent)])
    expect(new TextEncoder().encode(exactRaw).byteLength).toBe(MAX_PERSISTED_CHAT_BYTES)

    const removeItem = vi.fn()
    let raw = exactRaw
    vi.stubGlobal('localStorage', { getItem: () => raw, removeItem })
    expect(loadPersistedChatMessages()).toHaveLength(1)

    raw = JSON.stringify([message(1, `${exactContent}界`)])
    expect(loadPersistedChatMessages()).toEqual([])
    expect(removeItem).toHaveBeenCalledTimes(1)
  })
})
