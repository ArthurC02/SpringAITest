import { expect, test, type Page, type Route } from '@playwright/test'

// P1-01/P1-02(共通層的 409 假成功 toast)與 P1-03/P1-04(JSON 欄位受控化)的瀏覽器回歸。
// 授權來源:`Toast.tsx` 的 runWithToast(conflict 由本層處理,永不噴成功 toast)與
// `OrchestratorsView.tsx` 的 JsonField(受控 text + 欄位錯誤 + 停用寫入動作)。

const definition = {
  instructions: 'Coordinate the workers.',
  policy: { joinPolicy: 'fail-fast', repairPolicy: 'fail' },
  workflow: { id: 'w1', revision: 1 },
  workerPool: [{ agentId: 'a1', revision: 2 }],
  workerPolicy: { requiredAudience: [], requiredCapabilities: [] },
  context: { allowedTools: [], knowledgeSources: [] },
  audience: [],
  capabilities: [],
  verifier: { agentId: 'v1', revision: 1 },
  budgets: {
    maxContextRounds: 2, maxTasks: 8, maxChildRuns: 9, maxConcurrency: 4,
    maxRepairRounds: 1, tokenBudget: 10000, timeoutSeconds: 300,
  },
}

const orchestrator = {
  id: 'o1', name: 'Root', description: 'Collaboration runtime', enabled: true,
  draft_version: 1, published_revision: null, updated_at: '2026-08-03T00:00:00Z', definition,
}

async function json(route: Route, body: unknown, headers: Record<string, string> = {}) {
  await route.fulfill({ status: 200, contentType: 'application/json', headers, body: JSON.stringify(body) })
}

interface Harness {
  /** PUT /draft 的下一次回應狀態(409 用來觸發樂觀併發衝突)。 */
  draftStatus: number
  /** 伺服器目前持有的 orchestrator（重新載入會讀到這份）。 */
  current: typeof orchestrator
  /** 最後一次送出的 draft payload。 */
  saved: { definition: { budgets: Record<string, number> } } | null
}

async function openEditor(page: Page): Promise<Harness> {
  const harness: Harness = { draftStatus: 200, current: orchestrator, saved: null }
  await page.route('**/api/**', async (route) => {
    const request = route.request()
    const path = new URL(request.url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') {
      return json(route, {
        token: 'orchestrator-token', username: 'tester', role: 'ADMIN', tenantCode: 'demo',
        capabilities: ['workflow.manage'],
      })
    }
    if (path === '/api/features') return json(route, { workflowDesignerEnabled: true })
    if (path === '/api/admin/orchestrators' && request.method() === 'GET') return json(route, [harness.current])
    if (path === '/api/admin/orchestrators' && request.method() === 'POST') return json(route, harness.current)
    if (path === '/api/admin/orchestrators/o1') return json(route, harness.current, { ETag: `"${harness.current.draft_version}"` })
    if (path === '/api/admin/orchestrators/o1/draft' && request.method() === 'PUT') {
      harness.saved = request.postDataJSON() as Harness['saved']
      if (harness.draftStatus === 409) {
        return route.fulfill({
          status: 409, contentType: 'application/json',
          body: JSON.stringify({ timestamp: '2026-08-03T00:00:00Z', status: 409, message: 'draft 版本衝突', fieldErrors: {} }),
        })
      }
      return json(route, harness.current)
    }
    return json(route, [])
  })

  await page.goto('/')
  await page.getByTestId('auth-username').fill('tester')
  await page.getByTestId('auth-password').fill('password123')
  await page.getByTestId('auth-submit').click()
  await page.getByTestId('nav-agentPlatform').click()
  await page.getByTestId('agent-platform-tab-orchestrators').click()
  await page.getByRole('button', { name: '編輯' }).click()
  await expect(page.getByRole('heading', { name: 'Root', level: 2 })).toBeVisible()
  return harness
}

test('a 409 draft save locks the editor, exposes reload, and emits no success toast', async ({ page }) => {
  const harness = await openEditor(page)
  const save = page.getByRole('button', { name: '儲存', exact: true })
  const name = page.getByLabel('名稱')

  harness.draftStatus = 409
  await name.fill('Renamed root')
  await save.click()

  await expect(page.getByRole('alert')).toContainText('草稿已過期')
  await expect(page.locator('.toast--success')).toHaveCount(0)
  await expect(name).toBeDisabled()
  await expect(save).toBeDisabled()
  await expect(page.getByRole('button', { name: '驗證' })).toBeDisabled()
  await expect(page.getByRole('button', { name: '發布' })).toBeDisabled()

  // 鎖定不是永久的:重新載入取得新資料與新 ETag,顯示同步到伺服器版本後才恢復可寫。
  harness.draftStatus = 200
  harness.current = {
    ...orchestrator, draft_version: 2, name: 'Root',
    definition: { ...definition, budgets: { ...definition.budgets, maxTasks: 12 } },
  }
  await page.getByRole('button', { name: '重新載入' }).click()
  await expect(page.getByRole('alert')).toHaveCount(0)
  await expect(name).toBeEnabled()
  // W5:Budgets 預設為結構化數字 grid（非裸 JSON），見 orchestrator-budget-maxTasks。
  await expect(page.locator('#orchestrator-budget-maxTasks')).toHaveValue('12')

  await save.click()
  await expect(page.locator('.toast--success')).toContainText('草稿已儲存')
})

test('invalid JSON blocks save/validate/publish and a correction restores them without stale payload', async ({ page }) => {
  const harness = await openEditor(page)
  // W5:Budgets 預設是結構化 grid，裸 JSON 驗證路徑只在「進階 JSON 模式」逃生口才可達。
  await page.getByRole('button', { name: 'Budgets — 切換為進階 JSON 模式' }).click()
  const budgets = page.getByLabel('Budgets(JSON 進階模式)')
  const save = page.getByRole('button', { name: '儲存', exact: true })
  const validate = page.getByRole('button', { name: '驗證' })
  const publish = page.getByRole('button', { name: '發布' })

  await expect(save).toBeEnabled()
  await budgets.fill('{"maxTasks": 8,')
  await expect(page.getByText('JSON 格式錯誤')).toBeVisible()
  // 畫面保留使用者輸入(非受控時會與 draft 脫節),而寫入動作全部停用。
  await expect(budgets).toHaveValue('{"maxTasks": 8,')
  await expect(save).toBeDisabled()
  await expect(validate).toBeDisabled()
  await expect(publish).toBeDisabled()

  await budgets.fill(JSON.stringify({ ...definition.budgets, maxTasks: 42 }))
  await expect(page.getByText('JSON 格式錯誤')).toHaveCount(0)
  await expect(save).toBeEnabled()
  await save.click()
  await expect(page.locator('.toast--success')).toContainText('草稿已儲存')
  expect(harness.saved?.definition.budgets.maxTasks).toBe(42)
})

// 規格 §5.5:Worker/Verifier 下拉只能選到已發布、且（若後端有回傳角色資料）角色相符的
// Agent；欄位缺席/legacy null 時 fail-open,不過濾、維持「僅已發布」現行行為。
test('create form Worker/Verifier pickers filter by published_execution_roles, fail-open when the field is absent', async ({ page }) => {
  await page.route('**/api/**', async (route) => {
    const request = route.request()
    const path = new URL(request.url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') {
      return json(route, {
        token: 'orch-role-filter-token', username: 'admin', role: 'ADMIN', tenantCode: 'demo',
        capabilities: ['workflow.manage'],
      })
    }
    if (path === '/api/features') return json(route, { workflowDesignerEnabled: true })
    if (path === '/api/agents') {
      return json(route, [
        { id: 'worker-only', name: 'Worker Only', slug: 'worker-only', description: '', enabled: true, published_revision: 1, published_execution_roles: ['worker'], updated_at: '2026-08-09T00:00:00Z' },
        { id: 'verifier-only', name: 'Verifier Only', slug: 'verifier-only', description: '', enabled: true, published_revision: 1, published_execution_roles: ['verifier'], updated_at: '2026-08-09T00:00:00Z' },
        { id: 'both-roles', name: 'Both Roles', slug: 'both-roles', description: '', enabled: true, published_revision: 1, published_execution_roles: ['worker', 'verifier'], updated_at: '2026-08-09T00:00:00Z' },
        { id: 'legacy-no-roles', name: 'Legacy No Roles', slug: 'legacy-no-roles', description: '', enabled: true, published_revision: 1, published_execution_roles: null, updated_at: '2026-08-09T00:00:00Z' },
        { id: 'unpublished', name: 'Unpublished', slug: 'unpublished', description: '', enabled: true, published_revision: null, published_execution_roles: ['worker', 'verifier'], updated_at: '2026-08-09T00:00:00Z' },
      ])
    }
    if (path === '/api/admin/orchestrators' && request.method() === 'GET') return json(route, [])
    return json(route, [])
  })

  await page.goto('/')
  await page.getByTestId('auth-username').fill('admin')
  await page.getByTestId('auth-password').fill('password123')
  await page.getByTestId('auth-submit').click()
  await page.getByTestId('nav-agentPlatform').click()
  await page.getByTestId('agent-platform-tab-orchestrators').click()
  await page.getByRole('button', { name: '＋ 建立' }).click()
  await page.getByRole('button', { name: '＋ 加入 Worker' }).click()

  const workerPicker = page.locator('#orchestrator-create-worker-pool-0-agent')
  const verifierPicker = page.locator('#orchestrator-create-verifier-agent')
  await expect(workerPicker).toHaveJSProperty('tagName', 'SELECT')
  await expect(verifierPicker).toHaveJSProperty('tagName', 'SELECT')

  const workerLabels = await workerPicker.locator('option').allTextContents()
  const verifierLabels = await verifierPicker.locator('option').allTextContents()

  // Worker pool: worker-only、both-roles、legacy-no-roles(fail-open)在列；verifier-only 與
  // unpublished 不在列。
  expect(workerLabels.some((t) => t.includes('Worker Only'))).toBe(true)
  expect(workerLabels.some((t) => t.includes('Both Roles'))).toBe(true)
  expect(workerLabels.some((t) => t.includes('Legacy No Roles'))).toBe(true)
  expect(workerLabels.some((t) => t.includes('Verifier Only'))).toBe(false)
  expect(workerLabels.some((t) => t.includes('Unpublished'))).toBe(false)

  // Verifier: verifier-only、both-roles、legacy-no-roles(fail-open)在列;worker-only 與
  // unpublished 不在列。
  expect(verifierLabels.some((t) => t.includes('Verifier Only'))).toBe(true)
  expect(verifierLabels.some((t) => t.includes('Both Roles'))).toBe(true)
  expect(verifierLabels.some((t) => t.includes('Legacy No Roles'))).toBe(true)
  expect(verifierLabels.some((t) => t.includes('Worker Only'))).toBe(false)
  expect(verifierLabels.some((t) => t.includes('Unpublished'))).toBe(false)
})

test('invalid JSON in the create form blocks 建立', async ({ page }) => {
  await page.route('**/api/**', async (route) => {
    const request = route.request()
    const path = new URL(request.url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') {
      return json(route, {
        token: 'orchestrator-token', username: 'tester', role: 'ADMIN', tenantCode: 'demo',
        capabilities: ['workflow.manage'],
      })
    }
    if (path === '/api/features') return json(route, { workflowDesignerEnabled: true })
    if (path === '/api/admin/orchestrators' && request.method() === 'GET') return json(route, [])
    return json(route, [])
  })
  await page.goto('/')
  await page.getByTestId('auth-username').fill('tester')
  await page.getByTestId('auth-password').fill('password123')
  await page.getByTestId('auth-submit').click()
  await page.getByTestId('nav-agentPlatform').click()
  await page.getByTestId('agent-platform-tab-orchestrators').click()
  await page.getByRole('button', { name: '＋ 建立' }).click()

  await page.getByLabel('名稱').fill('New root')
  await page.getByLabel('Root Workflow id').fill('w1')
  await page.getByLabel('Workflow revision').fill('1')
  await page.getByLabel('Verifier Agent id').fill('v1')
  await page.getByLabel('Verifier revision').fill('1')
  // W5:Worker pool 預設是結構化清單（無已發布 Agent 目錄時自動退回逐列手動輸入），
  // 裸 JSON 驗證路徑只在「進階 JSON 模式」逃生口才可達。
  await page.getByRole('button', { name: 'Worker pool — 切換為進階 JSON 模式' }).click()
  const workerPool = page.getByLabel('Worker pool(JSON 進階模式)')
  await workerPool.fill('[{"agentId": "a1", "revision": 2}]')
  const create = page.getByRole('button', { name: '建立', exact: true })
  await expect(create).toBeEnabled()

  await workerPool.fill('[{"agentId": "a1"')
  await expect(page.getByText('JSON 格式錯誤')).toBeVisible()
  await expect(create).toBeDisabled()
})
