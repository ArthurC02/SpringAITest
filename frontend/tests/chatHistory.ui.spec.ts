import { expect, test, type Page, type Route } from '@playwright/test'

const API_ROUTE = /^http:\/\/127\.0\.0\.1:4174\/api\//

async function json(route: Route, body: unknown, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
}

async function login(page: Page, username = 'user') {
  await page.goto('/')
  await page.getByTestId('auth-username').fill(username)
  await page.getByTestId('auth-password').fill('password123')
  await page.getByTestId('auth-submit').click()
  await expect(page.getByTestId('session-identity')).toBeVisible()
}

function historyItem(id: number) {
  return { id, reply: `history-${id}`, createdAt: '2026-08-05T10:00:00Z' }
}

async function commonRoute(route: Route): Promise<boolean> {
  const path = new URL(route.request().url()).pathname
  if (path === '/api/auth/login') {
    const request = route.request().postDataJSON() as { username: string }
    await json(route, {
      token: `token-${request.username}`,
      username: request.username,
      role: 'USER',
      tenantCode: request.username === 'second' ? 'demo-b' : 'demo-a',
      capabilities: [],
    })
    return true
  }
  if (path === '/api/features') {
    await json(route, {})
    return true
  }
  if (path === '/api/documents') {
    await json(route, [])
    return true
  }
  return false
}

test('authenticated first load is bounded to 50 out of 1000 and exposes older-page UI', async ({ page }) => {
  let historyCalls = 0
  await page.route(API_ROUTE, async (route) => {
    if (await commonRoute(route)) return
    const url = new URL(route.request().url())
    if (url.pathname === '/api/chat/history/page') {
      historyCalls += 1
      expect(url.searchParams.get('limit')).toBe('50')
      expect(url.searchParams.has('before')).toBe(false)
      const thousand = Array.from({ length: 1000 }, (_, index) => historyItem(1000 - index))
      return json(route, { items: thousand.slice(0, 50), nextCursor: 'cursor-50', hasMore: true })
    }
    return json(route, [])
  })

  await page.goto('/')
  expect(historyCalls).toBe(0)
  await page.getByTestId('auth-username').fill('user')
  await page.getByTestId('auth-password').fill('password123')
  await page.getByTestId('auth-submit').click()

  await expect(page.locator('.bubble--assistant')).toHaveCount(50)
  await expect(page.getByRole('button', { name: '載入更早' })).toBeVisible()
  // React StrictMode replays the initial effect in development; both bounded
  // requests must still ask for only the first 50 records.
  expect(historyCalls).toBe(2)
})

test('initial page follows older cached server rows and keeps the local suffix', async ({ page }) => {
  const cached = [
    ...Array.from({ length: 98 }, (_, index) => ({
      id: `server:${903 + index}`,
      role: 'assistant',
      content: `cached-${903 + index}`,
    })),
    { id: 'local-user', role: 'user', content: 'local-question' },
    { id: 'local-stream', role: 'assistant', content: 'local-partial' },
  ]
  await page.addInitScript((messages) => {
    localStorage.setItem('springai-chat:messages', JSON.stringify(messages))
  }, cached)
  await page.route(API_ROUTE, async (route) => {
    if (await commonRoute(route)) return
    if (new URL(route.request().url()).pathname === '/api/chat/history/page') {
      const newest = Array.from({ length: 50 }, (_, index) => historyItem(1050 - index))
      return json(route, { items: newest, nextCursor: 'older', hasMore: true })
    }
    return json(route, [])
  })

  await login(page)

  await expect(page.locator('.bubble__content')).toHaveCount(150)
  await expect(page.locator('.bubble__content').nth(0)).toHaveText('cached-903')
  await expect(page.locator('.bubble__content').nth(97)).toHaveText('cached-1000')
  await expect(page.locator('.bubble__content').nth(98)).toHaveText('history-1001')
  await expect(page.locator('.bubble__content').nth(147)).toHaveText('history-1050')
  await expect(page.locator('.bubble__content').nth(148)).toHaveText('local-question')
  await expect(page.locator('.bubble__content').nth(149)).toHaveText('local-partial')
})

test('complete initial page removes stale cached server rows but keeps local state', async ({ page }) => {
  await page.addInitScript(() => {
    localStorage.setItem('springai-chat:messages', JSON.stringify([
      { id: 'server:1', role: 'assistant', content: 'stale-older' },
      { id: 'server:2', role: 'assistant', content: 'stale-overlap' },
      { id: 'server:99', role: 'assistant', content: 'stale-newer' },
      { id: 'local-stream', role: 'assistant', content: 'local-partial' },
    ]))
  })
  await page.route(API_ROUTE, async (route) => {
    if (await commonRoute(route)) return
    if (new URL(route.request().url()).pathname === '/api/chat/history/page') {
      return json(route, {
        items: [historyItem(3), historyItem(2)],
        nextCursor: null,
        hasMore: false,
      })
    }
    return json(route, [])
  })

  await login(page)

  await expect(page.locator('.bubble__content')).toHaveText([
    'history-2', 'history-3', 'local-partial',
  ])
})

test('empty complete initial page clears only server-derived cache', async ({ page }) => {
  await page.addInitScript(() => {
    localStorage.setItem('springai-chat:messages', JSON.stringify([
      { id: 'server:1', role: 'assistant', content: 'stale' },
      { id: 'local-user', role: 'user', content: 'local-question' },
      { id: 'local-stream', role: 'assistant', content: 'local-partial' },
    ]))
  })
  await page.route(API_ROUTE, async (route) => {
    if (await commonRoute(route)) return
    if (new URL(route.request().url()).pathname === '/api/chat/history/page') {
      return json(route, { items: [], nextCursor: null, hasMore: false })
    }
    return json(route, [])
  })

  await login(page)

  await expect(page.locator('.bubble__content')).toHaveText([
    'local-question', 'local-partial',
  ])
})

test('rapid older clicks issue one request and late merge preserves a newly completed stream', async ({ page }) => {
  let olderRoute: Route | null = null
  let olderCalls = 0
  await page.route(API_ROUTE, async (route) => {
    if (await commonRoute(route)) return
    const url = new URL(route.request().url())
    if (url.pathname === '/api/chat/history/page' && !url.searchParams.has('before')) {
      return json(route, { items: [historyItem(4), historyItem(3)], nextCursor: 'older', hasMore: true })
    }
    if (url.pathname === '/api/chat/history/page') {
      olderCalls += 1
      olderRoute = route
      return
    }
    if (url.pathname === '/api/chat/stream') {
      return route.fulfill({ status: 200, contentType: 'text/event-stream', body: 'data:new-answer\n\n' })
    }
    return json(route, [])
  })

  await login(page)
  const olderButton = page.getByRole('button', { name: '載入更早' })
  await olderButton.evaluate((button: HTMLButtonElement) => {
    button.click()
    button.click()
  })
  await expect.poll(() => olderCalls).toBe(1)

  const composer = page.locator('textarea.composer__input')
  await composer.fill('new-question')
  await composer.press('Enter')
  await expect(page.locator('.bubble--assistant .bubble__content').last()).toHaveText('new-answer')

  await json(olderRoute!, { items: [historyItem(2), historyItem(1)], nextCursor: null, hasMore: false })
  await expect(page.locator('.bubble__content')).toHaveText([
    'history-1', 'history-2', 'history-3', 'history-4', 'new-question', 'new-answer',
  ])
  expect(olderCalls).toBe(1)
})

test('500 is retryable and malformed success responses are not merged', async ({ page }) => {
  let responseKind: 'server-error' | 'malformed' | 'ok' = 'server-error'
  await page.route(API_ROUTE, async (route) => {
    if (await commonRoute(route)) return
    const path = new URL(route.request().url()).pathname
    if (path === '/api/chat/history/page') {
      if (responseKind === 'server-error') {
        return json(route, { message: '聊天記錄暫時無法使用', fieldErrors: {} }, 500)
      }
      if (responseKind === 'malformed') return json(route, [])
      return json(route, { items: [], nextCursor: null, hasMore: false })
    }
    return json(route, [])
  })

  await login(page)
  await expect(page.getByRole('alert')).toHaveText('聊天記錄暫時無法使用')
  responseKind = 'malformed'
  await page.getByRole('button', { name: '重試載入聊天記錄' }).click()
  await expect(page.getByRole('alert')).toHaveText('聊天記錄回應格式不正確')
  responseKind = 'ok'
  await page.getByRole('button', { name: '重試載入聊天記錄' }).click()
  await expect(page.getByRole('alert')).toHaveCount(0)
})

test('401 uses global logout and an aborted old-account page cannot contaminate the next account', async ({ page }) => {
  let firstHistory = true
  let releaseOld!: () => void
  const oldHeld = new Promise<void>((resolve) => { releaseOld = resolve })
  await page.route(API_ROUTE, async (route) => {
    if (await commonRoute(route)) return
    const path = new URL(route.request().url()).pathname
    if (path === '/api/chat/history/page') {
      if (firstHistory) {
        firstHistory = false
        await oldHeld
        return json(route, { items: [historyItem(999)], nextCursor: null, hasMore: false })
          .catch(() => undefined)
      }
      return json(route, { items: [], nextCursor: null, hasMore: false })
    }
    return json(route, [])
  })

  await login(page, 'first')
  await page.getByTestId('logout-button').click()
  await expect(page.getByTestId('auth-submit')).toBeVisible()
  releaseOld()
  await login(page, 'second')
  await expect(page.locator('.bubble__content', { hasText: 'history-999' })).toHaveCount(0)

  await page.unroute(API_ROUTE)
  await page.route(API_ROUTE, async (route) => {
    if (await commonRoute(route)) return
    if (new URL(route.request().url()).pathname === '/api/chat/history/page') {
      return json(route, { message: '未認證或憑證無效', fieldErrors: {} }, 401)
    }
    return json(route, [])
  })
  await page.reload()
  await expect(page.getByTestId('auth-submit')).toBeVisible()
})
