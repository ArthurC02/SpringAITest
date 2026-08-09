import { expect, test, type Route } from '@playwright/test'

// W5 裸 JSON/GUID 清零(規格 §5):所有新表單化欄位都是「catalog 端點正常 → 下拉,
// catalog 端點失敗(含功能旗標關閉導致的 404)→ 優雅退回手動輸入,不壞頁」。
// 這支測試專打 CatalogPicker 在三個實際呼叫點的 fail-open 行為與成功路徑,
// 逐欄位/逐檔的其餘表單邏輯（結構化編輯本身）已由 orchestratorEditor.ui.spec.ts、
// agentBuilder.ui.spec.ts、evaluationPanel.ui.spec.ts 覆蓋,這裡不重複。

async function json(route: Route, body: unknown, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
}

async function loginAdmin(page: import('@playwright/test').Page) {
  await page.goto('/')
  await page.getByTestId('auth-username').fill('admin')
  await page.getByTestId('auth-password').fill('password123')
  await page.getByTestId('auth-submit').click()
}

test('Rollout Orchestrator picker falls back to manual GUID input when the catalog 404s', async ({ page }) => {
  await page.route('**/api/**', async (route) => {
    const path = new URL(route.request().url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') {
      return json(route, {
        token: 'ops-404-token', username: 'admin', role: 'ADMIN', tenantCode: 'demo',
        capabilities: ['workflow.manage'],
      })
    }
    if (path === '/api/features') return json(route, { agentWriteToolsEnabled: true, workflowDesignerEnabled: false })
    // WORKFLOW_DESIGNER_ENABLED off ⇒ the orchestrator list proxy 404s, same as any other
    // gated route the frontend must never treat as fatal.
    if (path === '/api/admin/orchestrators') {
      return json(route, { timestamp: '2026-08-09T00:00:00Z', status: 404, message: 'not found', fieldErrors: {} }, 404)
    }
    return json(route, {})
  })

  await loginAdmin(page)
  await page.getByTestId('nav-operations').click()
  await expect(page.getByRole('heading', { name: '營運治理' })).toBeVisible()

  const orchId = page.locator('#ops-orch-id')
  // A <select> does not accept .fill(); this only succeeds if the fallback rendered an <input>.
  await orchId.fill('not-a-guid')
  await expect(page.getByText('已切換為手動輸入')).toBeVisible()
  await expect(page.getByText('Orchestrator ID 必須是合法的 GUID')).toBeVisible()

  await orchId.fill('3fa85f64-5717-4562-b3fc-2c963f66afa6')
  await expect(page.getByText('Orchestrator ID 必須是合法的 GUID')).toHaveCount(0)
})

test('Rollout Orchestrator picker renders a dropdown and auto-fills the published revision on selection', async ({ page }) => {
  const orchestratorId = '11111111-1111-4111-8111-111111111111'
  await page.route('**/api/**', async (route) => {
    const path = new URL(route.request().url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') {
      return json(route, {
        token: 'ops-ok-token', username: 'admin', role: 'ADMIN', tenantCode: 'demo',
        capabilities: ['workflow.manage'],
      })
    }
    if (path === '/api/features') return json(route, { agentWriteToolsEnabled: true, workflowDesignerEnabled: true })
    if (path === '/api/admin/orchestrators') {
      return json(route, [{
        id: orchestratorId, name: 'Research Team', description: 'Collaboration runtime',
        enabled: true, published_revision: 4, updated_at: '2026-08-09T00:00:00Z',
      }])
    }
    return json(route, {})
  })

  await loginAdmin(page)
  await page.getByTestId('nav-operations').click()

  const orchId = page.locator('#ops-orch-id')
  await expect(orchId).toHaveJSProperty('tagName', 'SELECT')
  await orchId.selectOption(orchestratorId)
  await expect(page.locator('#ops-orch-rev')).toHaveValue('4')
})

test('Eval candidate skill picker falls back to manual input when the skill catalog fails to load', async ({ page }) => {
  await page.route('**/api/**', async (route) => {
    const path = new URL(route.request().url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') {
      return json(route, {
        token: 'eval-skill-fail-token', username: 'admin', role: 'ADMIN', tenantCode: 'demo',
        capabilities: ['workflow.manage'],
      })
    }
    if (path === '/api/features') return json(route, { agentWriteToolsEnabled: true })
    if (path === '/api/skills/catalog') {
      return json(route, { timestamp: '2026-08-09T00:00:00Z', status: 500, message: 'catalog store unavailable', fieldErrors: {} }, 500)
    }
    return json(route, [])
  })

  await loginAdmin(page)
  await page.getByTestId('nav-operations').click()

  const candidateName = page.locator('#eval-candidate-name')
  await candidateName.fill('kb-query')
  await expect(candidateName).toHaveValue('kb-query')
  await expect(page.getByText('清單載入失敗,已切換為手動輸入。')).toBeVisible()
})

test('Eval candidate skill picker renders a dropdown once the skill catalog loads', async ({ page }) => {
  await page.route('**/api/**', async (route) => {
    const path = new URL(route.request().url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') {
      return json(route, {
        token: 'eval-skill-ok-token', username: 'admin', role: 'ADMIN', tenantCode: 'demo',
        capabilities: ['workflow.manage'],
      })
    }
    if (path === '/api/features') return json(route, { agentWriteToolsEnabled: true })
    if (path === '/api/skills/catalog') {
      return json(route, [
        { name: 'kb-query', description: 'RAG lookup', required_role: 'USER', source: 'builtin', revision: null },
      ])
    }
    return json(route, [])
  })

  await loginAdmin(page)
  await page.getByTestId('nav-operations').click()

  const candidateName = page.locator('#eval-candidate-name')
  await expect(candidateName).toHaveJSProperty('tagName', 'SELECT')
  await candidateName.selectOption('kb-query')
  await expect(candidateName).toHaveValue('kb-query')
})

test('Orchestrator create form Agent pickers fall back to manual input when the Agent catalog 404s, and creation still succeeds', async ({ page }) => {
  await page.route('**/api/**', async (route) => {
    const request = route.request()
    const path = new URL(request.url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') {
      return json(route, {
        token: 'orch-agent-404-token', username: 'admin', role: 'ADMIN', tenantCode: 'demo',
        capabilities: ['workflow.manage'],
      })
    }
    if (path === '/api/features') return json(route, { workflowDesignerEnabled: true })
    // AGENT_BUILDER_ENABLED off (independent of workflowDesignerEnabled) ⇒ /api/agents 404s.
    if (path === '/api/agents') {
      return json(route, { timestamp: '2026-08-09T00:00:00Z', status: 404, message: 'not found', fieldErrors: {} }, 404)
    }
    if (path === '/api/admin/orchestrators' && request.method() === 'GET') return json(route, [])
    if (path === '/api/admin/orchestrators' && request.method() === 'POST') {
      return json(route, {
        id: 'new-orch', name: 'New root', description: '', enabled: true, draft_version: 1,
        published_revision: null, updated_at: '2026-08-09T00:00:00Z',
        definition: { workflow: { id: 'w1', revision: 1 } },
      })
    }
    // AgentPlatformView's default-selected tab (workflows, since agentBuilderEnabled is off)
    // expects list-shaped responses from its own catalog fetches — match the array-default
    // convention every other UI spec's catch-all route already uses.
    return json(route, [])
  })

  await page.goto('/')
  await page.getByTestId('auth-username').fill('admin')
  await page.getByTestId('auth-password').fill('password123')
  await page.getByTestId('auth-submit').click()
  await page.getByTestId('nav-agentPlatform').click()
  await page.getByTestId('agent-platform-tab-orchestrators').click()
  await page.getByRole('button', { name: '＋ 建立' }).click()

  await page.getByLabel('名稱').fill('New root')
  await page.getByLabel('Root Workflow id').fill('w1')
  await page.getByLabel('Workflow revision').fill('1')
  await page.getByRole('button', { name: '＋ 加入 Worker' }).click()
  await page.getByLabel('Worker Agent id').fill('a1')
  await page.getByLabel('Worker revision').fill('2')
  await page.getByLabel('Verifier Agent id').fill('v1')
  await page.getByLabel('Verifier revision').fill('1')

  const create = page.getByRole('button', { name: '建立', exact: true })
  await expect(create).toBeEnabled()
  await create.click()
  await expect(page.locator('.toast--success')).toContainText('已建立 Orchestrator 草稿')
})
