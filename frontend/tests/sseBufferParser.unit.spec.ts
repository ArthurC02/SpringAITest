import { expect, test } from 'vitest'
import { concatFramedContent, parseSseBuffer } from '../e2e/helpers/streaming'

/**
 * Pure, no-browser/no-server coverage for the SSE chunk parser E-06 otherwise only exercises
 * indirectly through a real proxy + backend. A regression here (e.g. losing a separator split
 * across chunks) would previously surface only as a confusing "proxy buffering" failure in the
 * full-compose evidence gate.
 */
test.describe('parseSseBuffer', () => {
  test('a blank-line separator split across two chunks is not lost', () => {
    const first = parseSseBuffer('data:evidence\r\n')
    expect(first.events).toEqual([])
    expect(first.remaining).toBe('data:evidence\r\n')

    const second = parseSseBuffer(`${first.remaining}\r\n`)
    expect(second.events).toEqual([{ event: null, data: 'evidence' }])
    expect(second.remaining).toBe('')
  })

  test('CRLF and LF line endings terminate an event identically', () => {
    const crlf = parseSseBuffer('data: {"a":1}\r\n\r\n')
    const lf = parseSseBuffer('data: {"a":1}\n\n')
    expect(crlf.events).toEqual(lf.events)
    expect(crlf.events).toEqual([{ event: null, data: ' {"a":1}' }])
    expect(crlf.remaining).toBe('')
  })

  test('multiple data: lines in one event join with a newline; event: sets the event name', () => {
    const { events, remaining } = parseSseBuffer('event:custom\ndata:line one\ndata:line two\n\n')
    expect(remaining).toBe('')
    expect(events).toEqual([{ event: 'custom', data: 'line one\nline two' }])
  })

  test('a trailing partial event without its terminating blank line stays in remaining', () => {
    const { events, remaining } = parseSseBuffer('data:done\n\ndata:partial')
    expect(events).toEqual([{ event: null, data: 'done' }])
    expect(remaining).toBe('data:partial')
  })

  test('data: (no space) and data:  (with space) report distinct raw content, unstripped', () => {
    const noSpace = parseSseBuffer('data:x\n\n')
    const withSpace = parseSseBuffer('data: x\n\n')
    expect(noSpace.events[0].data).toBe('x')
    expect(withSpace.events[0].data).toBe(' x')
  })

  test('a data-less frame (event: only) is consumed and dropped, not emitted', () => {
    const { events, remaining } = parseSseBuffer('event:ping\n\n')
    expect(events).toEqual([])
    expect(remaining).toBe('')
  })

  test('comment and id: lines are ignored instead of corrupting the event they precede', () => {
    const { events, remaining } = parseSseBuffer(': keep-alive\nid:1\ndata:x\n\n')
    expect(events).toEqual([{ event: null, data: 'x' }])
    expect(remaining).toBe('')
  })

  test('an empty buffer yields no events and nothing to carry over', () => {
    expect(parseSseBuffer('')).toEqual({ events: [], remaining: '' })
  })

  test('two complete events in one chunk are both extracted, not just the first', () => {
    const { events, remaining } = parseSseBuffer('data:a\n\ndata:b\n\n')
    expect(events).toEqual([
      { event: null, data: 'a' },
      { event: null, data: 'b' },
    ])
    expect(remaining).toBe('')
  })

  test('a CRLF frame carries a named event: alongside the space-prefixed data: variant', () => {
    const { events, remaining } = parseSseBuffer('event: RUN_STARTED\r\ndata: {"a":1}\r\n\r\n')
    expect(events).toEqual([{ event: 'RUN_STARTED', data: ' {"a":1}' }])
    expect(remaining).toBe('')
  })
})

/**
 * `data: model` is genuinely ambiguous as a single line in isolation — SSE's optional space
 * after `data:` and a real tokenizer's own leading-space token can produce byte-identical text
 * (see infra/evidence/evidence_model.py's `(" model", " reply")` and root AGENTS.md's two SSE
 * formats). These cases are the regression E-06 hit: a per-frame prefix guess misread a real
 * `chat`-framed reply as AG-UI framing. `concatFramedContent` must resolve the ambiguity from the
 * caller-declared `streamKind`, not from the bytes, so it is exercised directly here rather than
 * only through the full evidence gate.
 */
test.describe('concatFramedContent', () => {
  test('chat framing: correctly-framed frames reconstruct the controlled reply verbatim', () => {
    const { events } = parseSseBuffer('data:evidence\n\ndata: model\n\ndata: reply\n\n')
    expect(concatFramedContent(events, 'chat')).toBe('evidence model reply')
  })

  test('chat framing: AG-UI-style `data: ` framing on a chat case surfaces as extra bytes, not a false pass', () => {
    // A server that mistakenly wrote `data: ` for every chat frame (instead of `data:`) would
    // still parse and select as content, but every frame gained an extra byte of framing space
    // on top of any token that already carries its own leading space — the reconstruction must
    // therefore not equal the known controlled reply.
    const { events } = parseSseBuffer('data: evidence\n\ndata: model\n\n')
    expect(concatFramedContent(events, 'chat')).not.toBe('evidence model')
  })

  test('agui framing: strips exactly one leading space before parsing JSON deltas', () => {
    const { events } = parseSseBuffer(
      'data: {"type":"TEXT_MESSAGE_CONTENT","delta":"evidence"}\n\n'
      + 'data: {"type":"TEXT_MESSAGE_CONTENT","delta":" model"}\n\n'
      + 'data: {"type":"TEXT_MESSAGE_CONTENT","delta":" reply"}\n\n',
    )
    expect(concatFramedContent(events, 'agui')).toBe('evidence model reply')
  })

  test('agui framing: a frame missing its contractual leading space fails to parse instead of silently passing', () => {
    const { events } = parseSseBuffer('data:{"type":"TEXT_MESSAGE_CONTENT","delta":"evidence"}\n\n')
    expect(concatFramedContent(events, 'agui')).toBeNull()
  })
})
