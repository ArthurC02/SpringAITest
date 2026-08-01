import { expect, test, type Route } from '@playwright/test'

async function json(route: Route, body: unknown, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
}

test('an initial list response completing after logout cannot restart polling', async ({ page }) => {
  let listCalls = 0
  let releaseList!: () => void
  const delayedList = new Promise<void>((resolve) => { releaseList = resolve })

  await page.route('**/api/**', async (route) => {
    const path = new URL(route.request().url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') return json(route, { token: 'token', username: 'user', role: 'USER', tenantCode: 'demo', capabilities: [] })
    if (path === '/api/features') return json(route, {})
    if (path === '/api/documents') {
      listCalls += 1
      await delayedList
      return json(route, [{ id: 'late', title: 'late', status: 'processing', chunk_count: 0, created_at: '2026-07-25T00:00:00Z' }])
    }
    return json(route, [])
  })

  await page.goto('/')
  await page.getByTestId('auth-username').fill('user')
  await page.getByTestId('auth-password').fill('password123')
  await page.getByTestId('auth-submit').click()
  await expect.poll(() => listCalls).toBe(2)
  // dispatchEvent instead of click: CopilotKit's dev inspector overlay covers the top bar.
  await page.getByTestId('logout-button').dispatchEvent('click')
  releaseList()
  await expect(page.getByTestId('auth-username')).toBeVisible()
  await page.waitForTimeout(2500)
  expect(listCalls).toBe(2)
})
