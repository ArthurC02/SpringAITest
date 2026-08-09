import { expect, test, type Page, type Route } from '@playwright/test'

// O5「排程觸發」(04-operations-trigger-plan.md §6):閘門矩陣、建立表單(含 datetime-local
// → UTC 轉換)、取消確認、fire 歷史原因碼、CatalogPicker fail-open、空/錯誤狀態。

async function json(route: Route, body: unknown, status = 200, headers: Record<string, string> = {}) {
  await route.fulfill({ status, contentType: 'application/json', headers, body: JSON.stringify(body) })
}

const ORCHESTRATORS = [
  { id: 'orch-1', name: '週報協作', description: '', enabled: true, published_revision: 3, updated_at: '2026-08-01T00:00:00Z' },
  { id: 'orch-draft', name: '尚未發布', description: '', enabled: true, published_revision: null, updated_at: '2026-08-01T00:00:00Z' },
]

// 鍵名抄自 backend TriggerApiTests 斷言的 DTO allowlist:TriggerResponse 沒有 root_run_id,
// 清單一律包在 {items:[...]},occurrence 用 scheduled_for 且原因碼併在 status 前綴。
const SCHEDULED = {
  id: 'trigger-1', name: '週一報表', description: '',
  orchestrator_id: 'orch-1', orchestrator_revision: 3,
  fire_at: '2026-08-10T02:30:00Z', misfire_window_seconds: 300,
  status: 'scheduled', input_mapping: { message: '產生報表', topic: 'weekly' },
  created_by: 'admin',
  created_at: '2026-08-09T00:00:00Z', updated_at: '2026-08-09T00:00:00Z',
}

const FIRED = { ...SCHEDULED, id: 'trigger-2', name: '已觸發的排程', status: 'fired' }

interface Options {
  role?: 'ADMIN' | 'USER'
  capabilities?: string[]
  features?: Record<string, boolean>
  handler?: (route: Route, url: URL, method: string) => Promise<boolean>
}

async function login(page: Page, options: Options) {
  await page.route('**/api/**', async (route) => {
    const request = route.request()
    const url = new URL(request.url())
    const path = url.pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') {
      return json(route, {
        token: 'triggers-token', username: 'tester', role: options.role ?? 'ADMIN', tenantCode: 'demo',
        capabilities: options.capabilities ?? [],
      })
    }
    if (path === '/api/features') return json(route, options.features ?? {})
    if (options.handler && await options.handler(route, url, request.method())) return
    if (path === '/api/admin/orchestrators') return json(route, ORCHESTRATORS)
    return json(route, [])
  })
  await page.goto('/')
  await page.getByTestId('auth-username').fill('tester')
  await page.getByTestId('auth-password').fill('password123')
  await page.getByTestId('auth-submit').click()
}

async function openTriggers(page: Page, options: Omit<Options, 'features' | 'capabilities'> = {}) {
  await login(page, {
    ...options,
    capabilities: ['workflow.manage'],
    features: { agentTriggersEnabled: true },
  })
  await page.getByTestId('nav-triggers').click()
  await expect(page.getByRole('heading', { name: '排程觸發', exact: true })).toBeVisible()
}

test('the 排程觸發 entry needs the flag; the flag alone is not enough', async ({ page }) => {
  await login(page, { capabilities: ['workflow.manage'], features: { agentTriggersEnabled: false } })
  await expect(page.getByTestId('nav-triggers')).toHaveCount(0)
})

test('workflow.manage is required even with the flag on', async ({ page }) => {
  await login(page, { capabilities: [], features: { agentTriggersEnabled: true } })
  await expect(page.getByTestId('nav-triggers')).toHaveCount(0)
})

test('a capability that merely starts with workflow.manage is not a match', async ({ page }) => {
  await login(page, { capabilities: ['workflow.manage.other'], features: { agentTriggersEnabled: true } })
  await expect(page.getByTestId('nav-triggers')).toHaveCount(0)
})

test('both the flag and exact workflow.manage open the entry, USER role included', async ({ page }) => {
  await login(page, { role: 'USER', capabilities: ['workflow.manage'], features: { agentTriggersEnabled: true } })
  await expect(page.getByTestId('nav-triggers')).toBeVisible()
})

test('the list shows the target name and the Chinese status chip', async ({ page }) => {
  await openTriggers(page, {
    handler: async (route, url) => {
      if (url.pathname === '/api/admin/triggers') {
        await json(route, { items: [SCHEDULED, FIRED] })
        return true
      }
      return false
    },
  })

  const scheduledRow = page.locator('article.agent-block', { hasText: '週一報表' })
  await expect(scheduledRow.locator('.chip--processing')).toHaveText('已排程')
  // 目標經 catalog 對照成名稱 + pinned revision(不是裸 GUID)。
  await expect(scheduledRow.getByText('週報協作 · r3')).toBeVisible()
  await expect(scheduledRow.getByText('admin')).toBeVisible()

  const firedRow = page.locator('article.agent-block', { hasText: '已觸發的排程' })
  await expect(firedRow.locator('.chip--ready')).toHaveText('已觸發')
  // 已觸發的排程沒有取消鈕(只有 scheduled 能取消)。
  await expect(firedRow.getByRole('button', { name: '取消' })).toHaveCount(0)
})

test('creating a trigger auto-fills the published revision and posts an absolute UTC fire_at', async ({ page }) => {
  let created: Record<string, unknown> | null = null
  await openTriggers(page, {
    handler: async (route, url, method) => {
      if (url.pathname === '/api/admin/triggers' && method === 'POST') {
        created = JSON.parse(route.request().postData() ?? '{}')
        await json(route, { ...SCHEDULED, id: 'trigger-new' })
        return true
      }
      if (url.pathname === '/api/admin/triggers') {
        await json(route, { items: created ? [SCHEDULED] : [] })
        return true
      }
      return false
    },
  })

  await expect(page.getByText('目前沒有排程觸發器。')).toBeVisible()
  await page.getByLabel('名稱').fill('週一報表')
  // 只有已發布的協作流程出現在清單裡(orch-draft 的 published_revision 是 null)。
  await expect(page.getByLabel('目標 Orchestrator').locator('option')).toHaveText(['請選擇…', '週報協作 (r3)'])
  await page.getByLabel('目標 Orchestrator').selectOption('orch-1')
  await expect(page.getByLabel('目標 revision')).toHaveValue('3')
  await page.getByLabel('預定時間(本地時間)').fill('2026-08-10T10:30')
  await page.getByLabel('新增鍵').fill('topic')
  await page.getByLabel('新增值').fill('weekly')
  await page.getByRole('button', { name: '加入' }).click()
  await page.getByRole('button', { name: '建立觸發器' }).click()

  await expect(page.locator('article.agent-block', { hasText: '週一報表' })).toBeVisible()
  const body = created as unknown as Record<string, unknown>
  expect(body).not.toBeNull()
  expect(body.name).toBe('週一報表')
  // 欄位名必須是 backend TriggerCreateRequest 綁的那組,否則後端讀成 Guid.Empty 一律 404。
  expect(body.orchestrator_id).toBe('orch-1')
  expect(body.orchestrator_revision).toBe(3)
  expect(body.input_mapping).toEqual({ topic: 'weekly' })
  // 送出的是絕對 UTC 時刻,且等於瀏覽器時區的 2026-08-10 10:30。
  const fireAt = String(body.fire_at)
  expect(fireAt).toBe(new Date(fireAt).toISOString())
  const expected = await page.evaluate(() => new Date(2026, 7, 10, 10, 30).toISOString())
  expect(fireAt).toBe(expected)
  // 送出成功後表單清空,不會殘留上一筆內容。
  await expect(page.getByLabel('名稱')).toHaveValue('')
})

test('submitting an incomplete form shows Chinese field errors and sends no request', async ({ page }) => {
  let posts = 0
  await openTriggers(page, {
    handler: async (route, url, method) => {
      if (url.pathname === '/api/admin/triggers' && method === 'POST') {
        posts += 1
        await json(route, SCHEDULED)
        return true
      }
      if (url.pathname === '/api/admin/triggers') {
        await json(route, { items: [] })
        return true
      }
      return false
    },
  })

  await page.getByRole('button', { name: '建立觸發器' }).click()
  await expect(page.getByText('請輸入名稱。')).toBeVisible()
  await expect(page.getByText('請選擇目標 Orchestrator。')).toBeVisible()
  await expect(page.getByText('請填寫目標 revision(正整數)。')).toBeVisible()
  await expect(page.getByText('請選擇有效的預定時間。')).toBeVisible()
  expect(posts).toBe(0)

  // 進階 JSON 逃生門的解析錯誤同樣擋住送出。
  await page.getByRole('button', { name: '切換為進階 JSON 模式' }).click()
  await page.getByLabel('Input mapping(進階 JSON 模式)').fill('{ not json')
  await page.getByRole('button', { name: '建立觸發器' }).click()
  await expect(page.getByText('Input mapping 不是合法 JSON。')).toBeVisible()
  expect(posts).toBe(0)
})

// 後端 cancel 缺 If-Match 直接 428,所以這裡真的斷言送出的 If-Match 等於當前 ETag,
// 否則這條路徑會像先前一樣「測試全綠、實際 100% 失敗」。
test('cancelling asks for confirmation first and then sends the current ETag as If-Match', async ({ page }) => {
  let cancels = 0
  let sentIfMatch: string | undefined
  await openTriggers(page, {
    handler: async (route, url, method) => {
      if (url.pathname === '/api/admin/triggers/trigger-1/cancel' && method === 'POST') {
        cancels += 1
        sentIfMatch = route.request().headers()['if-match']
        await json(route, { ...SCHEDULED, status: 'cancelled' })
        return true
      }
      if (url.pathname === '/api/admin/triggers/trigger-1' && method === 'GET') {
        await json(route, SCHEDULED, 200, { ETag: '"4"' })
        return true
      }
      if (url.pathname === '/api/admin/triggers') {
        await json(route, { items: [cancels > 0 ? { ...SCHEDULED, status: 'cancelled' } : SCHEDULED] })
        return true
      }
      return false
    },
  })

  await page.getByRole('button', { name: '取消', exact: true }).click()
  await expect(page.getByText('確認取消排程觸發器「週一報表」?')).toBeVisible()
  await page.getByRole('button', { name: '取消', exact: true }).last().click()
  expect(cancels).toBe(0)

  await page.getByRole('button', { name: '取消', exact: true }).first().click()
  await page.getByRole('button', { name: '確認取消' }).click()
  await expect(page.locator('.chip--skip', { hasText: '已取消' })).toBeVisible()
  expect(cancels).toBe(1)
  expect(sentIfMatch).toBe('"4"')
})

test('the fire history loads on expand, renders the bounded status in Chinese and the fire result', async ({ page }) => {
  let occurrenceCalls = 0
  await openTriggers(page, {
    handler: async (route, url) => {
      if (url.pathname === '/api/admin/triggers') {
        await json(route, { items: [SCHEDULED] })
        return true
      }
      if (url.pathname === '/api/admin/triggers/trigger-1/occurrences') {
        occurrenceCalls += 1
        await json(route, {
          items: [
            {
              id: 'occ-1', trigger_id: 'trigger-1', scheduled_for: '2026-08-10T02:30:00Z',
              status: 'fired', root_run_id: 'abcdef12-3333-4444-8555-666666666666',
              created_at: '2026-08-10T02:30:01Z', updated_at: '2026-08-10T02:30:02Z',
            },
            {
              id: 'occ-2', trigger_id: 'trigger-1', scheduled_for: '2026-08-09T02:30:00Z',
              status: 'failed_target_unpublished', root_run_id: null,
              created_at: '2026-08-09T02:30:01Z', updated_at: '2026-08-09T02:30:02Z',
            },
          ],
          has_more: false, next_cursor: null,
        })
        return true
      }
      return false
    },
  })

  // 展開前不打歷史 API(每列一支請求是白費流量)。
  expect(occurrenceCalls).toBe(0)
  await page.getByText('展開 input mapping 與 fire 歷史').click()
  // 釘選的 sanitized input mapping 與 fire 歷史同區呈現。
  await expect(page.getByText('weekly')).toBeVisible()
  // 觸發結果只存在於 occurrence(TriggerResponse 沒有 root_run_id)。
  await expect(page.getByText('abcdef12(請至「執行總覽」查看)')).toBeVisible()
  // 原因碼是 status 的前綴,字典 key 必須是後端真值。
  await expect(page.getByText('失敗(目標協作流程未發布)')).toBeVisible()
  expect(occurrenceCalls).toBe(1)
})

// 「重新載入」的 onClick 沒有任何進行中判斷（按下的當下錯誤區塊才被 setError(null) 收掉），
// 所以同一個 tick 內連點兩次真的會送出兩個並行請求。沒有世代守衛時，先送出的那個晚到就會
// 覆寫較新的畫面狀態——這裡讓第一個回應「舊資料且慢」、第二個「新資料且快」來釘住這件事。
function occurrence(id: string, rootRunId: string) {
  return {
    id, trigger_id: 'trigger-1', scheduled_for: '2026-08-10T02:30:00Z',
    status: 'fired', root_run_id: rootRunId,
    created_at: '2026-08-10T02:30:01Z', updated_at: '2026-08-10T02:30:02Z',
  }
}

test('a slow earlier fire-history response cannot overwrite the newer one', async ({ page }) => {
  let occurrenceCalls = 0
  let releaseStale!: () => void
  const staleGate = new Promise<void>((resolve) => { releaseStale = resolve })

  await openTriggers(page, {
    handler: async (route, url) => {
      if (url.pathname === '/api/admin/triggers') {
        await json(route, { items: [SCHEDULED] })
        return true
      }
      if (url.pathname === '/api/admin/triggers/trigger-1/occurrences') {
        occurrenceCalls += 1
        // 展開時的第一次載入失敗，讓「重新載入」按鈕出現。
        if (occurrenceCalls === 1) {
          await json(route, { timestamp: '2026-08-09T00:00:00Z', status: 500, message: '伺服器暫時無法回應', fieldErrors: {} }, 500)
          return true
        }
        if (occurrenceCalls === 2) {
          await staleGate
          await json(route, { items: [occurrence('occ-stale', 'aaaaaaaa-3333-4444-8555-666666666666')], has_more: false, next_cursor: null })
          return true
        }
        await json(route, { items: [occurrence('occ-fresh', 'bbbbbbbb-3333-4444-8555-666666666666')], has_more: false, next_cursor: null })
        return true
      }
      return false
    },
  })

  await page.getByText('展開 input mapping 與 fire 歷史').click()
  await expect(page.getByRole('button', { name: '重新載入' })).toBeEnabled()

  // 同一個 tick 內按兩次：React 尚未重繪，兩次 onClick 都會真的送出請求。
  await page.evaluate(() => {
    const button = [...document.querySelectorAll('button')].find((b) => b.textContent === '重新載入')
    button?.click()
    button?.click()
  })
  await expect.poll(() => occurrenceCalls).toBe(3)

  // 較新的那個先回來並上畫面。
  await expect(page.getByText('bbbbbbbb(請至「執行總覽」查看)')).toBeVisible()

  // 較舊的那個晚到，必須被世代守衛丟掉。
  releaseStale()
  await expect(page.getByText('aaaaaaaa(請至「執行總覽」查看)')).toHaveCount(0)
  await expect(page.getByText('bbbbbbbb(請至「執行總覽」查看)')).toBeVisible()
})

test('an unavailable orchestrator catalog degrades to manual id entry instead of blocking the form', async ({ page }) => {
  await openTriggers(page, {
    handler: async (route, url) => {
      if (url.pathname === '/api/admin/orchestrators') {
        await json(route, { timestamp: '2026-08-09T00:00:00Z', status: 404, message: '找不到資源', fieldErrors: {} }, 404)
        return true
      }
      if (url.pathname === '/api/admin/triggers') {
        await json(route, { items: [SCHEDULED] })
        return true
      }
      return false
    },
  })

  await expect(page.getByText('清單載入失敗,已切換為手動輸入。')).toBeVisible()
  await page.getByLabel('目標 Orchestrator').fill('orch-manual')
  await expect(page.getByLabel('目標 Orchestrator')).toHaveValue('orch-manual')
  // 目錄不可用時,清單列的目標退回截短 id,不是空白。
  await expect(page.locator('article.agent-block', { hasText: '週一報表' }).getByText('orch-1 · r3')).toBeVisible()
})

test('a failing trigger list shows an error with a working retry', async ({ page }) => {
  let mode: 'fail' | 'empty' = 'fail'
  await openTriggers(page, {
    handler: async (route, url) => {
      if (url.pathname !== '/api/admin/triggers') return false
      if (mode === 'fail') {
        await json(route, { timestamp: '2026-08-09T00:00:00Z', status: 500, message: '伺服器暫時無法回應', fieldErrors: {} }, 500)
        return true
      }
      await json(route, { items: [] })
      return true
    },
  })

  await expect(page.getByRole('alert')).toContainText('伺服器暫時無法回應')
  mode = 'empty'
  await page.getByRole('button', { name: '重新載入' }).click()
  await expect(page.getByText('目前沒有排程觸發器。')).toBeVisible()
})
