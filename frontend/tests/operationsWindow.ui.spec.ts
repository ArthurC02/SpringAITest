import { expect, test, type Route } from '@playwright/test'

// W2-02(e) 統計區間明示:後端彙總查詢改為預設最近 90 天的時間窗,數字含義因此改變——
// 畫面必須在數字旁明示統計區間,不得靜默換窗(選項 (g)「加窗但不顯示」已被否決)。
// 兩個 class:
// (a) 伺服器帶出 window_days → 以伺服器值為準(即使不是 90);
// (b) 欄位缺席(尚未部署新後端)→ 退回預設文案「統計區間:最近 90 天」,畫面不得壞掉。

async function json(route: Route, body: unknown, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
}

/** 兩個測試共用:除 metrics/version-comparison 外一律回空集合。 */
async function mockOperations(
  page: import('@playwright/test').Page,
  metrics: unknown,
  comparison: unknown,
) {
  await page.route('**/api/**', async (route) => {
    const path = new URL(route.request().url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') {
      return json(route, {
        token: 'ops-window-token',
        username: 'admin',
        role: 'ADMIN',
        tenantCode: 'demo',
        capabilities: ['workflow.manage'],
      })
    }
    if (path === '/api/features') return json(route, { agentWriteToolsEnabled: true })
    if (path === '/api/admin/operations/metrics') return json(route, metrics)
    if (path === '/api/admin/operations/version-comparison') return json(route, comparison)
    return json(route, [])
  })
  await page.goto('/')
  await page.getByTestId('auth-username').fill('admin')
  await page.getByTestId('auth-password').fill('password123')
  await page.getByTestId('auth-submit').click()
  await page.getByTestId('nav-operations').click()
}

const METRICS_BODY = {
  release_gate: { regression_passed: true, override_active: false, audit_entries: 1 },
  multi_agent: {
    root_runs: 5,
    child_runs: 3,
    child_success: 3,
    agents: [],
    skills: [],
    tools: [],
    nodes: [],
    aggregation: { completed: 4, partialOrFailed: 1, averageFanOut: 2, averageLatencyMs: 300 },
  },
}

const COMPARISON_BODY = {
  selected_revision: null,
  rollout_events: 0,
  new_roots_only: false,
  active_runs_keep_immutable_snapshot: false,
  revisions: [],
  selected_vs_previous: null,
}

test('the dashboard states the server-reported statistics window next to the numbers', async ({ page }) => {
  await mockOperations(
    page,
    { ...METRICS_BODY, window_days: 30 },
    { ...COMPARISON_BODY, window_days: 30 },
  )

  // 兩個數字區塊(彙總卡片 + 版本比較)各自明示自己的區間,伺服器值優先於預設。
  await expect(page.getByText('統計區間:最近 30 天')).toHaveCount(2)
  await expect(page.getByText('統計區間:最近 90 天')).toHaveCount(0)
  // 數字仍然照常呈現——明示區間是加上說明,不是替換內容。
  await expect(page.locator('.card__num').first()).toHaveText('5')
})

test('falls back to the default 90-day wording when the server omits the window field', async ({ page }) => {
  await mockOperations(page, METRICS_BODY, COMPARISON_BODY)

  await expect(page.getByText('統計區間:最近 90 天')).toHaveCount(2)
  await expect(page.locator('.card__num').first()).toHaveText('5')
})
