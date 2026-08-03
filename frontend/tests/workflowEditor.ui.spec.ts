import { expect, test, type Page, type Route } from '@playwright/test'

// WorkflowsView 的樂觀併發衝突瀏覽器回歸(orchestratorEditor.ui.spec.ts 的同位案例):
// 409 必須鎖定編輯器、留下「重新載入」出口,且永遠不噴假的成功 toast。
// 授權來源:`Toast.tsx` 的 runWithToast(conflict 由本層處理)與 `WorkflowsView.tsx` 的 onConflict。
// Designer(React Flow + elkjs)在真實瀏覽器直接渲染,不 mock —— 本案只操作動作列按鈕,
// 不碰畫布,也就不需要 ELK 排版。

const definition = { schemaVersion: 1, kind: 'orchestrator', nodes: [], edges: [], governance: {} }

const workflow = {
  id: 'w1', name: 'Root harness', kind: 'orchestrator', enabled: true,
  draft_version: 1, published_revision: null, updated_at: '2026-08-03T00:00:00Z',
  definition, ui_metadata: { positions: {} },
}

async function json(route: Route, body: unknown, headers: Record<string, string> = {}) {
  await route.fulfill({ status: 200, contentType: 'application/json', headers, body: JSON.stringify(body) })
}

interface Harness {
  /** PUT /draft 的下一次回應狀態(409 用來觸發樂觀併發衝突)。 */
  draftStatus: number
  /** 伺服器目前持有的 workflow（重新載入會讀到這份）。 */
  current: typeof workflow
}

async function openEditor(page: Page): Promise<Harness> {
  const harness: Harness = { draftStatus: 200, current: workflow }
  await page.route('**/api/**', async (route) => {
    const request = route.request()
    const path = new URL(request.url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') {
      return json(route, {
        token: 'workflow-token', username: 'tester', role: 'ADMIN', tenantCode: 'demo',
        capabilities: ['workflow.manage'],
      })
    }
    if (path === '/api/features') return json(route, { workflowDesignerEnabled: true })
    if (path === '/api/admin/workflows/catalog/nodes') return json(route, { nodes: [] })
    if (path === '/api/admin/workflows' && request.method() === 'GET') return json(route, [harness.current])
    if (path === '/api/admin/workflows/w1') return json(route, harness.current, { ETag: `"${harness.current.draft_version}"` })
    if (path === '/api/admin/workflows/w1/draft' && request.method() === 'PUT') {
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
  await page.getByTestId('agent-platform-tab-workflows').click()
  await page.getByRole('button', { name: '編輯' }).click()
  await expect(page.getByRole('heading', { name: 'Root harness', level: 2 })).toBeVisible()
  return harness
}

test('a 409 workflow draft save locks the designer, exposes reload, and emits no success toast', async ({ page }) => {
  const harness = await openEditor(page)
  const save = page.getByRole('button', { name: '儲存草稿' })

  harness.draftStatus = 409
  await save.click()

  await expect(page.getByRole('alert')).toContainText('草稿已由其他人更新')
  await expect(page.locator('.toast--success')).toHaveCount(0)
  await expect(save).toBeDisabled()
  await expect(page.getByRole('button', { name: '驗證' })).toBeDisabled()
  await expect(page.getByRole('button', { name: '模擬' })).toBeDisabled()
  await expect(page.getByRole('button', { name: '發布' })).toBeDisabled()

  // 鎖定不是永久的:重新載入取得新資料與新 ETag,顯示同步到伺服器版本後才恢復可寫。
  harness.draftStatus = 200
  harness.current = { ...workflow, draft_version: 2, name: 'Root harness r2' }
  await page.getByRole('button', { name: '重新載入' }).click()
  await expect(page.getByRole('alert')).toHaveCount(0)
  await expect(page.getByRole('heading', { name: 'Root harness r2', level: 2 })).toBeVisible()
  await expect(save).toBeEnabled()

  await save.click()
  await expect(page.locator('.toast--success')).toContainText('草稿已儲存')
})
