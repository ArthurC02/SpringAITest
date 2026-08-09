import { expect, test, type Page, type Route } from '@playwright/test'

// WS1-a 「問這份文件」狀態閘（見 DocumentsView.tsx ASK_DISABLED_REASON / AppShell.askAboutDocument）:
// 只有 ready 列的鈕可按,processing/failed 列停用且 title 帶原因；按下 ready 列會展開副駕側欄,
// 讓後續提問自動帶入該文件脈絡。這裡驗證的是正面斷言（enabled/disabled + 展開狀態），不是「不報錯」。

const READY_DOC = { id: 'doc-ready', title: '就緒文件', status: 'ready', chunk_count: 3, created_at: '2026-07-25T00:00:00Z' }
const PROCESSING_DOC = { id: 'doc-processing', title: '處理中文件', status: 'processing', chunk_count: 0, created_at: '2026-07-25T00:00:00Z' }
const FAILED_DOC = { id: 'doc-failed', title: '失敗文件', status: 'failed', chunk_count: 0, created_at: '2026-07-25T00:00:00Z' }

async function json(route: Route, body: unknown, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
}

async function mountDocuments(page: Page): Promise<void> {
  await page.route('**/api/**', async (route) => {
    const path = new URL(route.request().url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') {
      return json(route, { token: 'token', username: 'user', role: 'USER', tenantCode: 'demo', capabilities: [] })
    }
    if (path === '/api/features') return json(route, {})
    if (path === '/api/documents' && route.request().method() === 'GET') {
      return json(route, [READY_DOC, PROCESSING_DOC, FAILED_DOC])
    }
    return json(route, [])
  })
  await page.goto('/')
  await page.getByTestId('auth-username').fill('user')
  await page.getByTestId('auth-password').fill('password123')
  await page.getByTestId('auth-submit').click()
  await page.getByTestId('nav-documents').click()
}

function askButton(page: Page, title: string) {
  return page.locator('tbody tr').filter({ hasText: title }).getByRole('button', { name: '問這份文件' })
}

test.describe('「問這份文件」狀態閘', () => {
  test('ready 列的鈕可按,processing/failed 列停用並帶原因', async ({ page }) => {
    await mountDocuments(page)

    const readyBtn = askButton(page, READY_DOC.title)
    await expect(readyBtn).toBeEnabled()
    await expect(readyBtn).not.toHaveAttribute('title')

    const processingBtn = askButton(page, PROCESSING_DOC.title)
    await expect(processingBtn).toBeDisabled()
    await expect(processingBtn).toHaveAttribute('title', '文件仍在處理中,尚無法提問')

    const failedBtn = askButton(page, FAILED_DOC.title)
    await expect(failedBtn).toBeDisabled()
    await expect(failedBtn).toHaveAttribute('title', '文件處理失敗,無法提問')
  })

  // 副駕多數時候本來就開著（首次登入自動展開後常駐），所以「展開副駕」不是可見回饋；
  // 指示條才是使用者唯一看得到「現在問的是哪份文件」的訊號，也是唯一的清除入口。
  test('聚焦後出現指示條,× 可清除', async ({ page }) => {
    await mountDocuments(page)

    await expect(page.getByText('目前聚焦:')).toHaveCount(0)
    await askButton(page, READY_DOC.title).click()
    await expect(page.getByText(`目前聚焦:《${READY_DOC.title}》`)).toBeVisible()

    await page.getByRole('button', { name: '清除聚焦文件' }).click()
    await expect(page.getByText('目前聚焦:')).toHaveCount(0)
  })

  test('點 ready 列的「問這份文件」會展開副駕側欄', async ({ page }) => {
    await mountDocuments(page)

    const sidebar = page.getByTestId('copilot-sidebar')
    const window = sidebar.locator('.copilotKitWindow')
    // 首次登入的自動展開行為（見 copilotFirstLoginIntro.ui.spec.ts）與本測試無關,先收合成已知起始狀態。
    if (await window.evaluate((element) => element.classList.contains('open'))) {
      await sidebar.locator('.copilot-launcher').click()
      await expect(window).not.toHaveClass(/\bopen\b/)
    }

    await askButton(page, READY_DOC.title).click()

    await expect(window).toHaveClass(/\bopen\b/)
  })
})
