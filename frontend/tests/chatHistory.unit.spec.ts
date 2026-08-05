import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  CHAT_HISTORY_PAGE_SIZE,
  historyItemsToMessages,
  mergeInitialMessages,
  mergeOlderMessages,
  parseChatHistoryPage,
} from '../src/chatHistory'
import { getChatHistoryPage } from '../src/api/chat'
import type { Message } from '../src/types'

const item = (id: number) => ({ id, reply: `reply-${id}`, createdAt: '2026-08-05T10:00:00Z' })

afterEach(() => {
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

describe('chat history page contract', () => {
  it('requests the bounded first and cursor pages through apiFetch', async () => {
    vi.stubGlobal('localStorage', { getItem: () => null })
    const fetch = vi.fn(async () => new Response(JSON.stringify({
      items: [item(2), item(1)], nextCursor: 'next', hasMore: true,
    }), { status: 200, headers: { 'Content-Type': 'application/json' } }))
    vi.stubGlobal('fetch', fetch)

    await getChatHistoryPage('opaque+/=')

    expect(fetch).toHaveBeenCalledTimes(1)
    expect(fetch.mock.calls[0][0]).toBe('/api/chat/history/page?limit=50&before=opaque%2B%2F%3D')
  })

  it('strictly validates envelope, cursor pairing, items, and page bound', () => {
    expect(parseChatHistoryPage({ items: [], nextCursor: null, hasMore: false }))
      .toEqual({ items: [], nextCursor: null, hasMore: false })
    expect(() => parseChatHistoryPage([])).toThrow('格式不正確')
    expect(() => parseChatHistoryPage({ items: [], nextCursor: null, hasMore: true }))
      .toThrow('格式不正確')
    expect(() => parseChatHistoryPage({ items: [item(0)], nextCursor: null, hasMore: false }))
      .toThrow('格式不正確')
    expect(() => parseChatHistoryPage({
      items: Array.from({ length: CHAT_HISTORY_PAGE_SIZE + 1 }, (_, index) => item(index + 1)),
      nextCursor: 'next',
      hasMore: true,
    })).toThrow('頁面上限')
  })

  it('reverses server DESC order and prepends with id dedupe without replacing local state', () => {
    const current: Message[] = [
      { id: 'server:2', role: 'assistant', content: 'cached server two' },
      { id: 'optimistic-user', role: 'user', content: 'new question' },
      { id: 'streaming', role: 'assistant', content: 'partial' },
    ]

    expect(historyItemsToMessages([item(2), item(1)]).map((message) => message.id))
      .toEqual(['server:1', 'server:2'])
    const merged = mergeOlderMessages(current, [item(2), item(1)])
    expect(merged.map((message) => message.id))
      .toEqual(['server:1', 'server:2', 'optimistic-user', 'streaming'])
    expect(merged.at(-1)?.content).toBe('partial')
    expect(current).toHaveLength(3)
  })

  it('merges the initial authority page after older cache and before the local suffix', () => {
    const current: Message[] = [
      ...Array.from({ length: 100 }, (_, index) => ({
        id: `server:${901 + index}`,
        role: 'assistant' as const,
        content: `cached-${901 + index}`,
      })),
      { id: 'optimistic-user', role: 'user', content: 'new question' },
      { id: 'streaming', role: 'assistant', content: 'partial' },
    ]
    const newest = Array.from({ length: 50 }, (_, index) => item(1050 - index))

    const merged = mergeInitialMessages(current, newest, true)

    expect(merged.slice(0, 150).map((message) => message.id))
      .toEqual(Array.from({ length: 150 }, (_, index) => `server:${901 + index}`))
    expect(merged.slice(-2)).toEqual(current.slice(-2))
  })

  it('uses initial server content for overlaps while keeping only the older cached prefix', () => {
    const current: Message[] = [
      ...Array.from({ length: 100 }, (_, index) => ({
        id: `server:${901 + index}`,
        role: 'assistant' as const,
        content: `stale-${901 + index}`,
      })),
      { id: 'server:901', role: 'assistant', content: 'duplicate-cache' },
      { id: 'local', role: 'assistant', content: 'streaming' },
    ]
    const page = [...Array.from({ length: 50 }, (_, index) => item(1000 - index)), item(951)]

    const merged = mergeInitialMessages(current, page, true)

    expect(merged.map((message) => message.id))
      .toEqual([
        ...Array.from({ length: 100 }, (_, index) => `server:${901 + index}`),
        'local',
      ])
    expect(merged.find((message) => message.id === 'server:951')?.content).toBe('reply-951')
    expect(merged.filter((message) => message.id === 'server:951')).toHaveLength(1)
  })

  it('clears every cached server row when an empty initial page is complete', () => {
    const current: Message[] = [
      { id: 'server:1', role: 'assistant', content: 'stale' },
      { id: 'local-user', role: 'user', content: 'question' },
      { id: 'local-stream', role: 'assistant', content: 'partial' },
    ]

    expect(mergeInitialMessages(current, [], false)).toEqual(current.slice(1))
  })

  it('uses a short complete initial page as the entire server authority', () => {
    const current: Message[] = [
      { id: 'server:1', role: 'assistant', content: 'stale older' },
      { id: 'server:2', role: 'assistant', content: 'stale overlap' },
      { id: 'server:99', role: 'assistant', content: 'stale newer' },
      { id: 'local', role: 'assistant', content: 'partial' },
    ]

    expect(mergeInitialMessages(current, [item(3), item(2)], false)).toEqual([
      { id: 'server:2', role: 'assistant', content: 'reply-2' },
      { id: 'server:3', role: 'assistant', content: 'reply-3' },
      current[3],
    ])
  })
})
