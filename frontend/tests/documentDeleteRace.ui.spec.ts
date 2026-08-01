import { expect, test, type Route } from '@playwright/test'

const DOC_ID = '11111111-1111-4111-8111-111111111111'

async function json(route: Route, body: unknown, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
}

test('a delayed pre-delete list response cannot restore a deleted row', async ({ page }) => {
  const document = { id: DOC_ID, title: 'race', status: 'processing', chunk_count: 0, created_at: '2026-07-25T00:00:00Z' }
  let listCalls = 0
  let releaseList!: () => void
  const delayedList = new Promise<void>((resolve) => { releaseList = resolve })

  await page.route('**/api/**', async (route) => {
    const request = route.request()
    const path = new URL(request.url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') return json(route, { token: 'token', username: 'user', role: 'USER', tenantCode: 'demo', capabilities: [] })
    if (path === '/api/features') return json(route, {})
    if (path === '/api/documents' && request.method() === 'GET') {
      listCalls += 1
      if (listCalls === 3) await delayedList
      return json(route, [document])
    }
    if (path === `/api/documents/${DOC_ID}` && request.method() === 'DELETE') return json(route, {})
    return json(route, [])
  })

  await page.goto('/')
  await page.getByTestId('auth-username').fill('user')
  await page.getByTestId('auth-password').fill('password123')
  await page.getByTestId('auth-submit').click()
  await page.getByTestId('nav-documents').click()
  const row = page.locator('tbody tr').filter({ hasText: 'race' })
  await expect(row).toBeVisible()
  await expect.poll(() => listCalls, { timeout: 5_000 }).toBe(3)
  await row.locator('.btn--danger').click()
  await page.locator('.confirm-dialog__confirm--danger').click()
  await expect(row).toHaveCount(0)
  releaseList()
  await page.waitForTimeout(500)
  await expect(row).toHaveCount(0)
})
