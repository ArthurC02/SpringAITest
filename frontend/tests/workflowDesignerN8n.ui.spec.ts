import { expect, test, type Locator, type Page, type Route } from '@playwright/test'

// D4 WorkflowDesigner 的 n8n 操作體感回歸：undo/redo、焦點守衛、複製貼上、框選、拉線到空白處開
// picker、edge hover 工具列、右鍵選單、Tab picker、全選刪除的 fail-closed、鍵盤移動要真的提交、
// 以及唯讀情境全部封死。全部在真實瀏覽器操作真的 React Flow（不 mock 畫布）。
// 授權來源：`workflowDesigner/` 全部檔案；語意判準仍是 canDeleteNode / canConnect。

const catalogNodes = [
  {
    type: 'optional_step', version: '1.0', title: 'Optional Step', kind: 'control',
    inputs: [{ id: 'in', dataType: 'Control' }],
    outputs: [{ id: 'out', dataType: 'Control' }],
    configSchema: { type: 'object', additionalProperties: false, properties: { label: { type: 'string' } } },
    authoringCapability: 'workflow.manage', catalogVisibility: 'system-admin', runtimePolicy: 'run.control',
    workflowKinds: ['orchestrator'], requiredStage: false,
  },
  {
    // requiredStage: true → canDeleteNode fail-closed，任何入口都不得刪掉它。
    type: 'required_gate', version: '1.0', title: 'Required Gate', kind: 'control',
    inputs: [{ id: 'in', dataType: 'Control' }],
    outputs: [{ id: 'out', dataType: 'Control' }],
    configSchema: { type: 'object', additionalProperties: false, properties: {} },
    authoringCapability: 'workflow.manage', catalogVisibility: 'system-admin', runtimePolicy: 'run.control',
    workflowKinds: ['orchestrator'], requiredStage: true,
  },
  {
    // dataType 完全不同 → 不可能接上 Control 埠，picker 相容性過濾必須把它濾掉。
    type: 'data_step', version: '1.0', title: 'Data Step', kind: 'data',
    inputs: [{ id: 'in', dataType: 'Payload' }],
    outputs: [{ id: 'out', dataType: 'Payload' }],
    configSchema: { type: 'object', additionalProperties: false, properties: {} },
    authoringCapability: 'workflow.manage', catalogVisibility: 'system-admin', runtimePolicy: 'run.control',
    workflowKinds: ['orchestrator'], requiredStage: false,
  },
]

const node = (id: string, type = 'optional_step') => ({ id, type, typeVersion: '1.0', config: {} })
const edge = (id: string, source: string, target: string) =>
  ({ id, source: { nodeId: source, port: 'out' }, target: { nodeId: target, port: 'in' } })

function workflowWith(
  nodes: unknown[], positions: Record<string, { x: number; y: number }>, edges: unknown[] = [],
) {
  return {
    id: 'w1', name: 'Root harness', kind: 'orchestrator', enabled: true,
    draft_version: 1, published_revision: null, updated_at: '2026-08-03T00:00:00Z',
    definition: { schemaVersion: 1, kind: 'orchestrator', nodes, edges, governance: {} },
    // 固定 viewport（zoom 1）讓畫布座標可預測；沒有 viewport 時 designer 會 fitView。
    ui_metadata: { positions, viewport: { x: 0, y: 0, zoom: 1 } },
  }
}

async function json(route: Route, body: unknown, headers: Record<string, string> = {}) {
  await route.fulfill({ status: 200, contentType: 'application/json', headers, body: JSON.stringify(body) })
}

interface EditorOptions { etag?: boolean }

async function openEditor(page: Page, workflow: ReturnType<typeof workflowWith>, options: EditorOptions = {}) {
  const saved: Record<string, unknown>[] = []
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
    if (path === '/api/admin/workflows/w1/draft' && request.method() === 'PUT') {
      saved.push(request.postDataJSON() as Record<string, unknown>)
      return json(route, workflow, { ETag: '"2"' })
    }
    // ETag 缺失 = 唯讀（既有 disabled 路徑），用來驗證所有變更入口都封死。
    if (path === '/api/admin/workflows/w1') return json(route, workflow, options.etag === false ? {} : { ETag: '"1"' })
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
  return { saved }
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

const nodeIds = (page: Page) => page.locator('.react-flow__node').evaluateAll(
  (elements) => elements.map((element) => element.getAttribute('data-id')),
)

test('Ctrl+Z restores a dragged position and a created node; Ctrl+Shift+Z reapplies', async ({ page }) => {
  await openEditor(page, workflowWith([node('n1')], { n1: { x: 60, y: 60 } }))
  const moved = page.locator('.react-flow__node').first()
  const before = await box(moved)

  await dragBy(page, moved, 150, 80)
  await expect.poll(async () => Math.round((await box(moved)).x - before.x)).toBeGreaterThan(120)

  await page.keyboard.press('Control+z')
  await expect.poll(async () => Math.round((await box(moved)).x - before.x)).toBe(0)
  await page.keyboard.press('Control+Shift+z')
  await expect.poll(async () => Math.round((await box(moved)).x - before.x)).toBeGreaterThan(120)

  await page.getByRole('button', { name: '＋ Optional Step' }).click()
  await expect(page.locator('.react-flow__node')).toHaveCount(2)
  await page.keyboard.press('Control+z')
  await expect(page.locator('.react-flow__node')).toHaveCount(1)
})

test('a Ctrl+Z typed inside an inspector field never reaches the canvas undo', async ({ page }) => {
  await openEditor(page, workflowWith([node('n1')], { n1: { x: 60, y: 60 } }))
  const moved = page.locator('.react-flow__node').first()
  const before = await box(moved)

  await dragBy(page, moved, 150, 80)
  await expect.poll(async () => Math.round((await box(moved)).x - before.x)).toBeGreaterThan(120)

  const field = page.locator('#workflow-node-config-label')
  await field.fill('hello')
  await field.press('Control+z')
  await page.waitForTimeout(250)
  // 焦點在文字欄位內：畫布位置不得回到拖曳前。
  expect(Math.round((await box(moved)).x - before.x)).toBeGreaterThan(120)
})

test('Ctrl+C / Ctrl+V clones the selected nodes and their internal edge with fresh ids', async ({ page }) => {
  await openEditor(page, workflowWith(
    [node('n1'), node('n2')], { n1: { x: 60, y: 60 }, n2: { x: 320, y: 60 } }, [edge('e1', 'n1', 'n2')],
  ))
  const nodes = page.locator('.react-flow__node')
  const edges = page.locator('.react-flow__edge')
  await expect(edges).toHaveCount(1)
  const originals = await nodeIds(page)

  await page.locator('.react-flow__pane').click({ position: { x: 20, y: 200 } })
  await page.keyboard.press('Control+a')
  await page.keyboard.press('Control+c')
  await page.keyboard.press('Control+v')

  await expect(nodes).toHaveCount(4)
  await expect(edges).toHaveCount(2)
  const after = await nodeIds(page)
  expect(after.filter((id) => !originals.includes(id))).toHaveLength(2)
  // 貼上有位移：不會四個節點疊在同一點。
  const boxes = await Promise.all([0, 1, 2, 3].map((index) => box(nodes.nth(index))))
  expect(new Set(boxes.map((rect) => `${Math.round(rect.x)}:${Math.round(rect.y)}`)).size).toBe(4)
})

test('a left-drag on empty canvas box-selects both nodes and they then move together', async ({ page }) => {
  await openEditor(page, workflowWith([node('n1'), node('n2')], { n1: { x: 40, y: 40 }, n2: { x: 250, y: 40 } }))
  const nodes = page.locator('.react-flow__node')
  const canvas = await box(page.locator('.workflow-designer__canvas'))

  await page.mouse.move(canvas.x + 10, canvas.y + 200)
  await page.mouse.down()
  await page.mouse.move(canvas.x + 470, canvas.y + 20, { steps: 15 })
  await page.mouse.up()
  await expect(nodes.nth(0)).toHaveClass(/selected/)
  await expect(nodes.nth(1)).toHaveClass(/selected/)

  const before = await Promise.all([0, 1].map((index) => box(nodes.nth(index))))
  await dragBy(page, nodes.nth(0), 0, 120)
  await page.waitForTimeout(300)
  const after = await Promise.all([0, 1].map((index) => box(nodes.nth(index))))
  // 兩個一起移動，且放開後不回彈。
  expect(Math.round(after[0].y - before[0].y)).toBeGreaterThan(90)
  expect(Math.round(after[1].y - before[1].y)).toBeGreaterThan(90)
})

test('dropping a connection on empty canvas opens a picker of compatible types and auto-wires the pick', async ({ page }) => {
  await openEditor(page, workflowWith([node('n1')], { n1: { x: 60, y: 60 } }))
  const canvas = await box(page.locator('.workflow-designer__canvas'))
  const handle = await box(page.locator('.react-flow__handle.source').first())

  await page.mouse.move(handle.x + handle.width / 2, handle.y + handle.height / 2)
  await page.mouse.down()
  await page.mouse.move(canvas.x + 420, canvas.y + 240, { steps: 12 })
  await page.mouse.up()

  const picker = page.getByRole('dialog', { name: '接上新節點' })
  await expect(picker).toBeVisible()
  await expect(picker.getByRole('option', { name: /Optional Step/ })).toBeVisible()
  await expect(picker.getByRole('option', { name: /Required Gate/ })).toBeVisible()
  // dataType 不相容的型別不得出現。
  await expect(picker.getByRole('option', { name: /Data Step/ })).toHaveCount(0)

  await picker.getByRole('option', { name: /Optional Step/ }).click()
  await expect(page.locator('.react-flow__node')).toHaveCount(2)
  await expect(page.locator('.react-flow__edge')).toHaveCount(1)
})

test('the edge hover toolbar inserts a node in the middle and deletes the connection', async ({ page }) => {
  await openEditor(page, workflowWith(
    // 斜的邊：完全水平的貝茲線 bounding box 高度為 0，Playwright 會判定「不可見」而無法 hover。
    [node('n1'), node('n2')], { n1: { x: 40, y: 40 }, n2: { x: 360, y: 180 } }, [edge('e1', 'n1', 'n2')],
  ))
  const edges = page.locator('.react-flow__edge')
  await expect(edges).toHaveCount(1)

  await edges.first().hover()
  await page.getByRole('button', { name: '在此中插節點' }).click()
  const picker = page.getByRole('dialog', { name: '中插節點' })
  await expect(picker).toBeVisible()
  await picker.getByRole('option', { name: /Optional Step/ }).click()

  // 原邊消失，換成 A→N→B 兩條。
  await expect(page.locator('.react-flow__node')).toHaveCount(3)
  await expect(edges).toHaveCount(2)

  await edges.first().hover()
  await page.getByRole('button', { name: '刪除連線' }).first().click()
  await expect(edges).toHaveCount(1)
  await expect(page.locator('.react-flow__node')).toHaveCount(3)
})

test('the node context menu disables delete for required stages and removes deletable ones', async ({ page }) => {
  await openEditor(page, workflowWith(
    [node('n1'), node('n2', 'required_gate')], { n1: { x: 40, y: 40 }, n2: { x: 320, y: 40 } },
  ))
  const deletable = page.locator('.react-flow__node', { hasText: 'Optional Step' })
  const required = page.locator('.react-flow__node', { hasText: 'Required Gate' })

  await required.click({ button: 'right' })
  const menu = page.getByRole('menu', { name: '畫布操作' })
  await expect(menu.getByRole('menuitem', { name: '刪除' })).toBeDisabled()
  await expect(menu.getByRole('menuitem', { name: '刪除' })).toHaveAttribute('title', '必要節點不可刪除')
  await page.keyboard.press('Escape')
  await expect(menu).toHaveCount(0)

  await deletable.click({ button: 'right' })
  await page.getByRole('menuitem', { name: '刪除' }).click()
  await expect(deletable).toHaveCount(0)
  await expect(required).toHaveCount(1)
})

test('the pane context menu opens the shared picker with its search field focused', async ({ page }) => {
  await openEditor(page, workflowWith([node('n1')], { n1: { x: 60, y: 60 } }))
  await page.locator('.react-flow__pane').click({ button: 'right', position: { x: 40, y: 240 } })
  await page.getByRole('menuitem', { name: '新增節點' }).click()

  const picker = page.getByRole('dialog', { name: '新增節點' })
  await expect(picker).toBeVisible()
  // 選單收起時的焦點還原不得把焦點從 picker 搶走。
  await expect(page.getByLabel('搜尋節點', { exact: true })).toBeFocused()
  await page.keyboard.type('optional')
  await expect(picker.getByRole('option')).toHaveCount(1)
  await page.keyboard.press('Enter')
  await expect(page.locator('.react-flow__node')).toHaveCount(2)
})

test('Tab opens the picker, typing filters it, Enter inserts and Escape closes it', async ({ page }) => {
  await openEditor(page, workflowWith([node('n1')], { n1: { x: 60, y: 60 } }))
  await page.locator('.react-flow__pane').click({ position: { x: 30, y: 220 } })

  await page.keyboard.press('Tab')
  const picker = page.getByRole('dialog', { name: '新增節點' })
  await expect(picker).toBeVisible()
  await page.keyboard.press('Escape')
  await expect(picker).toHaveCount(0)
  await expect(page.locator('.react-flow__node')).toHaveCount(1)

  await page.locator('.react-flow__pane').click({ position: { x: 30, y: 220 } })
  await page.keyboard.press('Tab')
  // 開啟即接手輸入：不需再點一次搜尋框就能打字過濾。
  await expect(page.getByLabel('搜尋節點', { exact: true })).toBeFocused()
  await page.keyboard.type('required')
  await expect(picker.getByRole('option')).toHaveCount(1)
  await page.keyboard.press('Enter')
  await expect(picker).toHaveCount(0)
  await expect(page.locator('.react-flow__node', { hasText: 'Required Gate' })).toHaveCount(1)
})

test('Ctrl+A then Delete removes deletable nodes only — required stages survive', async ({ page }) => {
  // n2/n3 都是必要階段且彼此相連：那條邊的兩端都活下來，才問得出「連線受不受保護」。
  await openEditor(page, workflowWith(
    [node('n1'), node('n2', 'required_gate'), node('n3', 'required_gate')],
    { n1: { x: 40, y: 40 }, n2: { x: 260, y: 40 }, n3: { x: 40, y: 200 } },
    [edge('e1', 'n1', 'n2'), edge('e2', 'n2', 'n3')],
  ))
  await page.locator('.react-flow__pane').click({ position: { x: 20, y: 320 } })
  await page.keyboard.press('Control+a')
  await page.keyboard.press('Delete')

  await expect(page.locator('.react-flow__node')).toHaveCount(2)
  await expect(page.locator('.react-flow__node', { hasText: 'Required Gate' })).toHaveCount(2)
  // 刻意的語意（見 commitRemoval 註解）：保護只涵蓋節點，連線一併消失，留下孤立的必要階段——
  // 伺服器 validator 會以 unreachable_node / dead_end 擋下驗證與發布，且整件事可以復原。
  await expect(page.locator('.react-flow__edge')).toHaveCount(0)
  await page.locator('.react-flow__pane').click({ position: { x: 20, y: 320 } })
  await page.keyboard.press('Control+z')
  await page.keyboard.press('Control+z')
  await expect(page.locator('.react-flow__node')).toHaveCount(3)
  await expect(page.locator('.react-flow__edge')).toHaveCount(2)
  await expect(page.locator('.react-flow__node', { hasText: 'Optional Step' })).toHaveCount(1)
})

test('a required stage can never be duplicated: Ctrl+D, the context menu and the node toolbar all refuse', async ({ page }) => {
  await openEditor(page, workflowWith(
    [node('n1'), node('n2', 'required_gate')], { n1: { x: 40, y: 40 }, n2: { x: 320, y: 40 } },
  ))
  const required = page.locator('.react-flow__node', { hasText: 'Required Gate' })
  await required.click()
  await page.keyboard.press('Control+d')
  await page.waitForTimeout(250)
  // 再製出第二個必要階段 = 伺服器必回 duplicate_required_stage 的圖，不得產生。
  await expect(page.locator('.react-flow__node')).toHaveCount(2)

  await required.hover()
  const toolbarDuplicate = page.getByRole('button', { name: '再製' })
  await expect(toolbarDuplicate).toBeDisabled()
  await expect(toolbarDuplicate).toHaveAttribute('title', '必要節點不可再製')

  await required.click({ button: 'right' })
  const item = page.getByRole('menu', { name: '畫布操作' }).getByRole('menuitem', { name: '再製' })
  await expect(item).toBeDisabled()
  await expect(item).toHaveAttribute('title', '必要節點不可再製')
})

test('Tab out of the canvas toolbar is never trapped — only the canvas itself opens the picker', async ({ page }) => {
  await openEditor(page, workflowWith([node('n1')], { n1: { x: 60, y: 60 } }))
  const picker = page.getByRole('dialog', { name: '新增節點' })
  const layout = page.getByRole('button', { name: '自動排版' })

  await layout.focus()
  await page.keyboard.press('Tab')
  await expect(picker).toHaveCount(0)
  await expect(page.getByRole('button', { name: '快捷鍵說明' })).toBeFocused()

  await page.keyboard.press('Shift+Tab')
  await expect(picker).toHaveCount(0)
  await expect(layout).toBeFocused()

  // 焦點真的落在畫布容器本身時，Tab 仍然是「開節點選擇器」。
  await page.locator('.react-flow__pane').click({ position: { x: 30, y: 220 } })
  await page.keyboard.press('Tab')
  await expect(picker).toBeVisible()
})

test('closing the picker returns focus to the element that opened it', async ({ page }) => {
  await openEditor(page, workflowWith([node('n1')], { n1: { x: 60, y: 60 } }))
  const trigger = page.getByRole('button', { name: '＋ 新增節點' })
  await trigger.click()
  const picker = page.getByRole('dialog', { name: '新增節點' })
  await expect(picker).toBeVisible()

  await page.keyboard.press('Escape')
  await expect(picker).toHaveCount(0)
  await expect(trigger).toBeFocused()
})

test('the picker is a modal combobox that keeps Tab inside itself', async ({ page }) => {
  await openEditor(page, workflowWith([node('n1')], { n1: { x: 60, y: 60 } }))
  await page.getByRole('button', { name: '＋ 新增節點' }).click()
  const picker = page.getByRole('dialog', { name: '新增節點' })
  await expect(picker).toHaveAttribute('aria-modal', 'true')

  const search = page.getByRole('combobox', { name: '搜尋節點' })
  await expect(search).toBeFocused()
  await expect(search).toHaveAttribute('aria-expanded', 'true')
  await expect(search).toHaveAttribute('aria-autocomplete', 'list')

  await page.keyboard.press('Tab')
  await expect(picker).toBeVisible()
  await expect(search).toBeFocused()
})

test('shortcuts stay inside the editor: native Ctrl+C / Ctrl+A / Escape survive outside the canvas', async ({ page }) => {
  await openEditor(page, workflowWith([node('n1')], { n1: { x: 60, y: 60 } }))
  await page.evaluate(() => {
    const log: KeyboardEvent[] = []
    ;(window as unknown as { __keys: typeof log }).__keys = log
    window.addEventListener('keydown', (event) => log.push(event), true)
  })
  // defaultPrevented 只在派送「結束後」讀：同為 window capture 的監聽器只按註冊順序執行，而
  // designer 的 keydown effect 何時掛上不保證早於這支探針（實測會 flaky，而且反向漏判＝假綠燈）。
  // 純修飾鍵本身不是快捷鍵：React Flow 的多選鍵處理會 preventDefault 掉 Control/Shift 的 keydown。
  const takenKeys = () => page.evaluate(() => {
    const store = window as unknown as { __keys: KeyboardEvent[] }
    const modifiers = new Set(['Control', 'Shift', 'Alt', 'Meta'])
    const taken = store.__keys
      .filter((event) => event.defaultPrevented && !modifiers.has(event.key))
      .map((event) => event.key)
    store.__keys.length = 0
    return taken
  })

  await page.getByRole('button', { name: '儲存草稿' }).focus()
  await page.keyboard.press('Control+c')
  await page.keyboard.press('Control+a')
  await page.keyboard.press('Escape')
  // 編輯器外的焦點：整頁的原生複製/全選/Escape（含 CopilotKit 側欄）都不得被吃掉。
  expect(await takenKeys()).toEqual([])

  // 畫布內但沒有選取節點：Ctrl+C 一樣放行，否則使用者按了 Ctrl+C 兩邊都沒複製到東西。
  await page.locator('.react-flow__pane').click({ position: { x: 30, y: 220 } })
  await page.keyboard.press('Control+c')
  expect(await takenKeys()).toEqual([])

  // 反面：焦點在畫布內時，同一顆 Ctrl+A 仍然是畫布全選。
  await page.keyboard.press('Control+a')
  expect(await takenKeys()).toEqual(['a'])
  await expect(page.locator('.react-flow__node').first()).toHaveClass(/selected/)
})

test('undo back to the saved definition clears dirty and re-enables validate/simulate', async ({ page }) => {
  await openEditor(page, workflowWith([node('n1')], { n1: { x: 60, y: 60 } }))
  const validate = page.getByRole('button', { name: '驗證' })
  const simulate = page.getByRole('button', { name: '模擬' })
  await expect(validate).toBeEnabled()

  await page.getByRole('button', { name: '＋ Optional Step' }).click()
  await expect(page.locator('.react-flow__node')).toHaveCount(2)
  await expect(validate).toBeDisabled()
  await expect(validate).toHaveAttribute('title', '請先儲存草稿')

  // 焦點還在 palette 按鈕上（畫布外、編輯器內）：Ctrl+Z 仍要能復原，dirty 才回得到 false。
  await page.keyboard.press('Control+z')
  await expect(page.locator('.react-flow__node')).toHaveCount(1)
  await expect(validate).toBeEnabled()
  await expect(simulate).toBeEnabled()
})

test('arrow-key movement is committed, not only painted on screen', async ({ page }) => {
  const { saved } = await openEditor(page, workflowWith([node('n1')], { n1: { x: 100, y: 100 } }))
  const moved = page.locator('.react-flow__node').first()
  await moved.click()
  const before = await box(moved)

  for (let index = 0; index < 5; index += 1) await page.keyboard.press('ArrowRight')
  await page.keyboard.press('Shift+ArrowDown')
  await expect.poll(async () => Math.round((await box(moved)).x - before.x)).toBe(40)
  expect(Math.round((await box(moved)).y - before.y)).toBe(40)

  // 真正的判準：位置有沒有進到提交出去的草稿（只有畫面動 = 靜默不儲存）。
  await page.getByRole('button', { name: '儲存草稿' }).click()
  await expect.poll(() => saved.length).toBe(1)
  expect((saved[0] as { ui_metadata: { positions: Record<string, { x: number; y: number }> } }).ui_metadata.positions.n1)
    .toEqual({ x: 140, y: 140 })

  // Ctrl+S 走同一條儲存路徑（瀏覽器的存檔對話框要被 preventDefault 擋掉）。
  await moved.click()
  await page.keyboard.press('Control+s')
  await expect.poll(() => saved.length).toBe(2)
})

test('the ? overlay lists the shortcut table and Escape closes it', async ({ page }) => {
  await openEditor(page, workflowWith([node('n1')], { n1: { x: 60, y: 60 } }))
  await page.locator('.react-flow__pane').click({ position: { x: 30, y: 220 } })

  await page.keyboard.press('?')
  const help = page.getByRole('dialog', { name: '鍵盤快捷鍵' })
  await expect(help).toBeVisible()
  await expect(help.getByText('移動選取節點 8px / 40px')).toBeVisible()
  await page.keyboard.press('Escape')
  await expect(help).toBeHidden()
})

test('a read-only editor blocks every new entry point: shortcuts, menu, toolbars and picker', async ({ page }) => {
  await openEditor(page, workflowWith(
    [node('n1'), node('n2')], { n1: { x: 40, y: 40 }, n2: { x: 320, y: 180 } }, [edge('e1', 'n1', 'n2')],
  ), { etag: false })
  const nodes = page.locator('.react-flow__node')
  await expect(nodes).toHaveCount(2)
  await expect(page.getByRole('button', { name: '＋ Optional Step' })).toBeDisabled()
  await expect(page.getByRole('button', { name: '＋ 新增節點' })).toBeDisabled()

  await nodes.first().hover()
  await expect(page.getByRole('button', { name: '再製' })).toHaveCount(0)
  await page.locator('.react-flow__edge').first().hover()
  await expect(page.getByRole('button', { name: '在此中插節點' })).toHaveCount(0)

  // 歷史按鈕在唯讀時也不得是入口。
  await expect(page.getByRole('button', { name: '↩ 復原' })).toBeDisabled()
  await expect(page.getByRole('button', { name: '↪ 重做' })).toBeDisabled()
  await expect(page.getByRole('button', { name: '自動排版' })).toBeDisabled()

  await page.locator('.react-flow__pane').click({ position: { x: 20, y: 260 } })
  const before = await box(nodes.first())
  await page.keyboard.press('Tab')
  await expect(page.getByRole('dialog', { name: '新增節點' })).toHaveCount(0)
  await page.keyboard.press('Control+a')
  await page.keyboard.press('Delete')
  await page.keyboard.press('Control+c')
  await page.keyboard.press('Control+v')
  await page.keyboard.press('Control+x')
  await page.keyboard.press('Control+d')
  await page.keyboard.press('Control+z')
  await page.keyboard.press('Shift+Alt+KeyT')
  await page.keyboard.press('ArrowRight')
  await page.waitForTimeout(300)
  await expect(nodes).toHaveCount(2)
  await expect(page.locator('.react-flow__edge')).toHaveCount(1)
  // 自動排版與方向鍵微調都不得移動任何節點。
  const after = await box(nodes.first())
  expect([Math.round(after.x - before.x), Math.round(after.y - before.y)]).toEqual([0, 0])

  await nodes.first().click({ button: 'right' })
  const menu = page.getByRole('menu', { name: '畫布操作' })
  await expect(menu.getByRole('menuitem', { name: '再製' })).toBeDisabled()
  await expect(menu.getByRole('menuitem', { name: '刪除' })).toBeDisabled()
})
