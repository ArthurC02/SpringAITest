import { expect, test, type Route } from '@playwright/test'

// W4 功能開通狀態頁(規格 §4):唯讀呈現既有 GET /api/features 已取回的旗標,不新增端點、
// 不新增未認證面(端點本身就是 AllowAnonymous)。ADMIN-only 側欄過濾比照系統設定的
// adminOnly 模式;USER 完全看不到入口。

async function json(route: Route, body: unknown, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
}

test('ADMIN sees the feature status page rendering the already-fetched /api/features flags in Chinese', async ({ page }) => {
  let featuresRequests = 0
  await page.route('**/api/**', async (route) => {
    const path = new URL(route.request().url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') {
      return json(route, { token: 'features-token', username: 'admin', role: 'ADMIN', tenantCode: 'demo' })
    }
    if (path === '/api/features') {
      featuresRequests += 1
      return json(route, {
        agentBuilderEnabled: true,
        agentTestRunEnabled: false,
        workflowDesignerEnabled: true,
        multiAgentDispatchEnabled: false,
        agentChatEnabled: false,
        agentWriteToolsEnabled: true,
      })
    }
    return json(route, [])
  })

  await page.goto('/')
  await page.getByTestId('auth-username').fill('admin')
  await page.getByTestId('auth-password').fill('password123')
  await page.getByTestId('auth-submit').click()
  await expect(page.getByTestId('session-identity')).toBeVisible()
  // React StrictMode double-invokes the mount effect in dev, so settle before sampling —
  // the point under test is that *opening the Features view* triggers no extra call, not
  // that the app only ever calls /api/features once.
  await expect.poll(() => featuresRequests).toBeGreaterThan(0)
  const requestsBeforeNav = featuresRequests

  await page.getByTestId('nav-features').click()

  await expect(page.getByRole('heading', { name: '功能開通狀態' })).toBeVisible()
  const builderRow = page.locator('tr', { hasText: 'Agent 建置器' })
  await expect(builderRow).toContainText('已開通')
  const testConsoleRow = page.locator('tr', { hasText: 'Agent 測試主控台' })
  await expect(testConsoleRow).toContainText('未開通')
  const writeToolsRow = page.locator('tr', { hasText: '寫入核准與治理' })
  await expect(writeToolsRow).toContainText('已開通')

  // Reuses the AppShell-level fetch (props, not a re-fetch) — opening the view adds no call.
  expect(featuresRequests).toBe(requestsBeforeNav)
})

test('a USER never sees the feature status nav entry', async ({ page }) => {
  await page.route('**/api/**', async (route) => {
    const path = new URL(route.request().url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') {
      return json(route, { token: 'features-user-token', username: 'user', role: 'USER', tenantCode: 'demo' })
    }
    if (path === '/api/features') return json(route, { agentBuilderEnabled: true })
    return json(route, [])
  })

  await page.goto('/')
  await page.getByTestId('auth-username').fill('user')
  await page.getByTestId('auth-password').fill('password123')
  await page.getByTestId('auth-submit').click()

  await expect(page.getByTestId('session-identity')).toBeVisible()
  await expect(page.getByTestId('nav-features')).toHaveCount(0)
})
