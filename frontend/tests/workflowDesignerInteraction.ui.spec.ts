import { expect, test, type Locator, type Page, type Route } from '@playwright/test'

// D4 WorkflowDesigner 的 n8n 式可操作性回歸：React Flow 狀態改為本地投影後，
// 拖曳、選取、鍵盤刪除、連線、inspector 刪除、palette 拖放都必須真的可用。
// 全部在真實瀏覽器操作真的 React Flow（不 mock 畫布），語意仍以 canDeleteNode / canConnect 為準。
// 授權來源：`workflowDesigner/WorkflowDesigner.tsx`、`catalog.ts` canDeleteNode、`connection.ts` canConnect。

const catalogNodes = [
  {
    type: 'optional_step', version: '1.0', title: 'Optional Step', kind: 'control',
    inputs: [{ id: 'in', dataType: 'Control', required: false }],
    outputs: [{ id: 'out', dataType: 'Control', required: false }],
    configSchema: { type: 'object', additionalProperties: false, properties: {} },
    authoringCapability: 'workflow.manage', catalogVisibility: 'system-admin', runtimePolicy: 'run.control',
    workflowKinds: ['orchestrator'], requiredStage: false,
  },
  {
    // requiredStage: true → canDeleteNode fail-closed，鍵盤與 inspector 都不得刪掉它。
    type: 'required_gate', version: '1.0', title: 'Required Gate', kind: 'control',
    inputs: [{ id: 'in', dataType: 'Control', required: true }],
    outputs: [{ id: 'out', dataType: 'Control', required: false }],
    configSchema: { type: 'object', additionalProperties: false, properties: {} },
    authoringCapability: 'workflow.manage', catalogVisibility: 'system-admin', runtimePolicy: 'run.control',
    workflowKinds: ['orchestrator'], requiredStage: true,
  },
]

function workflowWith(nodes: unknown[], positions: Record<string, { x: number; y: number }>) {
  return {
    id: 'w1', name: 'Root harness', kind: 'orchestrator', enabled: true,
    draft_version: 1, published_revision: null, updated_at: '2026-08-03T00:00:00Z',
    definition: { schemaVersion: 1, kind: 'orchestrator', nodes, edges: [], governance: {} },
    ui_metadata: { positions },
  }
}

const node = (id: string, type = 'optional_step') => ({ id, type, typeVersion: '1.0', config: {} })

async function json(route: Route, body: unknown, headers: Record<string, string> = {}) {
  await route.fulfill({ status: 200, contentType: 'application/json', headers, body: JSON.stringify(body) })
}

async function openEditor(page: Page, workflow: ReturnType<typeof workflowWith>) {
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

async function box(locator: Locator) {
  const rect = await locator.boundingBox()
  if (!rect) throw new Error('element has no bounding box')
  return rect
}

async function dragBy(page: Page, target: Locator, dx: number, dy: number) {
  const rect = await box(target)
  await page.mouse.move(rect.x + rect.width / 2, rect.y + rect.height / 2)
  await page.mouse.down()
  await page.mouse.move(rect.x + rect.width / 2 + dx, rect.y + rect.height / 2 + dy, { steps: 12 })
  await page.mouse.up()
}

test('palette click adds nodes and a mouse drag moves one without snapping back', async ({ page }) => {
  await openEditor(page, workflowWith([], {}))
  const nodes = page.locator('.react-flow__node')

  await page.getByRole('button', { name: '＋ Optional Step' }).click()
  await page.getByRole('button', { name: '＋ Optional Step' }).click()
  await expect(nodes).toHaveCount(2)

  const moved = nodes.nth(1)
  const before = await box(moved)
  await dragBy(page, moved, 160, 90)

  // 位置只在 onNodeDragStop 提交；提交後 props 回流重建投影，位置必須維持在放開的地方，不回彈。
  await expect.poll(async () => Math.round((await box(moved)).x - before.x)).toBeGreaterThan(120)
  await page.waitForTimeout(300)
  const after = await box(moved)
  expect(Math.round(after.x - before.x)).toBeGreaterThan(120)
  expect(Math.round(after.y - before.y)).toBeGreaterThan(60)
})

test('selecting a node highlights it and Backspace deletes only catalog-deletable nodes', async ({ page }) => {
  await openEditor(page, workflowWith([node('n1'), node('n2', 'required_gate')], { n1: { x: 40, y: 40 }, n2: { x: 320, y: 40 } }))
  const deletable = page.locator('.react-flow__node', { hasText: 'Optional Step' })
  const required = page.locator('.react-flow__node', { hasText: 'Required Gate' })

  await required.click()
  await expect(required).toHaveClass(/selected/)
  await page.keyboard.press('Backspace')
  // requiredStage 節點 fail-closed：不得從畫布消失（更不得先消失再回來）。
  await page.waitForTimeout(300)
  await expect(required).toHaveCount(1)

  await deletable.click()
  await expect(deletable).toHaveClass(/selected/)
  await page.keyboard.press('Backspace')
  await expect(deletable).toHaveCount(0)
  await expect(required).toHaveCount(1)
})

test('a mouse-drawn connection becomes an edge that can be selected and deleted', async ({ page }) => {
  await openEditor(page, workflowWith([node('n1'), node('n2')], { n1: { x: 40, y: 60 }, n2: { x: 340, y: 60 } }))
  const nodes = page.locator('.react-flow__node')
  const edges = page.locator('.react-flow__edge')

  const source = await box(nodes.nth(0).locator('.react-flow__handle.source'))
  const target = await box(nodes.nth(1).locator('.react-flow__handle.target'))
  await page.mouse.move(source.x + source.width / 2, source.y + source.height / 2)
  await page.mouse.down()
  await page.mouse.move(target.x + target.width / 2, target.y + target.height / 2, { steps: 12 })
  await page.mouse.up()
  await expect(edges).toHaveCount(1)

  await edges.click()
  await expect(edges).toHaveClass(/selected/)
  await page.keyboard.press('Backspace')
  await expect(edges).toHaveCount(0)
  await expect(nodes).toHaveCount(2)
})

test('the inspector delete button removes a node and stays disabled for required nodes', async ({ page }) => {
  await openEditor(page, workflowWith([node('n1'), node('n2', 'required_gate')], { n1: { x: 40, y: 40 }, n2: { x: 320, y: 40 } }))
  const remove = page.getByRole('button', { name: '刪除節點' })
  const deletable = page.locator('.react-flow__node', { hasText: 'Optional Step' })
  const required = page.locator('.react-flow__node', { hasText: 'Required Gate' })

  await required.click()
  await expect(remove).toBeDisabled()
  await expect(remove).toHaveAttribute('title', '必要節點不可刪除')

  await deletable.click()
  await expect(remove).toBeEnabled()
  await remove.click()
  await expect(deletable).toHaveCount(0)
  await expect(required).toHaveCount(1)
})

test('dragging a palette entry onto the canvas creates the node at the drop point', async ({ page }) => {
  await openEditor(page, workflowWith([node('n1')], { n1: { x: 20, y: 20 } }))
  const nodes = page.locator('.react-flow__node')
  await expect(nodes).toHaveCount(1)

  // HTML5 DnD 沒有可靠的 page.mouse 模擬路徑，改用共享 DataTransfer 合成事件（Chromium）。
  const canvas = page.locator('.workflow-designer__canvas')
  const rect = await box(canvas)
  const dropX = Math.round(rect.x + rect.width * 0.6)
  const dropY = Math.round(rect.y + rect.height * 0.6)
  const dataTransfer = await page.evaluateHandle(() => new DataTransfer())
  await page.getByRole('button', { name: '＋ Optional Step' }).dispatchEvent('dragstart', { dataTransfer })
  await canvas.dispatchEvent('dragover', { dataTransfer, clientX: dropX, clientY: dropY })
  await canvas.dispatchEvent('drop', { dataTransfer, clientX: dropX, clientY: dropY })

  await expect(nodes).toHaveCount(2)
  // 落點 = 節點左上角（screenToFlowPosition 的反投影），不是舊的對角線 cascade。
  const dropped = await box(nodes.nth(1))
  expect(Math.abs(dropped.x - dropX)).toBeLessThan(12)
  expect(Math.abs(dropped.y - dropY)).toBeLessThan(12)
})
