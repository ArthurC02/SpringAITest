import { expect, test } from 'vitest'
import { parseSseBuffer } from '../e2e/helpers/streaming'

/**
 * Pure, no-browser/no-server coverage for the SSE chunk parser E-06 otherwise only exercises
 * indirectly through a real proxy + backend. A regression here (e.g. losing a separator split
 * across chunks, or collapsing the `data:`/`data: ` distinction) would previously surface only as
 * a confusing "proxy buffering" failure in the full-compose evidence gate.
 */
test.describe('parseSseBuffer', () => {
  test('a blank-line separator split across two chunks is not lost', () => {
    const first = parseSseBuffer('data:evidence\r\n')
    expect(first.events).toEqual([])
    expect(first.remaining).toBe('data:evidence\r\n')

    const second = parseSseBuffer(`${first.remaining}\r\n`)
    expect(second.events).toEqual([{ event: null, data: 'evidence', rawPrefix: 'data:' }])
    expect(second.remaining).toBe('')
  })

  test('CRLF and LF line endings terminate an event identically', () => {
    const crlf = parseSseBuffer('data: {"a":1}\r\n\r\n')
    const lf = parseSseBuffer('data: {"a":1}\n\n')
    expect(crlf.events).toEqual(lf.events)
    expect(crlf.events).toEqual([{ event: null, data: '{"a":1}', rawPrefix: 'data: ' }])
    expect(crlf.remaining).toBe('')
  })

  test('multiple data: lines in one event join with a newline; event: sets the event name', () => {
    const { events, remaining } = parseSseBuffer('event:custom\ndata:line one\ndata:line two\n\n')
    expect(remaining).toBe('')
    expect(events).toEqual([{ event: 'custom', data: 'line one\nline two', rawPrefix: 'data:' }])
  })

  test('a trailing partial event without its terminating blank line stays in remaining', () => {
    const { events, remaining } = parseSseBuffer('data:done\n\ndata:partial')
    expect(events).toEqual([{ event: null, data: 'done', rawPrefix: 'data:' }])
    expect(remaining).toBe('data:partial')
  })

  test('data: (no space) and data:  (with space) are reported distinctly', () => {
    const noSpace = parseSseBuffer('data:x\n\n')
    const withSpace = parseSseBuffer('data: x\n\n')
    expect(noSpace.events[0].rawPrefix).toBe('data:')
    expect(withSpace.events[0].rawPrefix).toBe('data: ')
  })

  test('a data-less frame (event: only) is consumed and dropped, not emitted', () => {
    const { events, remaining } = parseSseBuffer('event:ping\n\n')
    expect(events).toEqual([])
    expect(remaining).toBe('')
  })

  test('comment and id: lines are ignored instead of corrupting the event they precede', () => {
    const { events, remaining } = parseSseBuffer(': keep-alive\nid:1\ndata:x\n\n')
    expect(events).toEqual([{ event: null, data: 'x', rawPrefix: 'data:' }])
    expect(remaining).toBe('')
  })

  test('an empty buffer yields no events and nothing to carry over', () => {
    expect(parseSseBuffer('')).toEqual({ events: [], remaining: '' })
  })

  test('two complete events in one chunk are both extracted, not just the first', () => {
    const { events, remaining } = parseSseBuffer('data:a\n\ndata:b\n\n')
    expect(events).toEqual([
      { event: null, data: 'a', rawPrefix: 'data:' },
      { event: null, data: 'b', rawPrefix: 'data:' },
    ])
    expect(remaining).toBe('')
  })

  test('a CRLF frame carries a named event: alongside the space-prefixed data: variant', () => {
    const { events, remaining } = parseSseBuffer('event: RUN_STARTED\r\ndata: {"a":1}\r\n\r\n')
    expect(events).toEqual([{ event: 'RUN_STARTED', data: '{"a":1}', rawPrefix: 'data: ' }])
    expect(remaining).toBe('')
  })
})
