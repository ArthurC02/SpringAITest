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

test('several data: lines inside one frame rejoin with a newline', async ({ page }) => {
  // Distinct from the multi-frame case above: one frame carrying one multi-line token, which
  // platform writes as consecutive `data:` lines. Markdown makes the join observable — the
  // rejoined value is a two-item list, a dropped newline would collapse it into one item.
  await page.route('**/api/**', async (route) => {
    const path = new URL(route.request().url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') {
      return json(route, { token: 'token', username: 'user', role: 'USER', tenantCode: 'demo', capabilities: [] })
    }
    if (path === '/api/features') return json(route, {})
    if (path === '/api/chat/stream') return sse(route, 'data:- alpha\ndata:- beta\n\n')
    return json(route, [])
  })

  await login(page)
  await send(page, 'list please')

  await expect(page.locator('.bubble--assistant .bubble__content li')).toHaveText(['alpha', 'beta'])
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

test('an X-Auth-Invalid stream response logs out even though the status is 200', async ({ page }) => {
  await page.route('**/api/**', async (route) => {
    const path = new URL(route.request().url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') {
      return json(route, { token: 'token', username: 'user', role: 'USER', tenantCode: 'demo', capabilities: [] })
    }
    if (path === '/api/features') return json(route, {})
    if (path === '/api/chat/stream') {
      // This endpoint is AllowAnonymous, so an expired JWT never arrives as a 401 the way the
      // apiFetch paths get one — platform flags it with this response header on a normal 200 SSE.
      return route.fulfill({
        status: 200,
        headers: { 'content-type': 'text/event-stream', 'X-Auth-Invalid': '1' },
        body: 'data:ok\n\n',
      })
    }
    return json(route, [])
  })

  await login(page)
  await send(page, 'confidential plan')

  // Same global logout as a 401: session and chat keys gone, expiry notice on the login page.
  await expect(page.getByTestId('auth-page')).toBeVisible()
  await expect(page.getByText('session 已過期，請重新登入。')).toBeVisible()
  expect(await readKey(page, SESSION_KEY)).toBeNull()
  expect(await readKey(page, MESSAGES_KEY)).toBeNull()
})

test('a stream request that fails outright shows the ApiError message, not raw JSON', async ({ page }) => {
  await page.route('**/api/**', async (route) => {
    const path = new URL(route.request().url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') {
      return json(route, { token: 'token', username: 'user', role: 'USER', tenantCode: 'demo', capabilities: [] })
    }
    if (path === '/api/features') return json(route, {})
    if (path === '/api/chat/stream') {
      // Failing before a single SSE byte: the body is ApiError JSON, not text/event-stream.
      return json(
        route,
        { timestamp: '2026-07-25T00:00:00Z', status: 503, message: '模型服務暫時不可用', fieldErrors: {} },
        503,
      )
    }
    return json(route, [])
  })

  await login(page)
  await send(page, 'hello')

  await expect(page.locator('.bubble--error .bubble__content')).toHaveText('模型服務暫時不可用')
  // No token ever arrived, so the placeholder itself becomes the error bubble.
  await expect(page.locator('.bubble--assistant:not(.bubble--error)')).toHaveCount(0)
})

test('stopping a stream before any token removes the placeholder without an error bubble', async ({ page }) => {
  let releaseStream!: () => void
  const heldStream = new Promise<void>((resolve) => { releaseStream = resolve })

  await page.route('**/api/**', async (route) => {
    const path = new URL(route.request().url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') {
      return json(route, { token: 'token', username: 'user', role: 'USER', tenantCode: 'demo', capabilities: [] })
    }
    if (path === '/api/features') return json(route, {})
    if (path === '/api/chat/stream') {
      // Keep the request in flight so the composer stays in its streaming state until we stop it.
      await heldStream
      return sse(route, 'data:too late\n\n').catch(() => {})
    }
    return json(route, [])
  })

  await login(page)
  await send(page, 'stop me')
  const stopButton = page.locator('button.composer__stop')
  await expect(stopButton).toBeVisible()
  await expect(page.locator('.bubble--assistant')).toHaveCount(1)

  // dispatchEvent instead of click: CopilotKit's floating sidebar button sits over this corner.
  await stopButton.dispatchEvent('click')

  // A user-initiated abort is not a failure: the empty placeholder is dropped rather than turned
  // into an error bubble, and the composer goes back to its send state.
  await expect(page.locator('.bubble--assistant')).toHaveCount(0)
  await expect(page.locator('.bubble--user .bubble__content')).toHaveText('stop me')
  await expect(stopButton).toHaveCount(0)
  releaseStream()
})

test('a stream sent without a stored session omits Bearer and still renders a mid-stream error', async ({ page }) => {
  let authHeader: string | undefined
  const stream = `data:Hi\n\nevent:error\ndata:${STREAM_ERROR}\n\n`
  await page.route('**/api/**', async (route) => {
    const path = new URL(route.request().url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') {
      return json(route, { token: 'token', username: 'user', role: 'USER', tenantCode: 'demo', capabilities: [] })
    }
    if (path === '/api/features') return json(route, {})
    if (path === '/api/chat/stream') {
      authHeader = route.request().headers()['authorization']
      return sse(route, stream)
    }
    return json(route, [])
  })

  await login(page)
  // Logging out in another tab clears the shared key while this tab still renders the chat.
  // streamChat re-reads localStorage per request, so this turn takes the anonymous branch.
  await page.evaluate((key) => localStorage.removeItem(key), SESSION_KEY)
  await send(page, 'anonymous turn')

  await expect(page.locator('.bubble--assistant:not(.bubble--error) .bubble__content')).toHaveText('Hi')
  await expect(page.locator('.bubble--error .bubble__content')).toHaveText(STREAM_ERROR)
  expect(authHeader).toBeUndefined()
  // Anonymous chat is allowed, so a failed stream must not be mistaken for an expired session.
  await expect(page.getByTestId('auth-page')).toHaveCount(0)
})
