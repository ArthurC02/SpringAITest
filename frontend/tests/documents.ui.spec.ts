import { expect, test, type Route } from '@playwright/test'

// The 202 flow is eventually consistent: the document is absent from GET /api/documents until
// the RabbitMQ consumer has stored it. `useDocuments` bridges that with an optimistic row plus
// a pendingRef merge, and the regression this guards is the optimistic row being wiped by the
// very next full-list replacement (the user watches their upload vanish, then reappear).

const DOC_ID = '11111111-1111-4111-8111-111111111111'

async function json(route: Route, body: unknown, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
}

async function openDocuments(page: import('@playwright/test').Page) {
  await page.goto('/')
  await page.getByTestId('auth-username').fill('user')
  await page.getByTestId('auth-password').fill('password123')
  await page.getByTestId('auth-submit').click()
  await page.getByTestId('nav-documents').click()
}

async function fillDocument(page: import('@playwright/test').Page, title = '季報', text = '第三季營收摘要') {
  await page.getByLabel('標題').fill(title)
  await page.getByRole('button', { name: '貼上文字' }).click()
  await page.getByLabel('內容', { exact: true }).fill(text)
}

test('202 create optimistically inserts a processing row that a full list replacement cannot wipe', async ({ page }) => {
  let serverDocs: unknown[] = []
  let listCalls = 0
  let slowLists = false
  let activeLists = 0
  let maxActiveLists = 0

  await page.route('**/api/**', async (route) => {
    const request = route.request()
    const path = new URL(request.url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') {
      return json(route, { token: 'token', username: 'user', role: 'USER', tenantCode: 'demo', capabilities: [] })
    }
    if (path === '/api/features') return json(route, {})
    if (path === '/api/documents' && request.method() === 'POST') {
      // Platform answers 202 with only these three fields; every other column is a client default.
      return json(route, { id: DOC_ID, title: '季報', status: 'processing' }, 202)
    }
    if (path === '/api/documents' && request.method() === 'GET') {
      listCalls += 1
      activeLists += 1
      maxActiveLists = Math.max(maxActiveLists, activeLists)
      if (slowLists) await new Promise((resolve) => setTimeout(resolve, 2500))
      activeLists -= 1
      return json(route, serverDocs)
    }
    return json(route, [])
  })

  await page.goto('/')
  await page.getByTestId('auth-username').fill('user')
  await page.getByTestId('auth-password').fill('password123')
  await page.getByTestId('auth-submit').click()
  await page.getByTestId('nav-documents').click()
  await expect(page.getByText('尚無文件,新增一份讓 AI 檢索。')).toBeVisible()

  await page.getByLabel('標題').fill('季報')
  await page.getByRole('button', { name: '貼上文字' }).click()
  await page.getByLabel('內容', { exact: true }).fill('第三季營收摘要')
  const callsBeforeCreate = listCalls
  slowLists = true
  await page.getByRole('button', { name: '新增文件' }).click()

  const row = page.locator('tbody tr').filter({ hasText: '季報' })
  await expect(row.locator('.chip')).toHaveText('處理中')

  // The server list is still empty here; wait out two whole poll replacements and assert the
  // optimistic row survived both of them.
  await expect.poll(() => listCalls, { timeout: 10_000 }).toBeGreaterThan(callsBeforeCreate + 1)
  await expect(row.locator('.chip')).toHaveText('處理中')
  await expect(page.locator('tbody tr')).toHaveCount(1)
  expect(maxActiveLists).toBe(1)

  // Once the consumer catches up the same id must merge in place: ready state, real chunk
  // count, and no duplicate row left behind by the pending buffer.
  serverDocs = [{ id: DOC_ID, title: '季報', status: 'ready', chunk_count: 3, created_at: '2026-07-25T00:00:00Z' }]
  await expect(row.locator('.chip')).toHaveText('就緒', { timeout: 10_000 })
  await expect(page.locator('tbody tr')).toHaveCount(1)
  await expect(row).toContainText('3')
})

test('lost confirmation exposes retry and reuses one idempotency key for the logical submit', async ({ page }) => {
  const attempts: Array<{ key: string | undefined; body: string | null }> = []
  let postCalls = 0

  await page.route('**/api/**', async (route) => {
    const request = route.request()
    const path = new URL(request.url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') {
      return json(route, { token: 'token', username: 'user', role: 'USER', tenantCode: 'demo', capabilities: [] })
    }
    if (path === '/api/features') return json(route, {})
    if (path === '/api/documents' && request.method() === 'GET') return json(route, [])
    if (path === '/api/documents' && request.method() === 'POST') {
      postCalls += 1
      attempts.push({ key: request.headers()['idempotency-key'], body: request.postData() })
      if (postCalls === 1) {
        return json(route, {
          timestamp: '2026-08-05T00:00:00Z', status: 502, code: 'bad_gateway',
          message: '上游服務暫時無法使用，請稍後再試', correlationId: 'c1', fieldErrors: {},
        }, 502)
      }
      return json(route, { id: DOC_ID, title: '季報', status: 'processing' }, 202)
    }
    return json(route, [])
  })

  await openDocuments(page)
  await fillDocument(page)
  await page.getByRole('button', { name: '新增文件' }).click()
  await expect(page.getByText('上游服務暫時無法使用，請稍後再試')).toBeVisible()
  await page.getByRole('button', { name: '再試一次' }).click()

  await expect(page.locator('tbody tr').filter({ hasText: '季報' })).toHaveCount(1)
  expect(attempts).toHaveLength(2)
  expect(attempts[0].key).toBeTruthy()
  expect(attempts[1].key).toBe(attempts[0].key)
  expect(attempts[1].body).toBe(attempts[0].body)
})

test('409 ends the logical attempt so the next submit receives a new key', async ({ page }) => {
  const keys: Array<string | undefined> = []

  await page.route('**/api/**', async (route) => {
    const request = route.request()
    const path = new URL(request.url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') {
      return json(route, { token: 'token', username: 'user', role: 'USER', tenantCode: 'demo', capabilities: [] })
    }
    if (path === '/api/features') return json(route, {})
    if (path === '/api/documents' && request.method() === 'GET') return json(route, [])
    if (path === '/api/documents' && request.method() === 'POST') {
      keys.push(request.headers()['idempotency-key'])
      if (keys.length === 1) {
        return json(route, {
          timestamp: '2026-08-05T00:00:00Z', status: 409, code: 'conflict',
          message: 'Idempotency-Key 已用於不同內容', correlationId: 'c1', fieldErrors: {},
        }, 409)
      }
      return json(route, { id: DOC_ID, title: '季報', status: 'processing' }, 202)
    }
    return json(route, [])
  })

  await openDocuments(page)
  await fillDocument(page)
  await page.getByRole('button', { name: '新增文件' }).click()
  await expect(page.getByText('Idempotency-Key 已用於不同內容')).toBeVisible()
  await expect(page.getByRole('button', { name: '新增文件' })).toBeVisible()
  await page.getByRole('button', { name: '新增文件' }).click()

  await expect(page.locator('tbody tr').filter({ hasText: '季報' })).toHaveCount(1)
  expect(keys).toHaveLength(2)
  expect(keys[0]).toBeTruthy()
  expect(keys[1]).not.toBe(keys[0])
})

test('late success does not clear form content edited after submit', async ({ page }) => {
  let releasePost!: () => void
  const postGate = new Promise<void>((resolve) => { releasePost = resolve })

  await page.route('**/api/**', async (route) => {
    const request = route.request()
    const path = new URL(request.url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') {
      return json(route, { token: 'token', username: 'user', role: 'USER', tenantCode: 'demo', capabilities: [] })
    }
    if (path === '/api/features') return json(route, {})
    if (path === '/api/documents' && request.method() === 'GET') return json(route, [])
    if (path === '/api/documents' && request.method() === 'POST') {
      await postGate
      return json(route, { id: DOC_ID, title: '季報', status: 'processing' }, 202)
    }
    return json(route, [])
  })

  await openDocuments(page)
  await fillDocument(page)
  await page.getByRole('button', { name: '新增文件' }).click()
  await page.getByLabel('標題').fill('下一份文件')
  releasePost()

  await expect(page.getByLabel('標題')).toHaveValue('下一份文件')
  await expect(page.locator('tbody tr').filter({ hasText: '季報' })).toHaveCount(1)
})
