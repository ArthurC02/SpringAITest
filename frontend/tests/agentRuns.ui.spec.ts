import { expect, test, type Route } from '@playwright/test'

const agentId = '22222222-2222-4222-8222-222222222222'
const runOne = '33333333-3333-4333-8333-333333333333'
const runTwo = '44444444-4444-4444-8444-444444444444'
const runThree = '55555555-5555-4555-8555-555555555555'
const runFour = '66666666-6666-4666-8666-666666666666'

const publishedAgent = {
  id: agentId,
  slug: 'published-agent',
  name: 'Published Agent',
  description: 'D3 test target',
  enabled: true,
  draft_version: 4,
  draft_validated_version: null,
  published_revision: 3,
  created_at: '2026-07-24T00:00:00Z',
  updated_at: '2026-07-25T00:00:00Z',
  draft: {
    system_prompt: 'Help safely.',
    execution_roles: ['worker'],
    capabilities: ['analysis'],
    output_contract: {},
    audience: ['ADMIN'],
    allowed_tools: [],
    skill_bindings: [],
    knowledge_sources: [],
    business_rules: { version: 1, rules: [] },
    runtime_limits: {
      max_tool_rounds: 3,
      max_context_rounds: 2,
      timeout_seconds: 90,
      token_budget: 4000,
      step_budget: 20,
    },
    runtime_workflow: {
      id: '00000000-0000-4000-8000-000000000001',
      revision: 2,
    },
  },
}

async function json(
  route: Route,
  body: unknown,
  options: { status?: number; headers?: Record<string, string> } = {},
) {
  await route.fulfill({
    status: options.status ?? 200,
    contentType: 'application/json',
    headers: options.headers,
    body: JSON.stringify(body),
  })
}

async function loginAndOpenAgent(page: import('@playwright/test').Page) {
  await page.goto('/')
  const username = page.getByTestId('auth-username')
  if (await username.isVisible({ timeout: 5000 }).catch(() => false)) {
    await username.fill('admin')
    await page.getByTestId('auth-password').fill('password123')
    await page.getByTestId('auth-submit').click()
  }
  await page.getByTestId('nav-agents').click()
  await page.getByRole('button', { name: '編輯' }).click()
}

test('published Agent test console polls, resumes waiting_input, redacts trace, and cancels', async ({
  page,
}) => {
  let phase: 'idle' | 'waiting' | 'completed' | 'second' | 'cancelled' = 'idle'
  let starts = 0
  const resumeRequests: Array<{ key: string | undefined; body: unknown }> = []
  const cancelKeys: Array<string | undefined> = []
  const eventCursors: string[] = []
  const startKeys: string[] = []

  await page.route('**/api/**', async (route) => {
    const request = route.request()
    const url = new URL(request.url())
    const path = url.pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') {
      return json(route, {
        token: 'run-ui-token',
        username: 'admin',
        role: 'ADMIN',
        tenantCode: 'demo',
      })
    }
    if (path === '/api/features') {
      return json(route, { agentBuilderEnabled: true, agentTestRunEnabled: true })
    }
    if (path === '/api/documents' || path === '/api/chat/history') return json(route, [])
    if (path === '/api/agents' && request.method() === 'GET') {
      return json(route, [publishedAgent])
    }
    if (path === `/api/agents/${agentId}` && request.method() === 'GET') {
      return json(route, publishedAgent, { headers: { ETag: '"4"' } })
    }
    if (path === `/api/agents/${agentId}/revisions`) return json(route, [])
    if (path === '/api/skills/catalog' || path === '/api/tools') return json(route, [])
    if (path === '/api/agents/catalog/rule-facts') {
      return json(route, { facts: [], gates: ['pre-action'], operators: [], limits: { maxDepth: 3 } })
    }
    if (path === '/api/agents/catalog/rule-actions') return json(route, { actions: [] })

    if (path === `/api/agents/${agentId}/runs` && request.method() === 'POST') {
      starts += 1
      startKeys.push(request.headers()['idempotency-key'])
      const expectedMessage =
        starts === 1 ? 'Need context' : starts === 2 ? 'Cancel me' : 'Fail safely'
      expect(request.postDataJSON()).toEqual({ message: expectedMessage })
      if (starts === 3) {
        return json(
          route,
          {
            timestamp: '2026-07-25T00:00:00Z',
            status: 409,
            message: '已有進行中的測試 Run',
            fieldErrors: {},
          },
          { status: 409 },
        )
      }
      phase = starts === 1 ? 'waiting' : 'second'
      return json(
        route,
        {
          runId: starts === 1 ? runOne : runTwo,
          status: 'running',
          stateVersion: 1,
          checkpointVersion: 1,
          latestEventSequence: 0,
          pinnedAgentRevision: 3,
          pinnedWorkflowRevision: 2,
          pinnedSkills: [{ name: 'review', revision: 6 }],
          budget: { stepBudget: 20, stepsUsed: 1 },
        },
        { status: 202 },
      )
    }

    if (path === `/api/runs/${runOne}` && request.method() === 'GET') {
      return json(route, {
        runId: runOne,
        status: phase === 'completed' ? 'completed' : 'waiting_input',
        stateVersion: phase === 'completed' ? 3 : 2,
        checkpointVersion: 7,
        latestEventSequence: phase === 'completed' ? 2 : 1,
        pinnedAgentRevision: 3,
        pinnedWorkflowRevision: 2,
        pendingInput: phase === 'completed' ? null : { message: '請提供案號' },
        output: phase === 'completed' ? { answer: 'Done' } : undefined,
      })
    }
    if (path === `/api/runs/${runTwo}` && request.method() === 'GET') {
      return json(route, {
        runId: runTwo,
        status: phase === 'cancelled' ? 'cancelled' : 'running',
        stateVersion: phase === 'cancelled' ? 2 : 1,
        checkpointVersion: 1,
        latestEventSequence: 1,
        pinnedAgentRevision: 3,
        pinnedWorkflowRevision: 2,
      })
    }
    if (path === `/api/runs/${runOne}/events`) {
      const cursor = url.searchParams.get('afterSequence') ?? ''
      eventCursors.push(cursor)
      return json(route, {
        events:
          cursor === '0'
            ? [
                {
                  sequence: 1,
                  eventType: 'waiting.input',
                  payload: {
                    note: 'safe trace',
                    authorization: 'Bearer must-not-render',
                  },
                },
              ]
            : phase === 'completed' && cursor === '1'
              ? [{ sequence: 2, eventType: 'run.completed', payload: { result: 'Done' } }]
              : [],
        latestEventSequence: phase === 'completed' ? 2 : 1,
      })
    }
    if (path === `/api/runs/${runTwo}/events`) {
      eventCursors.push(url.searchParams.get('afterSequence') ?? '')
      return json(route, {
        events: [{ sequence: 1, eventType: 'run.started', payload: {} }],
        latestEventSequence: 1,
      })
    }
    if (path === `/api/runs/${runOne}/resume` && request.method() === 'POST') {
      resumeRequests.push({
        key: request.headers()['idempotency-key'],
        body: request.postDataJSON(),
      })
      if (resumeRequests.length === 1) {
        return json(
          route,
          {
            timestamp: '2026-07-25T00:00:00Z',
            status: 502,
            message: 'resume outcome unknown',
            fieldErrors: {},
          },
          { status: 502 },
        )
      }
      if (resumeRequests.length === 3) phase = 'completed'
      return json(
        route,
        {
          runId: runOne,
          status: resumeRequests.length === 2 ? 'waiting_input' : 'running',
          stateVersion: 3,
          checkpointVersion: 7,
          latestEventSequence: 1,
        },
        { status: 202 },
      )
    }
    if (path === `/api/runs/${runTwo}/cancel` && request.method() === 'POST') {
      cancelKeys.push(request.headers()['idempotency-key'])
      expect(request.postData()).toBeNull()
      if (cancelKeys.length === 1) {
        return json(
          route,
          {
            timestamp: '2026-07-25T00:00:00Z',
            status: 502,
            message: 'cancel outcome unknown',
            fieldErrors: {},
          },
          { status: 502 },
        )
      }
      phase = 'cancelled'
      return json(
        route,
        {
          runId: runTwo,
          status: 'cancelled',
          stateVersion: 2,
          checkpointVersion: 1,
          latestEventSequence: 1,
        },
        { status: 202 },
      )
    }
    return json(route, [])
  })

  await loginAndOpenAgent(page)
  await page.getByRole('button', { name: '測試 Run' }).click()
  await expect(page.getByRole('heading', { name: 'Direct Agent 測試主控台' })).toBeVisible()
  await expect(page.getByText('不使用串流、不提供核准，也不開放寫入工具')).toBeVisible()

  await page.getByLabel('測試訊息').fill('Need context')
  await page.getByRole('button', { name: '啟動測試 Run' }).click()
  await expect(page.getByText('等待補充資訊', { exact: true })).toBeVisible()
  await expect(page.getByText('請提供案號')).toBeVisible()
  await expect(page.getByText('Pinned Agent').locator('..')).toContainText('r3')
  await expect(page.getByText('Pinned Workflow').locator('..')).toContainText('r2')
  await expect(page.getByText('safe trace')).toBeVisible()
  await expect(page.getByText('Bearer must-not-render')).toHaveCount(0)
  await expect(page.getByText('[已遮罩]')).toBeVisible()

  await page.getByLabel('補充資訊').fill('CASE-42')
  await page.getByRole('button', { name: '從 checkpoint 恢復' }).click()
  await expect(page.getByRole('alert')).toContainText('resume outcome unknown')
  await expect(page.getByRole('button', { name: '以新嘗試重送 Resume' })).toBeDisabled()
  await page.getByRole('button', { name: '從 checkpoint 恢復' }).click()
  await expect.poll(() => resumeRequests).toHaveLength(2)
  expect(resumeRequests[0]).toEqual({
    key: expect.any(String),
    body: { input: { message: 'CASE-42' }, expectedCheckpointVersion: 7 },
  })
  expect(resumeRequests[1].key).toBe(resumeRequests[0].key)

  await page.getByLabel('補充資訊').fill('CASE-42')
  await page.getByRole('button', { name: '從 checkpoint 恢復' }).click()
  await expect.poll(() => resumeRequests).toHaveLength(3)
  expect(resumeRequests[2].key).not.toBe(resumeRequests[1].key)
  await expect(page.getByText('已完成', { exact: true })).toBeVisible()
  await expect(page.getByText('"answer": "Done"')).toBeVisible()

  await page.getByLabel('測試訊息').fill('Cancel me')
  await page.getByRole('button', { name: '啟動測試 Run' }).click()
  await expect(page.getByText(runTwo, { exact: true })).toBeVisible()
  await page.getByRole('button', { name: '取消 Run', exact: true }).click()
  await page
    .getByRole('dialog', { name: '確認操作' })
    .getByRole('button', { name: '取消 Run' })
    .click()
  await expect(page.getByRole('alert')).toContainText('cancel outcome unknown')
  await page.getByRole('button', { name: '取消 Run', exact: true }).click()
  await page
    .getByRole('dialog', { name: '確認操作' })
    .getByRole('button', { name: '取消 Run' })
    .click()
  await expect(page.getByText('已取消', { exact: true })).toBeVisible()

  await page.getByLabel('測試訊息').fill('Fail safely')
  await page.getByRole('button', { name: '啟動測試 Run' }).click()
  await expect(page.getByRole('alert')).toContainText('已有進行中的測試 Run')
  await expect(page.getByText('已取消', { exact: true })).toBeVisible()

  expect(startKeys).toHaveLength(3)
  expect(startKeys.every(Boolean)).toBe(true)
  expect(cancelKeys).toHaveLength(2)
  expect(cancelKeys[0]).toEqual(expect.any(String))
  expect(cancelKeys[1]).toBe(cancelKeys[0])
  expect(eventCursors).toContain('0')
})

test('start retries reuse a logical key while changed, successful, and explicit attempts rotate it', async ({
  page,
}) => {
  const requests: Array<{ message: string; key: string | undefined }> = []
  const successfulRunIds = [runOne, runTwo, runThree, runFour]

  await page.route('**/api/**', async (route) => {
    const request = route.request()
    const path = new URL(request.url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') {
      return json(route, {
        token: 'run-idempotency-token',
        username: 'admin',
        role: 'ADMIN',
        tenantCode: 'demo',
      })
    }
    if (path === '/api/features') {
      return json(route, { agentBuilderEnabled: true, agentTestRunEnabled: true })
    }
    if (path === '/api/documents' || path === '/api/chat/history') return json(route, [])
    if (path === '/api/agents' && request.method() === 'GET') return json(route, [publishedAgent])
    if (path === `/api/agents/${agentId}` && request.method() === 'GET') {
      return json(route, publishedAgent, { headers: { ETag: '"4"' } })
    }
    if (path === `/api/agents/${agentId}/revisions`) return json(route, [])
    if (path === '/api/skills/catalog' || path === '/api/tools') return json(route, [])
    if (path === '/api/agents/catalog/rule-facts') {
      return json(route, { facts: [], gates: ['pre-action'], operators: [], limits: {} })
    }
    if (path === '/api/agents/catalog/rule-actions') return json(route, { actions: [] })
    if (path === `/api/agents/${agentId}/runs` && request.method() === 'POST') {
      const body = request.postDataJSON() as { message: string }
      requests.push({ message: body.message, key: request.headers()['idempotency-key'] })
      if (requests.length === 1 || requests.length === 5) {
        const status = requests.length === 1 ? 502 : 409
        return json(
          route,
          {
            timestamp: '2026-07-25T00:00:00Z',
            status,
            message:
              status === 502 ? 'downstream outcome unknown' : 'definitive request conflict',
            fieldErrors: {},
          },
          { status },
        )
      }
      const successIndex = requests.length === 6 ? 3 : requests.length - 2
      return json(
        route,
        {
          runId: successfulRunIds[successIndex],
          status: 'completed',
          latestEventSequence: 0,
          pinnedAgentRevision: 3,
          pinnedWorkflowRevision: 2,
        },
        { status: 202 },
      )
    }
    return json(route, [])
  })

  await loginAndOpenAgent(page)
  await page.getByRole('button', { name: '測試 Run' }).click()
  const input = page.getByLabel('測試訊息')
  const startButton = page.getByRole('button', { name: '啟動測試 Run' })

  await input.fill('  retry me  ')
  await startButton.click()
  await expect(page.getByRole('alert')).toContainText('downstream outcome unknown')
  await expect(page.getByRole('button', { name: '以新嘗試重送 Start' })).toBeDisabled()
  await page.reload()
  await loginAndOpenAgent(page)
  await page.getByRole('button', { name: '測試 Run' }).click()
  await input.fill('retry me')
  await startButton.click()
  await expect(page.getByText(runOne, { exact: true })).toBeVisible()
  expect(requests[0].message).toBe('retry me')
  expect(requests[1].key).toBe(requests[0].key)

  await input.fill('changed')
  await startButton.click()
  await expect(page.getByText(runTwo, { exact: true })).toBeVisible()
  expect(requests[2].key).not.toBe(requests[1].key)

  await startButton.click()
  await expect(page.getByText(runThree, { exact: true })).toBeVisible()
  expect(requests[3].key).not.toBe(requests[2].key)

  await input.fill('explicit')
  await startButton.click()
  await expect(page.getByRole('alert')).toContainText('definitive request conflict')
  await expect(page.getByRole('button', { name: '以新嘗試重送 Start' })).toBeEnabled()
  await page.getByRole('button', { name: '以新嘗試重送 Start' }).click()
  await expect(page.getByText(runFour, { exact: true })).toBeVisible()
  expect(requests[5].key).not.toBe(requests[4].key)
  expect(new Set(successfulRunIds).size).toBe(4)
})

test('accepted nonterminal cancel survives reload, stays cancelling, and settles by polling', async ({
  page,
}) => {
  let cancelAccepted = false
  let allowCancelled = false
  let cancelCalls = 0
  let cancelKey: string | undefined

  await page.route('**/api/**', async (route) => {
    const request = route.request()
    const url = new URL(request.url())
    const path = url.pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') {
      return json(route, {
        token: 'cancel-lifecycle-token',
        username: 'admin',
        role: 'ADMIN',
        tenantCode: 'demo',
      })
    }
    if (path === '/api/features') {
      return json(route, { agentBuilderEnabled: true, agentTestRunEnabled: true })
    }
    if (path === '/api/documents' || path === '/api/chat/history') return json(route, [])
    if (path === '/api/agents' && request.method() === 'GET') return json(route, [publishedAgent])
    if (path === `/api/agents/${agentId}` && request.method() === 'GET') {
      return json(route, publishedAgent, { headers: { ETag: '"4"' } })
    }
    if (path === `/api/agents/${agentId}/revisions`) return json(route, [])
    if (path === '/api/skills/catalog' || path === '/api/tools') return json(route, [])
    if (path === '/api/agents/catalog/rule-facts') {
      return json(route, { facts: [], gates: ['pre-action'], operators: [], limits: {} })
    }
    if (path === '/api/agents/catalog/rule-actions') return json(route, { actions: [] })
    if (path === `/api/agents/${agentId}/runs` && request.method() === 'POST') {
      return json(
        route,
        {
          runId: runFour,
          status: 'running',
          latestEventSequence: 0,
          pinnedAgentRevision: 3,
          pinnedWorkflowRevision: 2,
        },
        { status: 202 },
      )
    }
    if (path === `/api/runs/${runFour}/cancel` && request.method() === 'POST') {
      cancelCalls += 1
      cancelKey = request.headers()['idempotency-key']
      cancelAccepted = true
      return json(
        route,
        {
          runId: runFour,
          status: 'running',
          cancel_requested_at: '2026-07-25T00:00:00Z',
          latestEventSequence: 0,
        },
        { status: 202 },
      )
    }
    if (path === `/api/runs/${runFour}` && request.method() === 'GET') {
      return json(route, {
        runId: runFour,
        status: cancelAccepted && allowCancelled ? 'cancelled' : 'running',
        cancel_requested_at:
          cancelAccepted && !allowCancelled ? '2026-07-25T00:00:00Z' : null,
        latestEventSequence: cancelAccepted && allowCancelled ? 1 : 0,
        pinnedAgentRevision: 3,
        pinnedWorkflowRevision: 2,
      })
    }
    if (path === `/api/runs/${runFour}/events`) {
      return json(route, {
        events:
          cancelAccepted && allowCancelled
            ? [{ sequence: 1, eventType: 'run.cancelled', payload: {} }]
            : [],
        latestEventSequence: cancelAccepted && allowCancelled ? 1 : 0,
      })
    }
    return json(route, [])
  })

  await loginAndOpenAgent(page)
  await page.getByRole('button', { name: '測試 Run' }).click()
  await page.getByLabel('測試訊息').fill('cancel accepted')
  await page.getByRole('button', { name: '啟動測試 Run' }).click()
  await expect(page.getByText(runFour, { exact: true })).toBeVisible()
  await page.getByRole('button', { name: '取消 Run', exact: true }).click()
  await page
    .getByRole('dialog', { name: '確認操作' })
    .getByRole('button', { name: '取消 Run' })
    .click()
  await expect(page.getByText('取消中', { exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: '啟動測試 Run' })).toBeDisabled()
  await expect(page.getByRole('button', { name: '取消 Run', exact: true })).toBeDisabled()

  const storedCancelKey = await page.evaluate(() => {
    const entry = Object.entries(sessionStorage).find(([key]) => key.includes(':cancel:'))
    return entry?.[1] ?? null
  })
  expect(storedCancelKey).toContain(cancelKey)

  await page.reload()
  await loginAndOpenAgent(page)
  await page.getByRole('button', { name: '測試 Run' }).click()
  await expect(page.getByText(runFour, { exact: true })).toBeVisible()
  await expect(page.getByText('取消中', { exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: '啟動測試 Run' })).toBeDisabled()
  await expect(page.getByRole('button', { name: '取消 Run', exact: true })).toBeDisabled()
  expect(
    await page.evaluate(() => {
      const entry = Object.entries(sessionStorage).find(([key]) => key.includes(':cancel:'))
      return entry?.[1] ?? null
    }),
  ).toBe(storedCancelKey)

  allowCancelled = true
  await expect(page.getByText('已取消', { exact: true })).toBeVisible({ timeout: 5000 })
  expect(cancelCalls).toBe(1)
  expect(
    await page.evaluate(() =>
      Object.keys(sessionStorage).filter(
        (key) => key.startsWith('springai-agent-runs:idempotency:') && key.includes('cancel'),
      ),
    ),
  ).toEqual([])
})

test('Agent test tab stays fail-closed when the run feature is disabled', async ({ page }) => {
  let runApiRequested = false
  await page.route('**/api/**', async (route) => {
    const request = route.request()
    const path = new URL(request.url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') {
      return json(route, {
        token: 'run-disabled-token',
        username: 'admin',
        role: 'ADMIN',
        tenantCode: 'demo',
      })
    }
    if (path === '/api/features') {
      return json(route, { agentBuilderEnabled: true, agentTestRunEnabled: false })
    }
    if (path === '/api/documents' || path === '/api/chat/history') return json(route, [])
    if (path === '/api/agents' && request.method() === 'GET') return json(route, [publishedAgent])
    if (path === `/api/agents/${agentId}`) {
      return json(route, publishedAgent, { headers: { ETag: '"4"' } })
    }
    if (path === `/api/agents/${agentId}/revisions`) return json(route, [])
    if (path.startsWith('/api/runs') || path.endsWith('/runs')) runApiRequested = true
    return json(route, [])
  })

  await loginAndOpenAgent(page)
  await expect(page.getByRole('button', { name: '測試 Run' })).toHaveCount(0)
  expect(runApiRequested).toBe(false)
})

test('a delayed old-run poll cannot overwrite a newly started run or its event cursor', async ({
  page,
}) => {
  let starts = 0
  let oldPollRequests = 0
  let releaseOldPoll!: () => void
  const oldPollGate = new Promise<void>((resolve) => {
    releaseOldPoll = resolve
  })
  const newRunCursors: string[] = []

  await page.route('**/api/**', async (route) => {
    const request = route.request()
    const url = new URL(request.url())
    const path = url.pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') {
      return json(route, {
        token: 'run-race-token',
        username: 'admin',
        role: 'ADMIN',
        tenantCode: 'demo',
      })
    }
    if (path === '/api/features') {
      return json(route, { agentBuilderEnabled: true, agentTestRunEnabled: true })
    }
    if (path === '/api/documents' || path === '/api/chat/history') return json(route, [])
    if (path === '/api/agents' && request.method() === 'GET') return json(route, [publishedAgent])
    if (path === `/api/agents/${agentId}` && request.method() === 'GET') {
      return json(route, publishedAgent, { headers: { ETag: '"4"' } })
    }
    if (path === `/api/agents/${agentId}/revisions`) return json(route, [])
    if (path === '/api/skills/catalog' || path === '/api/tools') return json(route, [])
    if (path === '/api/agents/catalog/rule-facts') {
      return json(route, { facts: [], gates: ['pre-action'], operators: [], limits: {} })
    }
    if (path === '/api/agents/catalog/rule-actions') return json(route, { actions: [] })

    if (path === `/api/agents/${agentId}/runs` && request.method() === 'POST') {
      starts += 1
      return json(
        route,
        {
          runId: starts === 1 ? runOne : runTwo,
          status: 'running',
          stateVersion: 1,
          checkpointVersion: 1,
          latestEventSequence: 0,
          pinnedAgentRevision: 3,
          pinnedWorkflowRevision: 2,
        },
        { status: 202 },
      )
    }
    if (path === `/api/runs/${runOne}` && request.method() === 'GET') {
      oldPollRequests += 1
      await oldPollGate
      return json(route, {
        runId: runOne,
        status: 'failed',
        latestEventSequence: 99,
        error: 'stale old-run failure',
      })
    }
    if (path === `/api/runs/${runOne}/events`) {
      oldPollRequests += 1
      await oldPollGate
      return json(route, {
        events: [
          {
            sequence: 99,
            eventType: 'stale.old-run',
            payload: { marker: 'must-not-appear' },
          },
        ],
        latestEventSequence: 99,
      })
    }
    if (path === `/api/runs/${runTwo}` && request.method() === 'GET') {
      return json(route, {
        runId: runTwo,
        status: 'running',
        stateVersion: 2,
        checkpointVersion: 1,
        latestEventSequence: 1,
        pinnedAgentRevision: 3,
        pinnedWorkflowRevision: 2,
      })
    }
    if (path === `/api/runs/${runTwo}/events`) {
      const cursor = url.searchParams.get('afterSequence') ?? ''
      newRunCursors.push(cursor)
      return json(route, {
        events:
          cursor === '0'
            ? [{ sequence: 1, eventType: 'new.run', payload: { marker: 'current-run' } }]
            : [],
        latestEventSequence: 1,
      })
    }
    return json(route, [])
  })

  await loginAndOpenAgent(page)
  await page.getByRole('button', { name: '測試 Run' }).click()
  await page.getByLabel('測試訊息').fill('old run')
  await page.getByRole('button', { name: '啟動測試 Run' }).click()
  await expect.poll(() => oldPollRequests).toBe(2)

  await page.getByLabel('測試訊息').fill('new run')
  await page.getByRole('button', { name: '啟動測試 Run' }).click()
  await expect(page.getByText(runTwo, { exact: true })).toBeVisible()
  await expect(page.getByText('current-run')).toBeVisible()

  releaseOldPoll()
  await page.getByRole('button', { name: '立即重新整理' }).click()
  await expect.poll(() => newRunCursors.includes('1')).toBe(true)
  await expect(page.getByText(runTwo, { exact: true })).toBeVisible()
  await expect(page.getByText(runOne, { exact: true })).toHaveCount(0)
  await expect(page.getByText('stale old-run failure')).toHaveCount(0)
  await expect(page.getByText('must-not-appear')).toHaveCount(0)
  expect(newRunCursors).not.toContain('99')
})

test('Agent test tab stays hidden for an unpublished Agent even when the feature is enabled', async ({
  page,
}) => {
  const unpublishedAgent = { ...publishedAgent, published_revision: null }
  let runApiRequested = false
  await page.route('**/api/**', async (route) => {
    const request = route.request()
    const path = new URL(request.url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') {
      return json(route, {
        token: 'run-unpublished-token',
        username: 'admin',
        role: 'ADMIN',
        tenantCode: 'demo',
      })
    }
    if (path === '/api/features') {
      return json(route, { agentBuilderEnabled: true, agentTestRunEnabled: true })
    }
    if (path === '/api/documents' || path === '/api/chat/history') return json(route, [])
    if (path === '/api/agents' && request.method() === 'GET') return json(route, [unpublishedAgent])
    if (path === `/api/agents/${agentId}`) {
      return json(route, unpublishedAgent, { headers: { ETag: '"4"' } })
    }
    if (path === `/api/agents/${agentId}/revisions`) return json(route, [])
    if (path.startsWith('/api/runs') || path.endsWith('/runs')) runApiRequested = true
    return json(route, [])
  })

  await loginAndOpenAgent(page)
  await expect(page.getByRole('button', { name: '測試 Run' })).toHaveCount(0)
  expect(runApiRequested).toBe(false)
})
