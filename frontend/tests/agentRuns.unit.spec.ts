import { expect, test } from 'vitest'
import {
  cancelAgentRun,
  getAgentRunEvents,
  normalizeAgentRun,
  normalizeAgentRunEventPage,
  resumeAgentRun,
  startAgentTestRun,
} from '../src/api/agentRuns'
import {
  isAmbiguousFailure,
  mergeRunEvents,
  sanitizeRunDisplay,
  withAcceptedCancelStatus,
} from '../src/agentRunDisplay'
import { ApiError } from '../src/api/http'
import {
  AGENT_RUN_ATTEMPT_STORAGE_PREFIX,
  clearLogicalAttemptStorage,
  LogicalAttemptKey,
  OPERATIONS_ATTEMPT_STORAGE_PREFIX,
} from '../src/logicalAttemptKey'

class MemoryStorage implements Storage {
  private readonly values = new Map<string, string>()

  get length(): number {
    return this.values.size
  }

  clear(): void {
    this.values.clear()
  }

  getItem(key: string): string | null {
    return this.values.get(key) ?? null
  }

  key(index: number): string | null {
    return [...this.values.keys()][index] ?? null
  }

  removeItem(key: string): void {
    this.values.delete(key)
  }

  setItem(key: string, value: string): void {
    this.values.set(key, value)
  }
}

test.describe('D3 Agent run public contracts', () => {
  test('keeps a logical-attempt key through retries and rotates on identity change or success', () => {
    let sequence = 0
    const attempts = new LogicalAttemptKey(() => `key-${++sequence}`)

    const first = attempts.keyFor(['agent-1', 'same message'])
    expect(attempts.keyFor(['agent-1', 'same message'])).toBe(first)
    expect(attempts.keyFor(['agent-1', 'changed message'])).not.toBe(first)

    const checkpointAttempt = attempts.keyFor(['run-1', 7, 'resume'])
    expect(attempts.keyFor(['run-1', 8, 'resume'])).not.toBe(checkpointAttempt)

    const successful = attempts.keyFor(['agent-2', 'done'])
    attempts.consume(['agent-2', 'done'], successful)
    expect(attempts.keyFor(['agent-2', 'done'])).not.toBe(successful)

    const explicitAttempt = attempts.keyFor(['agent-3', 'retry'])
    expect(attempts.rotate(['agent-3', 'retry'])).toBe(true)
    expect(attempts.keyFor(['agent-3', 'retry'])).not.toBe(explicitAttempt)
  })

  test('persists ambiguous attempts across remounts in one tab and cleans invalid or definitive state', () => {
    let sequence = 0
    const tabSession = new MemoryStorage()
    const namespace = 'springai-agent-runs:idempotency:start:agent-1'
    const identity = ['agent-1', 'retry me'] as const

    const mounted = new LogicalAttemptKey(() => `key-${++sequence}`, tabSession, namespace)
    const key = mounted.keyFor(identity)
    mounted.markAmbiguous(identity, key)

    const remounted = new LogicalAttemptKey(() => `key-${++sequence}`, tabSession, namespace)
    expect(remounted.keyFor(identity)).toBe(key)
    expect(remounted.isAmbiguous(identity)).toBe(true)
    expect(remounted.rotate(identity)).toBe(false)

    remounted.consume(identity, key)
    expect(tabSession.getItem(namespace)).toBeNull()
    expect(
      new LogicalAttemptKey(() => `key-${++sequence}`, tabSession, namespace).keyFor(identity),
    ).not.toBe(key)

    tabSession.setItem(namespace, '{"version":1,"key":42}')
    new LogicalAttemptKey(() => `key-${++sequence}`, tabSession, namespace)
    expect(tabSession.getItem(namespace)).toBeNull()
  })

  test('namespaces start, resume, and cancel attempts per tab session', () => {
    let sequence = 0
    const firstTab = new MemoryStorage()
    const secondTab = new MemoryStorage()
    const createKey = () => `key-${++sequence}`
    const cases = [
      ['start:agent-1', ['agent-1', 'message']],
      ['resume:run-1:7', ['run-1', 7, 'message']],
      ['cancel:run-1', ['run-1']],
    ] as const

    for (const [suffix, identity] of cases) {
      const namespace = `springai-agent-runs:idempotency:${suffix}`
      const first = new LogicalAttemptKey(createKey, firstTab, namespace).keyFor(identity)
      expect(new LogicalAttemptKey(createKey, firstTab, namespace).keyFor(identity)).toBe(first)
      expect(new LogicalAttemptKey(createKey, secondTab, namespace).keyFor(identity)).not.toBe(first)
    }
  })

  test('cleans Agent run and Operations attempt records at logout, nothing else', () => {
    const storage = new MemoryStorage()
    storage.setItem(`${AGENT_RUN_ATTEMPT_STORAGE_PREFIX}:start:agent-1`, '{}')
    storage.setItem(`${AGENT_RUN_ATTEMPT_STORAGE_PREFIX}:cancel:run-1`, '{}')
    storage.setItem(`${OPERATIONS_ATTEMPT_STORAGE_PREFIX}override-idempotency`, '{}')
    storage.setItem('unrelated', 'keep')

    clearLogicalAttemptStorage(storage)

    expect(storage.getItem(`${AGENT_RUN_ATTEMPT_STORAGE_PREFIX}:start:agent-1`)).toBeNull()
    expect(storage.getItem(`${AGENT_RUN_ATTEMPT_STORAGE_PREFIX}:cancel:run-1`)).toBeNull()
    expect(storage.getItem(`${OPERATIONS_ATTEMPT_STORAGE_PREFIX}override-idempotency`)).toBeNull()
    expect(storage.getItem('unrelated')).toBe('keep')
  })

  test('normalizes camelCase and snake_case run/event envelopes without exposing unknown fields', () => {
    expect(
      normalizeAgentRun({
        runId: 'run-camel',
        status: 'running',
        stateVersion: 2,
        checkpointVersion: 3,
        latestEventSequence: 4,
        pinnedAgentRevision: 7,
        pinnedWorkflowRevision: 9,
        pinnedSkills: [{ name: 'research', revision: 5 }],
        budget: { stepBudget: 20, nested: { hidden: true } },
        internalExecutionArtifact: 'must-not-render',
      }),
    ).toEqual(
      expect.objectContaining({
        runId: 'run-camel',
        status: 'running',
        stateVersion: 2,
        checkpointVersion: 3,
        latestEventSequence: 4,
        pinnedAgentRevision: 7,
        pinnedWorkflowRevision: 9,
        pinnedSkills: [{ name: 'research', revision: 5 }],
        budget: { stepBudget: 20 },
      }),
    )

    expect(
      normalizeAgentRun({
        run_id: 'run-snake',
        status: 'waiting_input',
        cancel_requested_at: '2026-07-25T00:00:00Z',
        checkpoint_version: 8,
        latest_event_sequence: 10,
        execution_snapshot: {
          agent_revision: 6,
          workflow_revision: 2,
        },
        skill_bindings: [{ skill: 'review', skill_revision: 3 }],
        runtime_limits: { token_budget: 4000, step_budget: 20 },
        pending_input: { question: '請提供案號' },
      }),
    ).toEqual(
      expect.objectContaining({
        runId: 'run-snake',
        status: 'cancelling',
        checkpointVersion: 8,
        pinnedAgentRevision: 6,
        pinnedWorkflowRevision: 2,
        pinnedSkills: [{ name: 'review', revision: 3 }],
        budget: { token_budget: 4000, step_budget: 20 },
        pendingInputMessage: '請提供案號',
      }),
    )

    expect(
      normalizeAgentRunEventPage({
        items: [
          { event_sequence: 2, event_type: 'skill.loaded', payload: { skill: 'review' } },
          { event_sequence: 'bad', event_type: 'ignored' },
        ],
        latest_event_sequence: 2,
      }),
    ).toEqual({
      events: [
        {
          sequence: 2,
          eventType: 'skill.loaded',
          createdAt: null,
          payload: { skill: 'review' },
        },
      ],
      latestEventSequence: 2,
    })
  })

  test('keeps a terminal status despite a cancel request, rejects a runId-less run, and maps object-form pinned skills', () => {
    expect(
      normalizeAgentRun({
        runId: 'run-terminal-camel',
        status: 'completed',
        cancelRequestedAt: '2026-07-25T00:00:00Z',
      }).status,
    ).toBe('completed')
    expect(
      normalizeAgentRun({
        run_id: 'run-terminal-snake',
        status: 'failed',
        cancel_requested_at: '2026-07-25T00:00:00Z',
      }).status,
    ).toBe('failed')

    expect(() => normalizeAgentRun({ status: 'running' })).toThrow('Run 回應缺少 runId。')

    expect(
      normalizeAgentRun({
        run_id: 'run-map',
        status: 'running',
        pinned_skills: { research: 5, broken: 'not-a-number' },
      }).pinnedSkills,
    ).toEqual([{ name: 'research', revision: 5 }])
  })

  test('accepts event sequence 0, drops negative sequences, and clamps a negative cursor to zero', async () => {
    expect(
      normalizeAgentRunEventPage({
        items: [
          { event_sequence: 0, event_type: 'run.started', payload: {} },
          { event_sequence: -1, event_type: 'ignored', payload: {} },
        ],
      }),
    ).toEqual({
      events: [{ sequence: 0, eventType: 'run.started', createdAt: null, payload: {} }],
      latestEventSequence: 0,
    })

    const originalFetch = globalThis.fetch
    const paths: string[] = []
    globalThis.fetch = async (input) => {
      paths.push(String(input))
      return new Response(JSON.stringify({ events: [], latestEventSequence: 0 }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      })
    }
    try {
      await getAgentRunEvents('run-1', -5)
      await getAgentRunEvents('run-1', 0)
    } finally {
      globalThis.fetch = originalFetch
    }

    expect(paths).toEqual([
      '/api/runs/run-1/events?afterSequence=0&limit=100',
      '/api/runs/run-1/events?afterSequence=0&limit=100',
    ])
  })

  test('uses polling paths, camelCase resume body, and Idempotency-Key for every command', async () => {
    const originalFetch = globalThis.fetch
    const requests: Array<{
      path: string
      method: string
      idempotencyKey: string | null
      body: unknown
    }> = []
    globalThis.fetch = async (input, init) => {
      requests.push({
        path: String(input),
        method: init?.method ?? 'GET',
        idempotencyKey: new Headers(init?.headers).get('Idempotency-Key'),
        body: init?.body ? JSON.parse(String(init.body)) : undefined,
      })
      const isEvents = String(input).includes('/events?')
      return new Response(
        JSON.stringify(
          isEvents
            ? { events: [], latestEventSequence: 0 }
            : {
                runId: 'run-1',
                status: 'running',
                checkpointVersion: 5,
                latestEventSequence: 0,
              },
        ),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      )
    }

    try {
      await startAgentTestRun('agent/id', 'hello', 'start-key')
      await getAgentRunEvents('run/id', 7)
      await resumeAgentRun('run/id', 'more context', 5, 'resume-key')
      await cancelAgentRun('run/id', 'cancel-key')
    } finally {
      globalThis.fetch = originalFetch
    }

    expect(requests).toEqual([
      {
        path: '/api/agents/agent%2Fid/runs',
        method: 'POST',
        idempotencyKey: 'start-key',
        body: { message: 'hello' },
      },
      {
        path: '/api/runs/run%2Fid/events?afterSequence=7&limit=100',
        method: 'GET',
        idempotencyKey: null,
        body: undefined,
      },
      {
        path: '/api/runs/run%2Fid/resume',
        method: 'POST',
        idempotencyKey: 'resume-key',
        body: { input: { message: 'more context' }, expectedCheckpointVersion: 5 },
      },
      {
        path: '/api/runs/run%2Fid/cancel',
        method: 'POST',
        idempotencyKey: 'cancel-key',
        body: undefined,
      },
    ])
  })

  test('deduplicates ordered events and redacts sensitive trace keys', () => {
    expect(
      mergeRunEvents(
        [
          { sequence: 2, eventType: 'old', createdAt: null, payload: {} },
          { sequence: 1, eventType: 'first', createdAt: null, payload: {} },
        ],
        [
          { sequence: 2, eventType: 'updated', createdAt: null, payload: {} },
          { sequence: 3, eventType: 'last', createdAt: null, payload: {} },
        ],
      ).map((event) => `${event.sequence}:${event.eventType}`),
    ).toEqual(['1:first', '2:updated', '3:last'])

    expect(
      sanitizeRunDisplay({
        accessToken: 'secret-value',
        nested: { api_key: 'also-secret', result: 'safe' },
      }),
    ).toEqual({
      accessToken: '[已遮罩]',
      nested: { api_key: '[已遮罩]', result: 'safe' },
    })
  })

  test('truncates long strings, stops at nesting depth, and caps collection length', () => {
    expect(sanitizeRunDisplay('x'.repeat(2000))).toBe('x'.repeat(2000))
    expect(sanitizeRunDisplay('x'.repeat(2001))).toBe(`${'x'.repeat(2000)}…`)

    expect(sanitizeRunDisplay({ a: { b: { c: { d: { e: { f: 'deep' } } } } } })).toEqual({
      a: { b: { c: { d: { e: { f: '[內容過深，已省略]' } } } } },
    })

    expect(sanitizeRunDisplay(Array.from({ length: 60 }, (_, index) => index))).toEqual(
      Array.from({ length: 50 }, (_, index) => index),
    )
  })

  test('treats network and 5xx failures as ambiguous and shows cancelling only before a terminal status', () => {
    expect(isAmbiguousFailure(new TypeError('network down'))).toBe(true)
    expect(isAmbiguousFailure(new ApiError(500, '伺服器錯誤'))).toBe(true)
    expect(isAmbiguousFailure(new ApiError(404, '找不到 Run'))).toBe(false)

    const running = normalizeAgentRun({ run_id: 'run-1', status: 'running' })
    expect(withAcceptedCancelStatus(running, true).status).toBe('cancelling')
    expect(withAcceptedCancelStatus(running, false)).toBe(running)

    const completed = normalizeAgentRun({ run_id: 'run-1', status: 'completed' })
    expect(withAcceptedCancelStatus(completed, true)).toBe(completed)
  })
})
