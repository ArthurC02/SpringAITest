import { expect, test, type Route } from '@playwright/test'

const runActionable = '11111111-1111-4111-8111-111111111111'
const runExpired = '22222222-2222-4222-8222-222222222222'
const runVisibleOnly = '33333333-3333-4333-8333-333333333333'
const agentId = '44444444-4444-4444-8444-444444444444'

async function json(
  route: Route,
  body: unknown,
  options: { status?: number; headers?: Record<string, string> } = {},
) {
  await route.fulfill({
    status: options.status ?? 200,
    contentType: 'application/json',
    headers: options.headers,
    body: JSON.stringify(body),
  })
}

async function login(page: import('@playwright/test').Page) {
  await page.goto('/')
  const username = page.getByTestId('auth-username')
  if (await username.isVisible({ timeout: 5000 }).catch(() => false)) {
    await username.fill('admin')
    await page.getByTestId('auth-password').fill('password123')
    await page.getByTestId('auth-submit').click()
  }
  await page.getByTestId('nav-approvals').click()
}

test('approval queue renders, switches scope, paginates, shows actionable/expired visuals, and decides from a queue click', async ({
  page,
}) => {
  const queueCalls: Array<{ scope: string | null; cursor: string | null }> = []
  const decisions: Array<{ approvalId: string; key: string | undefined }> = []

  await page.route('**/api/**', async (route) => {
    const request = route.request()
    const url = new URL(request.url())
    const path = url.pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') {
      return json(route, { token: 'queue-token', username: 'admin', role: 'ADMIN', tenantCode: 'demo' })
    }
    if (path === '/api/features') {
      return json(route, { agentWriteToolsEnabled: true })
    }
    if (path === '/api/documents' || path === '/api/chat/history') return json(route, [])

    if (path === '/api/runs/approvals') {
      const scope = url.searchParams.get('scope')
      const cursor = url.searchParams.get('cursor')
      queueCalls.push({ scope, cursor })
      if (scope === 'actionable' && !cursor) {
        return json(route, {
          items: [{
            approval_id: 'approval-1', run_id: runActionable, agent_id: agentId, agent_revision: 3,
            status: 'pending', required_role: 'USER', action_summary: 'runtime.write_evidence',
            created_at: '2026-01-01T00:00:00Z', expires_at: '2099-01-01T00:00:00Z', actionable: true,
          }],
          has_more: true, next_cursor: 'cursor-1',
        })
      }
      if (scope === 'actionable' && cursor === 'cursor-1') {
        return json(route, {
          items: [{
            approval_id: 'approval-2', run_id: runExpired, agent_id: agentId, agent_revision: 1,
            status: 'pending', required_role: 'ADMIN', action_summary: 'runtime.write_evidence',
            created_at: '2025-01-01T00:00:00Z', expires_at: '2020-01-01T00:00:00Z', actionable: false,
          }],
          has_more: false, next_cursor: null,
        })
      }
      if (scope === 'visible') {
        return json(route, {
          items: [
            {
              approval_id: 'approval-1', run_id: runActionable, agent_id: agentId, agent_revision: 3,
              status: 'pending', required_role: 'USER', action_summary: 'runtime.write_evidence',
              created_at: '2026-01-01T00:00:00Z', expires_at: '2099-01-01T00:00:00Z', actionable: true,
            },
            {
              approval_id: 'approval-3', run_id: runVisibleOnly, agent_id: agentId, agent_revision: 2,
              status: 'pending', required_role: 'USER', action_summary: 'unlisted.custom.action',
              created_at: '2026-01-01T00:00:00Z', expires_at: '2099-01-01T00:00:00Z', actionable: false,
            },
          ],
          has_more: false, next_cursor: null,
        })
      }
      return json(route, { items: [], has_more: false, next_cursor: null })
    }

    if (path === `/api/runs/${runActionable}/approvals` && request.method() === 'GET') {
      return json(route, [{
        id: 'approval-1', run_id: runActionable, status: 'pending', required_role: 'USER',
        expires_at: '2099-01-01T00:00:00Z',
      }])
    }
    if (path === `/api/runs/${runActionable}/approvals/approval-1/approve` && request.method() === 'POST') {
      decisions.push({ approvalId: 'approval-1', key: request.headers()['idempotency-key'] })
      return json(route, { id: 'approval-1', status: 'approved', decision: 'approved', decided_by: 'admin' })
    }

    return json(route, [])
  })

  await login(page)
  await expect(page.getByRole('heading', { name: 'Run 核准' })).toBeVisible()

  // 預設 scope=actionable，自動載入第一頁。
  await expect(page.getByRole('button', { name: '寫入執行證據' })).toBeVisible()
  await expect(page.getByText('44444444 · r3')).toBeVisible()
  await expect(page.getByText('已過期')).toHaveCount(0)

  // 分頁：載入更多把第二頁附加上去，不取代第一頁。
  await page.getByRole('button', { name: '載入更多' }).click()
  await expect(page.getByText('已過期')).toBeVisible()
  await expect(page.getByRole('button', { name: '載入更多' })).toHaveCount(0)
  // React StrictMode replays the initial mount effect once in dev, so the first page may be
  // requested twice; what matters is that both pages were fetched and appended, not replaced.
  expect(queueCalls).toContainEqual({ scope: 'actionable', cursor: null })
  expect(queueCalls).toContainEqual({ scope: 'actionable', cursor: 'cursor-1' })

  // scope 切換：全部可見，額外顯示一筆不可核准（未過期）的項目，帶不同視覺提示。
  await page.getByRole('button', { name: '全部可見' }).click()
  await expect(page.getByText('unlisted.custom.action')).toBeVisible()
  await expect(page.getByText('你目前不可核准')).toBeVisible()

  // 點佇列項＝自動代填 Run ID 並沿用既有決策 UI（不重寫）。
  await page.getByRole('button', { name: '寫入執行證據' }).first().click()
  await expect(page.getByRole('button', { name: '核准', exact: true })).toBeVisible()
  await page.getByRole('button', { name: '核准', exact: true }).click()
  await expect(page.getByText('已核准。')).toBeVisible()

  // 決策完成後，對應佇列項從畫面上移除。
  await expect(page.getByText('寫入執行證據')).toHaveCount(0)
  expect(decisions).toEqual([{ approvalId: 'approval-1', key: expect.any(String) }])
})

test('a non-404 queue failure shows an inline error but keeps the manual Run ID lookup usable, and an empty queue shows the empty-state notice', async ({
  page,
}) => {
  // 用明確的 mode 旗標而非呼叫次數判斷佇列回應：React StrictMode 在 dev 下會重放一次
  // 初始 effect，呼叫次數不等於「使用者按下重新載入」這個動作，用旗標才不會誤判成功/失敗次序。
  let mode: 'fail' | 'empty' = 'fail'

  await page.route('**/api/**', async (route) => {
    const request = route.request()
    const path = new URL(request.url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') {
      return json(route, { token: 'queue-fail-token', username: 'admin', role: 'ADMIN', tenantCode: 'demo' })
    }
    if (path === '/api/features') return json(route, { agentWriteToolsEnabled: true })
    if (path === '/api/documents' || path === '/api/chat/history') return json(route, [])

    if (path === '/api/runs/approvals') {
      if (mode === 'fail') {
        return json(
          route,
          { timestamp: '2026-08-09T00:00:00Z', status: 500, message: '伺服器暫時無法回應', fieldErrors: {} },
          { status: 500 },
        )
      }
      return json(route, { items: [], has_more: false, next_cursor: null })
    }

    if (path === `/api/runs/${runActionable}/approvals` && request.method() === 'GET') {
      return json(route, [])
    }

    return json(route, [])
  })

  await login(page)

  // 非 404 佇列載入失敗：錯誤顯示，手輸 Run ID 進階查詢仍可用（fail-open）。
  await expect(page.getByRole('alert')).toContainText('伺服器暫時無法回應')
  await page.getByText('已知 Run ID 時可直接查詢（進階）').click()
  await page.getByLabel('Run ID').fill(runActionable)
  await page.getByRole('button', { name: '查詢待核准項目' }).click()
  await expect(page.getByText('此 Run 目前沒有你可查看的待核准項目。')).toBeVisible()

  // 重新載入成功、佇列為空 → 空狀態文案。
  mode = 'empty'
  await page.getByRole('button', { name: '重新載入' }).click()
  await expect(page.getByText('目前沒有等待你核准的項目。')).toBeVisible()
  await expect(page.getByRole('alert')).toHaveCount(0)
})
