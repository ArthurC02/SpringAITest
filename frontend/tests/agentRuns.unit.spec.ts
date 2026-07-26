import { expect, test } from 'vitest'
import {
  cancelAgentRun,
  getAgentRunEvents,
  normalizeAgentRun,
  normalizeAgentRunEventPage,
  resumeAgentRun,
  startAgentTestRun,
} from '../src/api/agentRuns'
import { mergeRunEvents, sanitizeRunDisplay } from '../src/agentRunDisplay'
import {
  AGENT_RUN_ATTEMPT_STORAGE_PREFIX,
  clearLogicalAttemptStorage,
  LogicalAttemptKey,
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

  test('cleans only Agent run attempt records at logout', () => {
    const storage = new MemoryStorage()
    storage.setItem(`${AGENT_RUN_ATTEMPT_STORAGE_PREFIX}:start:agent-1`, '{}')
    storage.setItem(`${AGENT_RUN_ATTEMPT_STORAGE_PREFIX}:cancel:run-1`, '{}')
    storage.setItem('unrelated', 'keep')

    clearLogicalAttemptStorage(storage)

    expect(storage.getItem(`${AGENT_RUN_ATTEMPT_STORAGE_PREFIX}:start:agent-1`)).toBeNull()
    expect(storage.getItem(`${AGENT_RUN_ATTEMPT_STORAGE_PREFIX}:cancel:run-1`)).toBeNull()
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
})
