import { expect, test, type Page, type Route } from '@playwright/test'

// Mocked-browser coverage for the oldest and most breakable chat paths: SSE frame parsing,
// the mid-stream `event:error` terminal frame, localStorage debounce/flush, the logout
// persistence generation, and the global 401 logout. No real backend is involved.

const SESSION_KEY = 'springai:session'
const MESSAGES_KEY = 'springai-chat:messages'
const CONVERSATION_KEY = 'springai-chat:conversationId'
const USER_ID_KEY = 'springai-chat:userId'

// Platform's fixed terminal-frame message (ChatController.StreamErrorMessage), sent verbatim.
const STREAM_ERROR = '回覆過程發生錯誤，請稍後再試'

async function json(route: Route, body: unknown, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
}

async function sse(route: Route, body: string) {
  await route.fulfill({ status: 200, contentType: 'text/event-stream', body })
}

async function login(page: Page) {
  await page.goto('/')
  await page.getByTestId('auth-username').fill('user')
  await page.getByTestId('auth-password').fill('password123')
  await page.getByTestId('auth-submit').click()
  await expect(page.getByTestId('session-identity')).toBeVisible()
}

async function send(page: Page, text: string) {
  const composer = page.locator('textarea.composer__input')
  await composer.fill(text)
  await composer.press('Enter')
}

function readKey(page: Page, key: string): Promise<string | null> {
  return page.evaluate((name) => localStorage.getItem(name), key)
}

test('mid-stream event:error keeps rendered tokens and adds an error bubble', async ({ page }) => {
  // Two token frames then the terminal error frame, exactly as platform writes them
  // (`data:` with no space after the colon).
  let stream = `data:Hel\n\ndata:lo\n\nevent:error\ndata:${STREAM_ERROR}\n\n`
  await page.route('**/api/**', async (route) => {
    const path = new URL(route.request().url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') {
      return json(route, { token: 'token', username: 'user', role: 'USER', tenantCode: 'demo', capabilities: [] })
    }
    if (path === '/api/features') return json(route, {})
    if (path === '/api/chat/stream') return sse(route, stream)
    return json(route, [])
  })

  await login(page)
  await send(page, 'hello')

  const answered = page.locator('.bubble--assistant:not(.bubble--error) .bubble__content')
  const failed = page.locator('.bubble--error .bubble__content')
  // Multiple data frames concatenate into one bubble and the partial answer survives the failure.
  await expect(answered).toHaveText('Hello')
  await expect(failed).toHaveText(STREAM_ERROR)

  // Failing before any token arrived must convert the placeholder itself instead of leaving an
  // empty typing bubble behind.
  stream = `event:error\ndata:${STREAM_ERROR}\n\n`
  await send(page, 'again')
  await expect(failed).toHaveCount(2)
  await expect(answered).toHaveCount(1)
  await expect(answered).toHaveText('Hello')
})

test('chat state is debounced and flushed on unmount and page unload', async ({ page }) => {
  await page.route('**/api/**', async (route) => {
    const path = new URL(route.request().url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') {
      return json(route, { token: 'token', username: 'user', role: 'USER', tenantCode: 'demo', capabilities: [] })
    }
    if (path === '/api/features') return json(route, {})
    if (path === '/api/chat/stream') return sse(route, 'data:ok\n\n')
    return json(route, [])
  })

  await login(page)
  await send(page, 'first')
  await expect(page.locator('.bubble--assistant .bubble__content')).toHaveText('ok')

  // Timestamp the next write to the chat key relative to the moment before the message changes.
  // Dropping the debounce would persist on the very first token instead of ~500ms after the last.
  await page.evaluate((key) => {
    const probe = window as unknown as { __chatWrite?: number; __chatStart?: number }
    const original = Storage.prototype.setItem
    Storage.prototype.setItem = function patched(name: string, value: string) {
      if (name === key && !probe.__chatWrite) probe.__chatWrite = performance.now()
      return original.call(this, name, value)
    }
    localStorage.removeItem(key)
    probe.__chatStart = performance.now()
  }, MESSAGES_KEY)

  await send(page, 'second')
  await expect
    .poll(() => page.evaluate(() => (window as unknown as { __chatWrite?: number }).__chatWrite ?? 0))
    .toBeGreaterThan(0)
  const delayMs = await page.evaluate(() => {
    const probe = window as unknown as { __chatWrite: number; __chatStart: number }
    return probe.__chatWrite - probe.__chatStart
  })
  expect(delayMs).toBeGreaterThanOrEqual(450)
  expect(await readKey(page, MESSAGES_KEY)).toContain('second')

  // beforeunload flush: clearing the key after the debounce already fired means only the unload
  // handler can put the conversation back, so a lost listener loses the tab-close conversation.
  await page.evaluate((key) => localStorage.removeItem(key), MESSAGES_KEY)
  await page.reload()
  await expect(page.locator('.bubble--user .bubble__content').last()).toHaveText('second')

  // Unmount flush: wait for the mount debounce to land first, so the only write that can follow
  // the clear is the cleanup flush triggered by leaving the chat view.
  await expect.poll(() => readKey(page, MESSAGES_KEY)).not.toBeNull()
  await page.evaluate((key) => localStorage.removeItem(key), MESSAGES_KEY)
  await page.getByTestId('nav-documents').click()
  await expect.poll(() => readKey(page, MESSAGES_KEY)).toContain('second')
})

test('logout invalidates the persistence generation so a delayed flush cannot restore messages', async ({ page }) => {
  await page.route('**/api/**', async (route) => {
    const path = new URL(route.request().url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') {
      return json(route, { token: 'token', username: 'user', role: 'USER', tenantCode: 'demo', capabilities: [] })
    }
    if (path === '/api/features') return json(route, {})
    if (path === '/api/chat/stream') return sse(route, 'data:ok\n\n')
    return json(route, [])
  })

  await login(page)
  await send(page, 'confidential plan')
  await expect(page.locator('.bubble--assistant .bubble__content')).toHaveText('ok')
  await expect.poll(() => readKey(page, MESSAGES_KEY)).toContain('confidential plan')

  // Logging out unmounts the chat view, and that cleanup calls the same flush as beforeunload.
  // Without the generation guard it would rewrite the previous user's conversation right after
  // logout cleared it — a cross-user privacy boundary on a shared browser.
  // dispatchEvent instead of click: CopilotKit's dev inspector overlay covers the top bar.
  await page.getByTestId('logout-button').dispatchEvent('click')
  await expect(page.getByTestId('auth-page')).toBeVisible()
  await page.waitForTimeout(800) // outlive any debounce timer still pending at logout
  expect(await readKey(page, MESSAGES_KEY)).toBeNull()
  expect(await readKey(page, CONVERSATION_KEY)).toBeNull()
  expect(await readKey(page, SESSION_KEY)).toBeNull()
})

test('any 401 while logged in clears the session and chat keys and shows the expiry notice', async ({ page }) => {
  await page.route('**/api/**', async (route) => {
    const path = new URL(route.request().url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') {
      return json(route, { token: 'token', username: 'user', role: 'USER', tenantCode: 'demo', capabilities: [] })
    }
    if (path === '/api/features') return json(route, {})
    if (path === '/api/chat/stream') return sse(route, 'data:ok\n\n')
    if (path === '/api/analysis/summary') {
      return json(
        route,
        { timestamp: '2026-07-25T00:00:00Z', status: 401, message: 'token 已過期', fieldErrors: {} },
        401,
      )
    }
    return json(route, [])
  })

  await login(page)
  // A leftover anonymous userId must be cleared too, not just the keys this session wrote.
  await page.evaluate((key) => localStorage.setItem(key, 'anonymous-browser-id'), USER_ID_KEY)
  await send(page, 'confidential plan')
  await expect.poll(() => readKey(page, MESSAGES_KEY)).toContain('confidential plan')

  // Any authenticated call, not just chat, routes a 401 through the one global logout.
  await page.getByTestId('nav-analysis').click()
  await expect(page.getByTestId('auth-page')).toBeVisible()
  await expect(page.getByText('session 已過期，請重新登入。')).toBeVisible()
  expect(await readKey(page, SESSION_KEY)).toBeNull()
  expect(await readKey(page, MESSAGES_KEY)).toBeNull()
  expect(await readKey(page, CONVERSATION_KEY)).toBeNull()
  expect(await readKey(page, USER_ID_KEY)).toBeNull()
})
