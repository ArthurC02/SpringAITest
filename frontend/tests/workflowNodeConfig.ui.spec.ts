import { expect, test, type Page, type Route } from '@playwright/test'

// P1 Phase A': WorkflowDesigner 節點屬性表單化。configSchema 足以枚舉「鍵/型別/必填」時
// 渲染泛型表單；形狀超出窄子集(如巢狀 object)時退回既有 JSON textarea 逃生口——
// fail-open,而且 JSON 解析失敗要顯示行內錯誤,不再是完全靜默。
// 授權來源:`workflowDesigner/configSchema.ts` parseConfigSchema、`WorkflowDesigner.tsx` 節點屬性面板。
// Designer(React Flow + elkjs)在真實瀏覽器直接渲染,不 mock。

const definition = { schemaVersion: 1, kind: 'orchestrator', nodes: [], edges: [], governance: {} }

const workflow = {
  id: 'w1', name: 'Root harness', kind: 'orchestrator', enabled: true,
  draft_version: 1, published_revision: null, updated_at: '2026-08-03T00:00:00Z',
  definition, ui_metadata: { positions: {} },
}

const catalogNodes = [
  {
    type: 'bounded_agent_loop', version: '1.0', title: 'Bounded Agent Loop', kind: 'control',
    inputs: [{ id: 'in', dataType: 'Control', required: true }],
    outputs: [{ id: 'out', dataType: 'Control', required: false }],
    configSchema: {
      type: 'object', additionalProperties: false,
      required: ['maxIterations'],
      properties: { maxIterations: { type: 'integer', minimum: 1 } },
    },
    authoringCapability: 'workflow.manage', catalogVisibility: 'system-admin', runtimePolicy: 'agent.step',
    workflowKinds: ['orchestrator'], requiredStage: false,
  },
  {
    type: 'freeform_step', version: '1.0', title: 'Freeform Step', kind: 'control',
    inputs: [{ id: 'in', dataType: 'Control', required: true }],
    outputs: [{ id: 'out', dataType: 'Control', required: false }],
    // 巢狀 object 屬性超出可枚舉子集，必須 fail-open 回原始 JSON textarea。
    configSchema: { type: 'object', additionalProperties: false, properties: { nested: { type: 'object', properties: {} } } },
    authoringCapability: 'workflow.manage', catalogVisibility: 'system-admin', runtimePolicy: 'run.control',
    workflowKinds: ['orchestrator'], requiredStage: false,
  },
]

async function json(route: Route, body: unknown, headers: Record<string, string> = {}) {
  await route.fulfill({ status: 200, contentType: 'application/json', headers, body: JSON.stringify(body) })
}

async function openEditor(page: Page) {
  await page.route('**/api/**', async (route) => {
    const request = route.request()
    const path = new URL(request.url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') {
      return json(route, { token: 'workflow-token', username: 'tester', role: 'ADMIN', tenantCode: 'demo', capabilities: ['workflow.manage'] })
    }
    if (path === '/api/features') return json(route, { workflowDesignerEnabled: true })
    if (path === '/api/admin/workflows/catalog/nodes') return json(route, { catalogVersion: '1', nodes: catalogNodes })
    if (path === '/api/admin/workflows' && request.method() === 'GET') return json(route, [workflow])
    if (path === '/api/admin/workflows/w1') return json(route, workflow, { ETag: '"1"' })
    return json(route, [])
  })

  await page.goto('/')
  await page.getByTestId('auth-username').fill('tester')
  await page.getByTestId('auth-password').fill('password123')
  await page.getByTestId('auth-submit').click()
  await page.getByTestId('nav-agentPlatform').click()
  await page.getByTestId('agent-platform-tab-workflows').click()
  await page.getByRole('button', { name: '編輯' }).click()
  await expect(page.getByRole('heading', { name: 'Root harness', level: 2 })).toBeVisible()
}

test('resolvable configSchema renders a typed field; an unresolvable shape falls back to JSON with a visible parse error', async ({ page }) => {
  await openEditor(page)

  // 新增節點會自動選取它(WorkflowDesigner.addNode 的既有行為)，不需再另外點畫布。
  // 可枚舉形狀 → 泛型表單欄位，不是裸 JSON textarea。
  await page.getByRole('button', { name: '＋ Bounded Agent Loop' }).click()
  const maxIterations = page.locator('#workflow-node-config-maxIterations')
  await expect(maxIterations).toBeVisible()
  await expect(page.locator('#workflow-node-config')).toHaveCount(0)
  await maxIterations.fill('3')
  await expect(maxIterations).toHaveValue('3')

  // 巢狀 object 形狀 → 退回既有 JSON textarea 逃生口，不再渲染表單欄位。
  await page.getByRole('button', { name: '＋ Freeform Step' }).click()
  const jsonField = page.locator('#workflow-node-config')
  await expect(jsonField).toBeVisible()
  await expect(maxIterations).toHaveCount(0)

  // JSON 解析失敗要顯示行內錯誤（修掉現況的完全靜默），且不清空既有已生效內容。
  await jsonField.fill('{not json')
  await expect(page.getByRole('alert')).toContainText('JSON 格式錯誤')
})
