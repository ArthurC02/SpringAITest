import { expect, test, type Route } from '@playwright/test'

// The 202 flow is eventually consistent: the document is absent from GET /api/documents until
// the RabbitMQ consumer has stored it. `useDocuments` bridges that with an optimistic row plus
// a pendingRef merge, and the regression this guards is the optimistic row being wiped by the
// very next full-list replacement (the user watches their upload vanish, then reappear).

const DOC_ID = '11111111-1111-4111-8111-111111111111'

async function json(route: Route, body: unknown, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
}

test('202 create optimistically inserts a processing row that a full list replacement cannot wipe', async ({ page }) => {
  let serverDocs: unknown[] = []
  let listCalls = 0

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
  await page.getByRole('button', { name: '新增文件' }).click()

  const row = page.locator('tbody tr').filter({ hasText: '季報' })
  await expect(row.locator('.chip')).toHaveText('處理中')

  // The server list is still empty here; wait out two whole poll replacements and assert the
  // optimistic row survived both of them.
  await expect.poll(() => listCalls, { timeout: 10_000 }).toBeGreaterThan(callsBeforeCreate + 1)
  await expect(row.locator('.chip')).toHaveText('處理中')
  await expect(page.locator('tbody tr')).toHaveCount(1)

  // Once the consumer catches up the same id must merge in place: ready state, real chunk
  // count, and no duplicate row left behind by the pending buffer.
  serverDocs = [{ id: DOC_ID, title: '季報', status: 'ready', chunk_count: 3, created_at: '2026-07-25T00:00:00Z' }]
  await expect(row.locator('.chip')).toHaveText('就緒', { timeout: 10_000 })
  await expect(page.locator('tbody tr')).toHaveCount(1)
  await expect(row).toContainText('3')
})
