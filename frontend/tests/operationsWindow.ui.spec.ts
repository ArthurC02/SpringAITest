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
  release_gate: { regression_passed: false, override_active: false, audit_entries: 1 },
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

/** 「根執行 · N 個子執行」那張加窗卡片(不再是第一張卡——發版判定卡刻意排在統計區間標示之前)。 */
function rootRunsCard(page: import('@playwright/test').Page) {
  return page.locator('.card').filter({ hasText: '個子執行' }).locator('.card__num')
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
  await expect(rootRunsCard(page)).toHaveText('5')
})

test('falls back to the default 90-day wording when the server omits the window field', async ({ page }) => {
  await mockOperations(page, METRICS_BODY, COMPARISON_BODY)

  await expect(page.getByText('統計區間:最近 90 天')).toHaveCount(2)
  await expect(rootRunsCard(page)).toHaveText('5')
})

// release gate 的判定(regression_passed / override_active)來自 Backend 不加窗的 LIMIT 1 讀取,
// 加窗會 fail-open,所以刻意不加窗。畫面因此不得把它擺進「統計區間:最近 N 天」的涵蓋範圍——
// 否則一筆窗外的失敗迴歸會被讀成已排除(選項 (g)「含義變了卻不說」的鏡像失敗:含義沒變卻說變了)。
test('the release-gate verdict sits outside the statistics-window notice and is labelled as unwindowed', async ({
  page,
}) => {
  await mockOperations(
    page,
    { ...METRICS_BODY, window_days: 30 },
    { ...COMPARISON_BODY, window_days: 30 },
  )

  const gateCard = page.locator('.card').filter({ hasText: '品質迴歸關卡' })
  await expect(gateCard).toHaveCount(1)
  await expect(gateCard.locator('.card__num')).toHaveText('未通過')
  await expect(gateCard).toContainText('最新一次迴歸結果,不受統計區間影響')
  // 同卡混用已拆開:加窗的稽核筆數不再與全歷史判定同卡。
  await expect(gateCard).not.toContainText('稽核紀錄')
  await expect(page.locator('.card').filter({ hasText: '發版稽核紀錄筆數' }).locator('.card__num')).toHaveText('1')

  // 判定卡片排在統計區間標示之前 → 不在其涵蓋範圍內。
  const gateBeforeNotice = await page.evaluate(() => {
    const card = [...document.querySelectorAll('.card')].find((c) =>
      c.textContent?.includes('品質迴歸關卡'),
    )
    const notice = [...document.querySelectorAll('p')].find((p) =>
      p.textContent?.startsWith('統計區間:'),
    )
    if (!card || !notice) return null
    return (card.compareDocumentPosition(notice) & Node.DOCUMENT_POSITION_FOLLOWING) !== 0
  })
  expect(gateBeforeNotice).toBe(true)
})
