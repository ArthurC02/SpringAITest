import { expect, test, type Page, type Route } from '@playwright/test'
import { strToU8, zipSync } from 'fflate'
async function json(route: Route, body: unknown) {
  await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) })
}

async function signIn(page: Page, withRestore = false): Promise<string[]> {
  const requested: string[] = []
  let flowRevision = 2
  const flow = {
    name: 'expense-review', description: 'Flow only', required_role: 'USER', current_revision: 2,
    updated_at: '2026-07-30T00:00:00Z', enabled: true, kind: 'flow',
  }
  const agentic = {
    name: 'invoice-skill', description: 'Package only', required_role: 'USER', current_revision: 3,
    updated_at: '2026-07-30T00:00:00Z', enabled: true, kind: 'agentic',
  }
  await page.route('**/api/**', async (route) => {
    const path = new URL(route.request().url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    requested.push(path)
    if (path === '/api/auth/login') return json(route, {
      token: 'token', username: 'tester', role: 'ADMIN', tenantCode: 'demo', capabilities: [],
    })
    if (path === '/api/features') return json(route, {})
    if (path === '/api/business-workflows') return json(route, [{ ...flow, current_revision: flowRevision }])
    if (path === '/api/business-workflows/expense-review') {
      return json(route, { ...flow, current_revision: flowRevision, definition: 'name: expense-review' })
    }
    if (path === '/api/skills') return json(route, [agentic, flow])
    if (path === '/api/skills/catalog') return json(route, [
      { ...flow, source: 'custom', revision: flowRevision, bindable: true },
      { ...agentic, source: 'custom', revision: 3, bindable: true },
      {
        name: 'builtin-agent-helper', description: 'Read-only package', required_role: 'USER',
        source: 'builtin', revision: 1, bindable: true, kind: 'agentic',
        definition: '---\nname: builtin-agent-helper\ndescription: Read-only package\n---\nBuilt in body',
      },
    ])
    if (withRestore && path === '/api/skills/expense-review/revisions') return json(route, [
      { revision: 2, definition: 'name: expense-review', definition_sha256: '222222222222', created_by: 'tester', created_at: '2026-07-30T00:00:00Z', kind: 'flow' },
      { revision: 1, definition: 'name: expense-review', definition_sha256: '111111111111', created_by: 'tester', created_at: '2026-07-29T00:00:00Z', kind: 'flow' },
    ])
    if (withRestore && path === '/api/skills/expense-review/revisions/1/restore') {
      flowRevision = 3
      return json(route, { ...flow, current_revision: flowRevision, definition: 'name: expense-review' })
    }
    if (path.endsWith('/revisions')) return json(route, [])
    return json(route, [])
  })
  await page.goto('/')
  await page.getByTestId('auth-username').fill('tester')
  await page.getByTestId('auth-password').fill('password123')
  await page.getByTestId('auth-submit').click()
  await page.getByTestId('nav-config').click()
  return requested
}

test('Business Workflows and Agent Skills are mutually exclusive peer entries', async ({ page }) => {
  const requested = await signIn(page)
  await expect(page.getByText('expense-review')).toBeVisible()
  await expect(page.getByText('invoice-skill')).toHaveCount(0)
  await expect(page.getByRole('button', { name: '新增業務流程' })).toBeVisible()
  await expect(page.getByRole('button', { name: '⬆ 上傳 Agent Skill 套件', exact: true })).toHaveCount(0)

  await page.getByRole('button', { name: 'Agent Skills' }).click()
  await expect(page.getByText('invoice-skill')).toBeVisible()
  await expect(page.getByText('expense-review')).toHaveCount(0)
  await expect(page.getByRole('button', { name: '⬆ 上傳 Agent Skill 套件', exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: '新增業務流程' })).toHaveCount(0)
  expect(requested).toContain('/api/business-workflows')
  expect(requested).toContain('/api/skills')
})

test('Agent Skill retains shared run and revision flows after IA split', async ({ page }) => {
  await signIn(page)
  await page.getByRole('button', { name: 'Agent Skills' }).click()
  const row = page.getByRole('row').filter({ hasText: 'invoice-skill' })
  await row.getByRole('button', { name: '版本' }).click()
  await expect(page.getByText('尚無 revision。')).toBeVisible()
  await page.getByRole('button', { name: '返回清單' }).click()
  await row.getByRole('button', { name: '試跑' }).click()
  const runButton = page.getByRole('button', { name: '執行', exact: true })
  await expect(runButton).toBeVisible()

  // 統一 invoke 面(agentic 這一案):按下執行真的打 /api/skills/{name}/invoke,結果渲染出來。
  let invokeRequest: { path: string; body: Record<string, unknown> } | null = null
  await page.route('**/api/skills/invoice-skill/invoke', async (route) => {
    invokeRequest = {
      path: new URL(route.request().url()).pathname,
      body: JSON.parse(route.request().postData() ?? '{}'),
    }
    await json(route, { skill: 'invoice-skill', output: { answer: 'invoice run result' } })
  })
  await runButton.click()
  await expect.poll(() => invokeRequest).not.toBeNull()
  expect(invokeRequest!.path).toBe('/api/skills/invoice-skill/invoke')
  await expect(page.getByText('invoice run result', { exact: true })).toBeVisible()
})

test('a single-revision history renders the row but offers no restore target', async ({ page }) => {
  await signIn(page)
  // 邊界：0 筆走「尚無 revision。」、2 筆才有回溯對象；恰好 1 筆是唯讀當前版，兩者皆非。
  await page.route('**/api/skills/invoice-skill/revisions', (route) => json(route, [
    { revision: 1, definition: 'only', definition_sha256: '111111111111', created_by: 'tester', created_at: '2026-07-30T00:00:00Z', kind: 'agentic', has_package: true },
  ]))
  await page.getByRole('button', { name: 'Agent Skills' }).click()
  await page.getByRole('row').filter({ hasText: 'invoice-skill' }).getByRole('button', { name: '版本' }).click()
  await expect(page.locator('.rev')).toHaveCount(1)
  await expect(page.getByText('尚無 revision。')).toHaveCount(0)
  await expect(page.getByRole('button', { name: '回溯此版' })).toHaveCount(0)
})

test('an agentic revision without a stored package keeps its restore button disabled', async ({ page }) => {
  await signIn(page)
  // agentic 的 package bytes 只留在 server：舊版沒保存 package 就不可回溯（flow 則永遠可回溯）。
  await page.route('**/api/skills/invoice-skill/revisions', (route) => json(route, [
    { revision: 3, definition: 'current', definition_sha256: '333333333333', created_by: 'tester', created_at: '2026-07-30T00:00:00Z', kind: 'agentic', has_package: true },
    { revision: 2, definition: 'old', definition_sha256: '222222222222', created_by: 'tester', created_at: '2026-07-29T00:00:00Z', kind: 'agentic', has_package: false },
  ]))
  await page.getByRole('button', { name: 'Agent Skills' }).click()
  await page.getByRole('row').filter({ hasText: 'invoice-skill' }).getByRole('button', { name: '版本' }).click()
  await expect(page.getByText('此舊版未保存套件，無法回溯')).toBeVisible()
  await expect(page.getByRole('button', { name: '回溯此版' })).toBeDisabled()
})

test('built-in Agent Skill opens a read-only catalog view and returns without duplicate requests', async ({ page }) => {
  const requested = await signIn(page)
  await page.getByRole('button', { name: 'Agent Skills' }).click()
  const catalogBeforeView = requested.filter((path) => path === '/api/skills/catalog').length
  const row = page.getByRole('row').filter({ hasText: 'builtin-agent-helper' })
  await expect(row).toBeVisible()
  await row.getByRole('button').first().click()
  const view = page.getByRole('region', { name: '內建 Agent Skill builtin-agent-helper' })
  await expect(view).toBeVisible()
  await expect(view.getByText('內建 Agent Skill 為唯讀', { exact: false })).toBeVisible()
  await expect(view.locator('textarea')).toHaveValue(/name: builtin-agent-helper/)
  await expect(view.locator('textarea')).toHaveAttribute('readonly', '')
  await expect(view.getByRole('button', { name: /edit|import|delete/i })).toHaveCount(0)
  await expect.poll(() => requested.filter((path) => path === '/api/skills/catalog').length).toBe(catalogBeforeView + 1)
  await view.getByRole('button', { name: '返回 Agent Skills' }).click()
  await expect(row).toBeVisible()
})

test('restoring a Business Workflow reloads detail from its own API surface', async ({ page }) => {
  const requested = await signIn(page, true)
  await page.getByRole('button', { name: '版本' }).click()
  await page.getByRole('button', { name: '回溯此版' }).click()
  await page.getByRole('button', { name: '回溯', exact: true }).click()
  await expect.poll(() => requested.includes('/api/business-workflows/expense-review')).toBe(true)
  expect(requested).not.toContain('/api/skills/expense-review')
})

async function signInCrossKind(page: Page, initialKind: 'flow' | 'agentic') {
  const requested: string[] = []
  let currentKind = initialKind
  let revision = 2
  const name = 'switchable-artifact'
  const row = () => ({
    name, description: 'Cross-kind history', required_role: 'USER', current_revision: revision,
    updated_at: '2026-07-30T00:00:00Z', enabled: true, kind: currentKind,
    simpleForm: { templateId: 'template-stats', form: { name } },
  })
  await page.route('**/api/**', async (route) => {
    const path = new URL(route.request().url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    requested.push(path)
    if (path === '/api/auth/login') return json(route, {
      token: 'token', username: 'tester', role: 'ADMIN', tenantCode: 'demo', capabilities: [],
    })
    if (path === '/api/features') return json(route, {})
    if (path === '/api/business-workflows') return json(route, currentKind === 'flow' ? [row()] : [])
    if (path === '/api/skills') return json(route, currentKind === 'agentic' ? [row()] : [])
    if (path === '/api/skills/catalog') return json(route, [
      { ...row(), source: 'custom', revision, bindable: true },
    ])
    if (path === `/api/skills/${name}/revisions`) return json(route, [
      { revision: 2, definition: 'current', definition_sha256: '222222222222', created_by: 'tester', created_at: '2026-07-30T00:00:00Z', kind: initialKind, has_package: true },
      { revision: 1, definition: 'old', definition_sha256: '111111111111', created_by: 'tester', created_at: '2026-07-29T00:00:00Z', kind: initialKind === 'flow' ? 'agentic' : 'flow', has_package: true },
    ])
    if (path === `/api/skills/${name}/revisions/1/restore`) {
      currentKind = initialKind === 'flow' ? 'agentic' : 'flow'
      revision = 3
      return json(route, { ...row(), definition: 'restored' })
    }
    if (path === `/api/skills/${name}`) return json(route, { ...row(), definition: 'restored' })
    if (path === `/api/business-workflows/${name}`) return json(route, { ...row(), definition: 'restored' })
    return json(route, [])
  })
  await page.goto('/')
  await page.getByTestId('auth-username').fill('tester')
  await page.getByTestId('auth-password').fill('password123')
  await page.getByTestId('auth-submit').click()
  await page.getByTestId('nav-config').click()
  if (initialKind === 'agentic') await page.getByRole('button', { name: 'Agent Skills' }).click()
  return requested
}

for (const initialKind of ['flow', 'agentic'] as const) {
  test(`${initialKind} restore to the other kind exits the current entry safely`, async ({ page }) => {
    const requested = await signInCrossKind(page, initialKind)
    if (initialKind === 'agentic') {
      await expect(page.getByRole('button', { name: '簡單編輯' })).toHaveCount(0)
    }
    await page.getByRole('button', { name: '版本' }).click()
    await page.getByRole('button', { name: '回溯此版' }).click()
    await page.getByRole('button', { name: '回溯', exact: true }).click()
    await expect(page.getByText(initialKind === 'flow'
      ? '已回溯為 Agent Skill，請切換到「Agent Skills」分頁繼續。'
      : '已回溯為業務流程，請切換到「業務流程」分頁繼續。')).toBeVisible()
    await expect(page.getByText('switchable-artifact')).toHaveCount(0)
    expect(requested).toContain(initialKind === 'flow'
      ? '/api/skills/switchable-artifact'
      : '/api/business-workflows/switchable-artifact')
  })
}

test('a restore whose snapshot never synchronizes gives up after three attempts', async ({ page }) => {
  let detailGets = 0
  const flow = {
    name: 'expense-review', description: 'Flow only', required_role: 'USER', current_revision: 2,
    updated_at: '2026-07-30T00:00:00Z', enabled: true, kind: 'flow',
  }
  await page.route('**/api/**', async (route) => {
    const path = new URL(route.request().url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') return json(route, {
      token: 'token', username: 'tester', role: 'ADMIN', tenantCode: 'demo', capabilities: [],
    })
    if (path === '/api/features') return json(route, {})
    if (path === '/api/business-workflows') return json(route, [flow])
    if (path === '/api/business-workflows/expense-review') {
      // restore 已回 r3,但 detail 與 catalog 都還停在 r2 —— 每次重試都取到不同步的快照。
      detailGets += 1
      return json(route, { ...flow, definition: 'name: expense-review' })
    }
    if (path === '/api/skills/catalog') return json(route, [{ ...flow, source: 'custom', revision: 2, bindable: true }])
    if (path === '/api/skills/expense-review/revisions') return json(route, [
      { revision: 2, definition: 'name: expense-review', definition_sha256: '222222222222', created_by: 'tester', created_at: '2026-07-30T00:00:00Z', kind: 'flow' },
      { revision: 1, definition: 'name: expense-review', definition_sha256: '111111111111', created_by: 'tester', created_at: '2026-07-29T00:00:00Z', kind: 'flow' },
    ])
    if (path === '/api/skills/expense-review/revisions/1/restore') {
      return json(route, { ...flow, current_revision: 3, definition: 'name: expense-review' })
    }
    return json(route, [])
  })
  await page.goto('/')
  await page.getByTestId('auth-username').fill('tester')
  await page.getByTestId('auth-password').fill('password123')
  await page.getByTestId('auth-submit').click()
  await page.getByTestId('nav-config').click()
  await page.getByRole('row').filter({ hasText: 'expense-review' }).getByRole('button', { name: '版本' }).click()
  await page.getByRole('button', { name: '回溯此版' }).click()
  await page.getByRole('button', { name: '回溯', exact: true }).click()
  await expect(page.getByText('能力已回溯，但最新版本資料尚未同步，請返回清單後重新開啟。')).toBeVisible()
  expect(detailGets).toBe(3)
  await expect(page.getByRole('button', { name: '新增業務流程' })).toBeVisible()
})

async function openSimpleCreate(page: Page, catalogHandler: (route: Route) => Promise<void>) {
  await page.route('**/api/**', async (route) => {
    const path = new URL(route.request().url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') return json(route, {
      token: 'token', username: 'tester', role: 'ADMIN', tenantCode: 'demo', capabilities: [],
    })
    if (path === '/api/features') return json(route, {})
    if (path === '/api/business-workflows') return json(route, [])
    if (path === '/api/skills/catalog') return catalogHandler(route)
    if (path === '/api/business-workflows/validate') return json(route, { valid: true, errors: [] })
    if (path === '/api/skills/simple-created/invoke') return json(route, { skill: 'simple-created', output: {} })
    return json(route, {})
  })
  await page.goto('/')
  await page.getByTestId('auth-username').fill('tester')
  await page.getByTestId('auth-password').fill('password123')
  await page.getByTestId('auth-submit').click()
  await page.getByTestId('nav-config').click()
  await page.getByRole('button', { name: '新增業務流程' }).click()
}

const templateEntry = (name: string, marker: string) => ({
  name,
  description: marker,
  required_role: 'USER',
  source: 'builtin',
  revision: null,
  bindable: false,
  kind: 'flow',
  definition: `name: template # __SLOT_name__\ndescription: ${marker}\nflow: []`,
  input_schema: {},
})

test('simple save keeps the editor mounted and enables its embedded run panel', async ({ page }) => {
  await openSimpleCreate(page, (route) => json(route, [templateEntry('template-retrieval', 'retrieval-marker')]))
  let createRequest: { method: string; body: Record<string, unknown> } | null = null
  await page.route('**/api/business-workflows', async (route) => {
    const method = route.request().method()
    if (method === 'GET') return json(route, [])
    const body = JSON.parse(route.request().postData() ?? '{}') as Record<string, unknown>
    createRequest = { method, body }
    await route.fulfill({
      status: 201,
      contentType: 'application/json',
      body: JSON.stringify({
        name: 'simple-created', kind: 'flow', current_revision: 1, definition: body.definition,
      }),
    })
  })
  let invokeRequest: { path: string; body: Record<string, unknown> } | null = null
  await page.route('**/api/skills/simple-created/invoke', async (route) => {
    invokeRequest = {
      path: new URL(route.request().url()).pathname,
      body: JSON.parse(route.request().postData() ?? '{}'),
    }
    await json(route, { skill: 'simple-created', output: { answer: 'flow run result' } })
  })
  await page.getByText('知識問答', { exact: true }).click()
  await page.locator('.simple-skill input.input').first().fill('simple-created')
  await page.getByRole('button', { name: '儲存', exact: true }).click()
  await expect.poll(() => createRequest).not.toBeNull()
  expect(createRequest!.method).toBe('POST')
  expect(createRequest!.body.definition).toContain('simple-created')
  expect(createRequest!.body.simpleForm).toEqual(expect.objectContaining({
    templateId: 'template-retrieval',
    form: expect.objectContaining({ name: 'simple-created' }),
  }))
  await expect(page.getByRole('heading', { name: '⑥ 試一下' })).toBeVisible()
  const runButton = page.getByRole('button', { name: '執行', exact: true })
  await expect(runButton).toBeVisible()

  // 統一 invoke 面(flow 這一案):按下執行真的打 /api/skills/{name}/invoke,結果渲染出來。
  // 長表單捲到底時,執行鈕的滑鼠命中點會落在固定定位的 CopilotKit 聊天泡泡下方(純視覺重疊、
  // 與本測試無關);改用鍵盤觸發(focus + Enter)避開座標命中測試,同時仍是真實的按鈕啟用路徑。
  await runButton.press('Enter')
  await expect.poll(() => invokeRequest).not.toBeNull()
  expect(invokeRequest!.path).toBe('/api/skills/simple-created/invoke')
  await expect(page.getByText('flow run result', { exact: true })).toBeVisible()
})

test('creating a Business Workflow returns to the list with the new row visible', async ({ page }) => {
  await openSimpleCreate(page, (route) => json(route, [templateEntry('template-retrieval', 'retrieval-marker')]))
  let created = false
  await page.route('**/api/business-workflows', async (route) => {
    const method = route.request().method()
    if (method === 'POST') {
      created = true
      const body = JSON.parse(route.request().postData() ?? '{}') as Record<string, unknown>
      return route.fulfill({
        status: 201,
        contentType: 'application/json',
        body: JSON.stringify({
          name: 'newly-created-flow', kind: 'flow', current_revision: 1, definition: body.definition,
        }),
      })
    }
    return json(route, created ? [{
      name: 'newly-created-flow', description: 'retrieval-marker', required_role: 'USER', current_revision: 1,
      updated_at: '2026-07-30T00:00:00Z', enabled: true, kind: 'flow',
    }] : [])
  })
  await page.getByText('知識問答', { exact: true }).click()
  await page.locator('.simple-skill input.input').first().fill('newly-created-flow')
  await page.getByRole('button', { name: '儲存', exact: true }).click()
  await expect(page.getByRole('button', { name: '執行', exact: true })).toBeVisible()
  await page.getByRole('button', { name: '關閉' }).click()
  await expect(page.getByText('newly-created-flow')).toBeVisible()
})

test('out-of-order template skeleton responses only apply the current template', async ({ page }) => {
  let skeletonRequests = 0
  let releaseFirst!: () => void
  const firstBlocked = new Promise<void>((resolve) => { releaseFirst = resolve })
  let savedDefinition = ''
  await openSimpleCreate(page, async (route) => {
    skeletonRequests += 1
    if (skeletonRequests === 1) return json(route, []) // list load before opening the editor
    if (skeletonRequests === 2) {
      await firstBlocked
      return json(route, [templateEntry('template-retrieval', 'retrieval-marker')])
    }
    return json(route, [templateEntry('template-compare', 'compare-marker')])
  })
  await page.route('**/api/business-workflows', async (route) => {
    if (route.request().method() === 'POST') {
      savedDefinition = JSON.parse(route.request().postData() ?? '{}').definition ?? ''
    }
    await json(route, {})
  })
  await page.getByText('知識問答', { exact: true }).click()
  await page.getByText('比對排序', { exact: true }).click()
  await expect(page.locator('.simple-skill input.input').first()).toBeEnabled()
  releaseFirst()
  await page.locator('.simple-skill input.input').first().fill('simple-created')
  const save = page.getByRole('button', { name: '儲存', exact: true })
  await expect(save).toBeEnabled()
  await save.click()
  await expect.poll(() => savedDefinition).toContain('compare-marker')
  expect(savedDefinition).not.toContain('retrieval-marker')
})

test('blocking server validation refuses a simple save and keeps the run panel locked', async ({ page }) => {
  await openSimpleCreate(page, (route) => json(route, [templateEntry('template-retrieval', 'retrieval-marker')]))
  await page.route('**/api/business-workflows/validate', (route) => json(route, {
    valid: false, errors: [{ code: 'unknown_node', message: 'node kb_query@9 not found' }],
  }))
  let createRequests = 0
  await page.route('**/api/business-workflows', async (route) => {
    if (route.request().method() === 'POST') createRequests += 1
    await json(route, [])
  })
  await page.getByText('知識問答', { exact: true }).click()
  await page.locator('.simple-skill input.input').first().fill('blocked-flow')
  await page.getByRole('button', { name: '儲存', exact: true }).click()
  await expect(page.locator('.simple-skill__errors')).toContainText('引用了不存在的節點或版本')
  expect(createRequests).toBe(0)
  // 未存檔 → ⑥ 試一下維持停用（savedName 仍為 null）。
  await expect(page.getByText('先儲存後才能試跑（試跑會執行已存在的 Skill）。')).toBeVisible()
})

test('advanced handoff carries the composed definition into create mode and surfaces its 409', async ({ page }) => {
  await openSimpleCreate(page, (route) => json(route, [templateEntry('template-retrieval', 'retrieval-marker')]))
  // 進階編輯器左欄的節點目錄要拿到陣列;openSimpleCreate 的 catch-all 回物件會讓它整頁崩掉。
  await page.route('**/api/nodes', (route) => json(route, []))
  let createRequests = 0
  await page.route('**/api/business-workflows', async (route) => {
    if (route.request().method() !== 'POST') return json(route, [])
    createRequests += 1
    await route.fulfill({
      status: 409,
      contentType: 'application/json',
      body: JSON.stringify({ timestamp: '2026-07-30T00:00:00Z', status: 409, message: 'name taken', fieldErrors: {} }),
    })
  })
  await page.getByText('知識問答', { exact: true }).click()
  await page.locator('.simple-skill input.input').first().fill('handed-over')
  await page.getByRole('button', { name: '進階編輯' }).click()
  await expect(page.getByRole('heading', { name: '新增業務流程（進階）' })).toBeVisible()
  await expect(page.getByLabel('業務流程 YAML 定義')).toHaveValue(/name: "handed-over"/)
  // 自動驗證（debounce）先落地,才不會在 canSave 短暫關閉的空窗按到儲存。
  await expect(page.getByText('✅ 通過所有靜態驗證。')).toBeVisible()
  await page.locator('.skill-editor__actions .btn--primary').click()
  await expect(page.locator('.skill-editor').getByText('名稱已存在：name taken')).toBeVisible()
  expect(createRequests).toBe(1)
  await expect(page.locator('.skill-editor')).toBeVisible()
})

async function signInFailureWorkspace(page: Page, kind: 'flow' | 'agentic') {
  const operationRequests: string[] = []
  const row = {
    name: kind === 'flow' ? 'failing-flow' : 'failing-skill',
    description: 'failure coverage', required_role: 'USER', current_revision: 1,
    updated_at: '2026-07-30T00:00:00Z', enabled: true, kind,
  }
  await page.route('**/api/**', async (route) => {
    const path = new URL(route.request().url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') return json(route, {
      token: 'token', username: 'tester', role: 'ADMIN', tenantCode: 'demo', capabilities: [],
    })
    if (path === '/api/features') return json(route, {})
    if (path.endsWith('/export') || path === '/api/skills/import' || route.request().method() === 'DELETE') {
      operationRequests.push(`${route.request().method()} ${path}`)
      return route.fulfill({
        status: 500,
        contentType: 'application/json',
        body: JSON.stringify({ timestamp: '2026-07-30T00:00:00Z', status: 500, message: `${kind}-operation-failed`, fieldErrors: {} }),
      })
    }
    if (path === '/api/business-workflows') return json(route, kind === 'flow' ? [row] : [])
    if (path === '/api/skills') return json(route, kind === 'agentic' ? [row] : [])
    if (path === '/api/skills/catalog') return json(route, [{ ...row, source: 'custom', revision: 1, bindable: true }])
    return json(route, {})
  })
  await page.goto('/')
  await page.getByTestId('auth-username').fill('tester')
  await page.getByTestId('auth-password').fill('password123')
  await page.getByTestId('auth-submit').click()
  await page.getByTestId('nav-config').click()
  if (kind === 'agentic') await page.getByRole('button', { name: 'Agent Skills' }).click()
  return { name: row.name, operationRequests }
}

for (const kind of ['flow', 'agentic'] as const) {
  test(`${kind} Home surfaces export and delete failures without dropping its row`, async ({ page }) => {
    const { name, operationRequests } = await signInFailureWorkspace(page, kind)
    await page.getByRole('button', { name: '下載' }).click()
    await expect(page.getByText(`${kind}-operation-failed`)).toBeVisible()
    expect(operationRequests).toContain(`GET /api/${kind === 'flow' ? 'business-workflows/failing-flow' : 'skills/failing-skill'}/export`)
    const errorCount = await page.getByText(`${kind}-operation-failed`).count()
    await page.getByRole('button', { name: '停用' }).click()
    await page.getByRole('button', { name: '停用', exact: true }).last().click()
    await expect.poll(() => operationRequests).toContain(`DELETE /api/${kind === 'flow' ? 'business-workflows/failing-flow' : 'skills/failing-skill'}`)
    await expect(page.getByText(`${kind}-operation-failed`)).toHaveCount(errorCount + 1)
    await expect(page.getByText(name)).toBeVisible()
  })
}

test('Agent Skill Home surfaces import failure and keeps the upload entry available', async ({ page }) => {
  await signInFailureWorkspace(page, 'agentic')
  await page.getByLabel('上傳 Agent Skill 套件').setInputFiles({
    name: 'broken.zip', mimeType: 'application/zip', buffer: Buffer.from('broken'),
  })
  await expect(page.getByText('agentic-operation-failed')).toBeVisible()
  await expect(page.getByRole('button', { name: '⬆ 上傳 Agent Skill 套件', exact: true })).toBeVisible()
})

test('uploading an Agent Skill package returns to the list with the new row visible', async ({ page }) => {
  let imported = false
  await page.route('**/api/**', async (route) => {
    const path = new URL(route.request().url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') return json(route, {
      token: 'token', username: 'tester', role: 'ADMIN', tenantCode: 'demo', capabilities: [],
    })
    if (path === '/api/features') return json(route, {})
    if (path === '/api/business-workflows') return json(route, [])
    if (path === '/api/skills') return json(route, imported ? [{
      name: 'uploaded-skill', description: 'Uploaded package', required_role: 'USER', current_revision: 1,
      updated_at: '2026-07-30T00:00:00Z', enabled: true, kind: 'agentic',
    }] : [])
    if (path === '/api/skills/catalog') return json(route, [])
    if (path === '/api/skills/import') {
      imported = true
      return json(route, {
        name: 'uploaded-skill', description: 'Uploaded package', required_role: 'USER', current_revision: 1,
        updated_at: '2026-07-30T00:00:00Z', enabled: true, kind: 'agentic', definition: '',
      })
    }
    return json(route, {})
  })
  await page.goto('/')
  await page.getByTestId('auth-username').fill('tester')
  await page.getByTestId('auth-password').fill('password123')
  await page.getByTestId('auth-submit').click()
  await page.getByTestId('nav-config').click()
  await page.getByRole('button', { name: 'Agent Skills' }).click()
  await expect(page.getByText('尚無 Agent Skill。')).toBeVisible()
  await page.getByLabel('上傳 Agent Skill 套件').setInputFiles({
    name: 'uploaded-skill.zip', mimeType: 'application/zip', buffer: Buffer.from('zip-bytes'),
  })
  await expect(page.getByText('uploaded-skill')).toBeVisible()
  await expect(page.getByRole('button', { name: '⬆ 上傳 Agent Skill 套件', exact: true })).toBeVisible()
})

test('advanced workflow save returns to the list and permits an immediate second edit', async ({ page }) => {
  await signIn(page)
  let detailGets = 0
  await page.route('**/api/business-workflows/expense-review', async (route) => {
    if (route.request().method() === 'PUT') return json(route, {})
    detailGets += 1
    return json(route, {
      name: 'expense-review', description: 'Flow only', required_role: 'USER', current_revision: 2,
      updated_at: '2026-07-30T00:00:00Z', enabled: true, kind: 'flow', definition: 'name: expense-review\nflow: []',
    })
  })
  const row = page.getByRole('row').filter({ hasText: 'expense-review' })
  await row.getByRole('button').first().click()
  await expect(page.locator('.skill-editor')).toBeVisible()
  await page.locator('.skill-editor__actions .btn--primary').click()
  await expect(page.locator('.skill-editor')).toHaveCount(0)
  await expect(row).toBeVisible()
  await row.getByRole('button').first().click()
  await expect(page.locator('.skill-editor')).toBeVisible()
  expect(detailGets).toBe(3) // open + post-save refresh + second open
})

test('existing Agent Skill name is immutable and save updates only the same artifact', async ({ page }) => {
  let importRequests = 0
  const packageBytes = zipSync({
    'SKILL.md': strToU8('---\nname: invoice-skill\ndescription: Package only\n---\nBody'),
  })
  await page.route('**/api/**', async (route) => {
    const path = new URL(route.request().url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') return json(route, {
      token: 'token', username: 'tester', role: 'ADMIN', tenantCode: 'demo', capabilities: [],
    })
    if (path === '/api/features') return json(route, {})
    if (path === '/api/business-workflows') return json(route, [])
    if (path === '/api/skills') return json(route, [{
      name: 'invoice-skill', description: 'Package only', required_role: 'USER', current_revision: 4,
      updated_at: '2026-07-30T00:00:00Z', enabled: true, kind: 'agentic',
    }])
    if (path === '/api/skills/catalog') return json(route, [])
    if (path === '/api/skills/invoice-skill') return json(route, {
      name: 'invoice-skill', description: 'Package only', required_role: 'USER', current_revision: 4,
      updated_at: '2026-07-30T00:00:00Z', enabled: true, kind: 'agentic', definition: '',
    })
    if (path === '/api/skills/invoice-skill/export') {
      return route.fulfill({ status: 200, contentType: 'application/zip', body: Buffer.from(packageBytes) })
    }
    if (path === '/api/skills/import') {
      importRequests += 1
      return json(route, {
        name: 'invoice-skill', description: 'Package only', required_role: 'USER', current_revision: 5,
        updated_at: '2026-07-30T00:00:00Z', enabled: true, kind: 'agentic', definition: '',
      })
    }
    return json(route, {})
  })
  await page.goto('/')
  await page.getByTestId('auth-username').fill('tester')
  await page.getByTestId('auth-password').fill('password123')
  await page.getByTestId('auth-submit').click()
  await page.getByTestId('nav-config').click()
  await page.getByRole('button', { name: 'Agent Skills' }).click()
  await page.getByRole('row').filter({ hasText: 'invoice-skill' }).getByRole('button').first().click()
  await expect(page.locator('.agent-pkg')).toBeVisible()
  await expect(page.locator('.agent-pkg').getByRole('note')).toContainText('不可變更')
  const frontmatter = page.locator('#agent-fm')
  await frontmatter.fill('name: renamed-skill\ndescription: Package only')
  await page.locator('.skill-editor__actions .btn--primary').click()
  await expect(page.getByRole('main').getByRole('alert')).toContainText('名稱不可變更')
  expect(importRequests).toBe(0)
  await frontmatter.fill('name: invoice-skill\ndescription: Package only')
  await page.locator('.skill-editor__actions .btn--primary').click()
  await expect(page.locator('.skill-editor')).toHaveCount(0)
  expect(importRequests).toBe(1)
  await expect(page.getByRole('row').filter({ hasText: 'invoice-skill' })).toHaveCount(1)
  await expect(page.getByText('renamed-skill')).toHaveCount(0)
})
