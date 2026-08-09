import { expect, test, type Page, type Route } from '@playwright/test'

async function json(route: Route, body: unknown, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
}

async function login(
  page: Page,
  options: { role?: 'ADMIN' | 'USER'; capabilities?: string[]; features?: Record<string, boolean> },
) {
  await page.route('**/api/**', async (route) => {
    const request = route.request()
    const path = new URL(request.url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') {
      return json(route, {
        token: 'runs-token', username: 'tester', role: options.role ?? 'ADMIN', tenantCode: 'demo',
        capabilities: options.capabilities ?? [],
      })
    }
    if (path === '/api/features') return json(route, options.features ?? {})
    return json(route, [])
  })
  await page.goto('/')
  await page.getByTestId('auth-username').fill('tester')
  await page.getByTestId('auth-password').fill('password123')
  await page.getByTestId('auth-submit').click()
}

// 三個閘門:flag 關、無 capability、兩者皆備才顯示側欄入口(比照 Operations 的 nav gating 模式)。
test('the 執行總覽 sidebar entry requires both runDiscoveryEnabled and exact workflow.manage', async ({ page }) => {
  await login(page, { capabilities: ['workflow.manage'], features: { runDiscoveryEnabled: false } })
  await expect(page.getByTestId('nav-runs')).toHaveCount(0)
})

test('workflow.manage without the flag still hides the entry', async ({ page }) => {
  await login(page, { capabilities: [], features: { runDiscoveryEnabled: true } })
  await expect(page.getByTestId('nav-runs')).toHaveCount(0)
})

test('a capability that merely starts with workflow.manage is not a match', async ({ page }) => {
  await login(page, { capabilities: ['workflow.manage.other'], features: { runDiscoveryEnabled: true } })
  await expect(page.getByTestId('nav-runs')).toHaveCount(0)
})

test('both the flag and exact workflow.manage open the entry, USER role included', async ({ page }) => {
  await login(page, { role: 'USER', capabilities: ['workflow.manage'], features: { runDiscoveryEnabled: true } })
  await expect(page.getByTestId('nav-runs')).toBeVisible()
})

// root(協作)列:running + pending_approval(chip--warn,不撞 status 的 chip--processing)。
const rootRun = {
  id: '11111111-1111-4111-8111-111111111111', kind: 'orchestrator', status: 'running',
  orchestrator_root_run_id: null, task_id: null,
  agent_id: null, agent_revision: null,
  orchestrator_id: '22222222-2222-4222-8222-222222222222', orchestrator_revision: 2,
  workflow_id: '33333333-3333-4333-8333-333333333333', workflow_revision: 1,
  cancel_requested: false, budget_summary: { token_budget: 1000 },
  last_event_type: 'child.dispatched', last_event_at: '2026-01-01T00:01:00Z',
  child_progress: { total: 3, queued: 1, running: 1, completed: 1, failed: 0, cancelled: 0 },
  error_class: null, pending_approval: true, needs_recovery: false,
  started_at: '2026-01-01T00:00:00Z', created_at: '2026-01-01T00:00:00Z',
  updated_at: '2026-01-01T00:05:00Z', completed_at: null, elapsed_seconds: 305,
}

// worker 列:cancelled(chip--skip)+ needs_recovery(chip--failed) — 兩個徽章刻意不同 class,
// 避免測試選擇器與 pending_approval/needs_recovery 徽章的 class 撞在一起。
const workerRun = {
  id: '44444444-4444-4444-8444-444444444444', kind: 'worker', status: 'cancelled',
  orchestrator_root_run_id: rootRun.id, task_id: 'task-1',
  agent_id: '55555555-5555-4555-8555-555555555555', agent_revision: 7,
  orchestrator_id: null, orchestrator_revision: null,
  workflow_id: '66666666-6666-4666-8666-666666666666', workflow_revision: 3,
  cancel_requested: false, budget_summary: {},
  last_event_type: 'run.cancelled', last_event_at: '2026-01-01T00:02:00Z',
  child_progress: null, error_class: 'timeout', pending_approval: false, needs_recovery: true,
  started_at: '2026-01-01T00:00:30Z', created_at: '2026-01-01T00:00:00Z',
  updated_at: '2026-01-01T00:02:00Z', completed_at: '2026-01-01T00:02:00Z', elapsed_seconds: 45,
}

async function mountRunsView(
  page: Page,
  onList: (route: Route, url: URL) => Promise<boolean>,
  onDirectory?: (route: Route, path: string) => Promise<boolean>,
) {
  await page.route('**/api/**', async (route) => {
    const request = route.request()
    const url = new URL(request.url())
    const path = url.pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') {
      return json(route, {
        token: 'runs-token', username: 'tester', role: 'ADMIN', tenantCode: 'demo',
        capabilities: ['workflow.manage'],
      })
    }
    if (path === '/api/features') return json(route, { runDiscoveryEnabled: true })
    if (path === '/api/runs') {
      const handled = await onList(route, url)
      if (handled) return
    }
    if (onDirectory && (path === '/api/agents' || path === '/api/admin/orchestrators')) {
      const handled = await onDirectory(route, path)
      if (handled) return
    }
    return json(route, [])
  })
  await page.goto('/')
  await page.getByTestId('auth-username').fill('tester')
  await page.getByTestId('auth-password').fill('password123')
  await page.getByTestId('auth-submit').click()
  await page.getByTestId('nav-runs').click()
}

// 選擇器一律走 `.badge--user`(種類徽章)/`.chip--*`(狀態/pending/recovery 徽章)這種 class 定位,
// 不用純文字 getByText——種類/狀態的中文字面同時也出現在過濾下拉的 <option> 裡,純文字定位會撞到。
test('renders Chinese kind/status labels, elapsed time, child progress and the two badges', async ({ page }) => {
  await mountRunsView(page, async (route, url) => {
    if (url.searchParams.toString()) return false
    await json(route, { items: [rootRun, workerRun], has_more: false, next_cursor: null })
    return true
  })

  await expect(page.getByRole('heading', { name: '執行總覽' })).toBeVisible()
  const rootRow = page.locator('article.agent-block', { hasText: '待核准 · 請至「Run 核准」處理' })
  const workerRow = page.locator('article.agent-block', { hasText: '需要復原' })

  await expect(rootRow.locator('.badge--user')).toHaveText('協作 root')
  await expect(rootRow.locator('.chip--processing')).toHaveText('執行中')
  await expect(rootRow.getByText('5 分鐘')).toBeVisible()
  await expect(rootRow.getByText('共 3(排隊 1‧執行中 1‧完成 1‧失敗 0‧取消 0)')).toBeVisible()

  await expect(workerRow.locator('.badge--user')).toHaveText('Worker')
  await expect(workerRow.locator('.chip--skip')).toHaveText('已取消')
  await expect(workerRow.getByText('45 秒')).toBeVisible()
  await expect(workerRow.locator('.chip--failed')).toHaveText('需要復原')

  // 展開顯示其餘欄位(不做原始 JSON dump):<details> 內容平時是 hidden,展開後可見
  // Task ID 這種只在展開區才有的欄位。
  await expect(workerRow.getByText('task-1')).toBeHidden()
  await workerRow.getByText('展開其餘欄位').click()
  await expect(workerRow.getByText('task-1')).toBeVisible()
})

test('the status/kind dropdowns forward the selected backend query params and replace the list', async ({ page }) => {
  const calls: string[] = []
  await mountRunsView(page, async (route, url) => {
    calls.push(url.search)
    if (!url.searchParams.toString()) {
      await json(route, { items: [rootRun], has_more: false, next_cursor: null })
      return true
    }
    if (url.searchParams.get('status') === 'cancelled' && url.searchParams.get('kind') === 'worker') {
      await json(route, { items: [workerRun], has_more: false, next_cursor: null })
      return true
    }
    await json(route, { items: [], has_more: false, next_cursor: null })
    return true
  })

  await expect(page.locator('.badge--user')).toHaveText(['協作 root'])
  await page.getByLabel('狀態').selectOption('cancelled')
  await page.getByLabel('種類').selectOption('worker')
  await expect(page.locator('.badge--user')).toHaveText(['Worker'])
  expect(calls).toContainEqual('?kind=worker&status=cancelled')
})

test('載入更多 appends the next keyset page instead of replacing the first one', async ({ page }) => {
  await mountRunsView(page, async (route, url) => {
    const cursor = url.searchParams.get('cursor')
    if (!cursor) {
      await json(route, { items: [rootRun], has_more: true, next_cursor: 'cursor-1' })
      return true
    }
    if (cursor === 'cursor-1') {
      await json(route, { items: [workerRun], has_more: false, next_cursor: null })
      return true
    }
    return false
  })

  await expect(page.locator('.badge--user')).toHaveText(['協作 root'])
  await expect(page.getByRole('button', { name: '載入更多' })).toBeVisible()
  await page.getByRole('button', { name: '載入更多' }).click()
  // 兩頁都在畫面上,依附加順序排列(不是取代)。
  await expect(page.locator('.badge--user')).toHaveText(['協作 root', 'Worker'])
  await expect(page.getByRole('button', { name: '載入更多' })).toHaveCount(0)
})

// 名稱反查用的兩支目錄 API 各自有獨立的 flag + 權限,對本畫面的合法使用者很可能回 404/403,
// 所以名稱是純裝飾:查得到就顯示,查不到/整支失敗都必須靜默退回短 GUID。
const agentDirectory = [{
  id: workerRun.agent_id, name: '報表小幫手', slug: 'report-helper', description: '',
  enabled: true, published_revision: 7, updated_at: '2026-01-01T00:00:00Z',
}]
const orchestratorDirectory = [{
  id: rootRun.orchestrator_id, name: '客服協作', description: '',
  enabled: true, published_revision: 2, updated_at: '2026-01-01T00:00:00Z',
}]

async function mountBothRuns(page: Page, onDirectory: (route: Route, path: string) => Promise<boolean>) {
  await mountRunsView(page, async (route, url) => {
    if (url.searchParams.toString()) return false
    await json(route, { items: [rootRun, workerRun], has_more: false, next_cursor: null })
    return true
  }, onDirectory)
}

test('resolved directory names are prefixed onto the short run identity', async ({ page }) => {
  await mountBothRuns(page, async (route, path) => {
    await json(route, path === '/api/agents' ? agentDirectory : orchestratorDirectory)
    return true
  })

  const rootRow = page.locator('article.agent-block', { hasText: '協作 root' })
  const workerRow = page.locator('article.agent-block', { hasText: '需要復原' })
  await expect(rootRow.getByText('客服協作 · 22222222 · r2', { exact: true })).toBeVisible()
  await expect(workerRow.getByText('報表小幫手 · 55555555 · r7', { exact: true })).toBeVisible()
})

for (const status of [404, 500]) {
  test(`a ${status} from the directory APIs fails open silently and keeps the short identity`, async ({ page }) => {
    await mountBothRuns(page, async (route) => {
      await json(route, { timestamp: '2026-01-01T00:00:00Z', status, message: '目錄不可用', fieldErrors: {} }, status)
      return true
    })

    const rootRow = page.locator('article.agent-block', { hasText: '協作 root' })
    const workerRow = page.locator('article.agent-block', { hasText: '需要復原' })
    await expect(rootRow.getByText('22222222 · r2', { exact: true })).toBeVisible()
    await expect(workerRow.getByText('55555555 · r7', { exact: true })).toBeVisible()
    // 主清單照常渲染,且目錄失敗不得產生任何錯誤訊息或 toast。
    await expect(page.getByRole('alert')).toHaveCount(0)
    await expect(page.locator('.toast')).toHaveCount(0)
    await expect(page.getByText('目錄不可用')).toHaveCount(0)
  })
}

test('an empty page shows the empty-state notice, and a failure shows an error with a working retry', async ({ page }) => {
  let mode: 'fail' | 'empty' = 'fail'
  await mountRunsView(page, async (route) => {
    if (mode === 'fail') {
      await json(route, { timestamp: '2026-08-09T00:00:00Z', status: 500, message: '伺服器暫時無法回應', fieldErrors: {} }, 500)
      return true
    }
    await json(route, { items: [], has_more: false, next_cursor: null })
    return true
  })

  await expect(page.getByRole('alert')).toContainText('伺服器暫時無法回應')
  mode = 'empty'
  await page.getByRole('button', { name: '重新載入' }).click()
  await expect(page.getByText('目前沒有可顯示的執行紀錄。')).toBeVisible()
  await expect(page.getByRole('alert')).toHaveCount(0)
})
