import { expect, test, type Route } from '@playwright/test'

async function json(route: Route, body: unknown, status = 200) {
  await route.fulfill({
    status,
    contentType: 'application/json',
    body: JSON.stringify(body),
  })
}

async function login(page: import('@playwright/test').Page) {
  await page.goto('/')
  await page.getByTestId('auth-username').fill('user')
  await page.getByTestId('auth-password').fill('password123')
  await page.getByTestId('auth-submit').click()
}

test('canary tenant selects a redacted published Orchestrator and sends its id on chat SSE', async ({ page }) => {
  const selected = '11111111-1111-4111-8111-111111111111'
  let chatBody: Record<string, unknown> | null = null
  await page.route('**/api/**', async (route) => {
    const request = route.request()
    const path = new URL(request.url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') {
      return json(route, {
        token: 'token',
        username: 'user',
        role: 'USER',
        tenantCode: 'canary',
      })
    }
    if (path === '/api/features') return json(route, { agentChatEnabled: true })
    if (path === '/api/chat/orchestrators') {
      return json(route, {
        orchestrators: [{
          id: selected,
          name: 'Research Team',
          description: 'Published collaboration runtime',
          revision: 3,
          capabilities: ['research'],
        }],
      })
    }
    if (path === '/api/chat/history' || path === '/api/documents') return json(route, [])
    if (path === '/api/chat/stream') {
      chatBody = request.postDataJSON() as Record<string, unknown>
      return route.fulfill({
        status: 200,
        contentType: 'text/event-stream',
        body: 'data:done\n\n',
      })
    }
    return json(route, [])
  })

  await login(page)
  const selector = page.getByLabel('選擇協作 Orchestrator')
  await expect(selector).toBeVisible()
  await selector.selectOption(selected)
  const composer = page.locator('textarea.composer__input')
  await composer.fill('hello')
  await composer.press('Enter')

  await expect.poll(() => chatBody?.orchestratorId).toBe(selected)
  await expect(page.getByText('done')).toBeVisible()
})

test('reverting to the default option drops orchestratorId from the next chat SSE body', async ({ page }) => {
  const selected = '22222222-2222-4222-8222-222222222222'
  const chatBodies: Record<string, unknown>[] = []
  await page.route('**/api/**', async (route) => {
    const request = route.request()
    const path = new URL(request.url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') {
      return json(route, {
        token: 'token',
        username: 'user',
        role: 'USER',
        tenantCode: 'canary',
      })
    }
    if (path === '/api/features') return json(route, { agentChatEnabled: true })
    if (path === '/api/chat/orchestrators') {
      return json(route, {
        orchestrators: [{
          id: selected,
          name: 'Research Team',
          description: 'Published collaboration runtime',
          revision: 3,
          capabilities: ['research'],
        }],
      })
    }
    if (path === '/api/chat/history' || path === '/api/documents') return json(route, [])
    if (path === '/api/chat/stream') {
      chatBodies.push(request.postDataJSON() as Record<string, unknown>)
      return route.fulfill({
        status: 200,
        contentType: 'text/event-stream',
        body: 'data:done\n\n',
      })
    }
    return json(route, [])
  })

  await login(page)
  const selector = page.getByLabel('選擇協作 Orchestrator')
  await expect(selector).toBeVisible()
  await selector.selectOption(selected)
  const composer = page.locator('textarea.composer__input')
  await composer.fill('with orchestrator')
  await composer.press('Enter')
  await expect.poll(() => chatBodies[0]?.orchestratorId).toBe(selected)

  // Back to 預設協作模式 (value ""): selection must reset to null so the optional key disappears
  // from the body again, not linger as the previous id or arrive as an empty/null value.
  await selector.selectOption({ label: '預設協作模式' })
  await expect(selector).toHaveValue('')
  await composer.fill('back to default')
  await composer.press('Enter')
  await expect.poll(() => chatBodies.length).toBe(2)
  expect(chatBodies[1]).not.toHaveProperty('orchestratorId')
})

test('non-canary catalog 404 keeps the chat UI fully legacy', async ({ page }) => {
  let catalogRequested = false
  await page.route('**/api/**', async (route) => {
    const path = new URL(route.request().url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') {
      return json(route, {
        token: 'token',
        username: 'user',
        role: 'USER',
        tenantCode: 'control',
      })
    }
    if (path === '/api/features') return json(route, { agentChatEnabled: true })
    if (path === '/api/chat/orchestrators') {
      catalogRequested = true
      return json(route, { message: 'not found' }, 404)
    }
    if (path === '/api/chat/history' || path === '/api/documents') return json(route, [])
    return json(route, [])
  })

  await login(page)
  await expect.poll(() => catalogRequested).toBe(true)
  await expect(page.getByLabel('選擇協作 Orchestrator')).toHaveCount(0)
})
