import { expect, test, type Page, type Route } from '@playwright/test'

// WS1-b 就緒通知跨視圖（見 DocumentReadyNotifier.tsx / useDocuments.onSettled）: DocumentReadyNotifier
// 掛在 ToastProvider 子樹、與視圖切換無關,文件 processing → ready 的轉態通知必須在任何視圖都能看到,
// 不是只有留在文件頁才跳。這裡驗證使用者留在聊天視圖(預設視圖,非文件頁)時仍收到 toast。
//
// dedup 斷言用 MutationObserver 在 React 掛載前(document.documentElement 一律先存在)就開始記錄每一次
// `.toast--success` 節點被加入 DOM 的瞬間文字內容,而不是事後看 DOM 快照——toast 3 秒後自動消失,
// 快照法無法分辨「只跳過一次」與「跳兩次、第一次已消失」。main.tsx 用 <StrictMode>,其初始 effect
// 在開發伺服器下會重播一次(見 chatHistory.ui.spec.ts 的同一備註),若 dedup 沒做好,同一次轉態
// 真的可能被通知兩次。

const DOC_ID = 'doc-settle-1'
const DOC_TITLE = '季度報告'

async function json(route: Route, body: unknown, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
}

async function recordToastLog(page: Page): Promise<void> {
  await page.addInitScript(() => {
    const w = window as unknown as { __toastLog: string[] }
    w.__toastLog = []
    const observer = new MutationObserver((mutations) => {
      for (const mutation of mutations) {
        for (const node of Array.from(mutation.addedNodes)) {
          if (node instanceof HTMLElement && node.classList.contains('toast--success')) {
            w.__toastLog.push(node.textContent ?? '')
          }
        }
      }
    })
    // `document` (not `document.documentElement`, which does not exist yet when an init
    // script runs — it is only attached once HTML parsing begins) so the very first toast
    // insertion is never missed.
    observer.observe(document, { childList: true, subtree: true })
  })
}

function toastLog(page: Page): Promise<string[]> {
  return page.evaluate(() => (window as unknown as { __toastLog: string[] }).__toastLog)
}

test('文件在非文件視圖 processing→ready 轉態時跳 toast 且不重複通知', async ({ page }) => {
  await recordToastLog(page)
  let docStatus: 'processing' | 'ready' = 'processing'

  await page.route('**/api/**', async (route) => {
    const path = new URL(route.request().url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') {
      return json(route, { token: 'token', username: 'user', role: 'USER', tenantCode: 'demo', capabilities: [] })
    }
    if (path === '/api/features') return json(route, {})
    if (path === '/api/documents' && route.request().method() === 'GET') {
      return json(route, [
        { id: DOC_ID, title: DOC_TITLE, status: docStatus, chunk_count: docStatus === 'ready' ? 4 : 0, created_at: '2026-07-25T00:00:00Z' },
      ])
    }
    return json(route, [])
  })

  await page.goto('/')
  await page.getByTestId('auth-username').fill('user')
  await page.getByTestId('auth-password').fill('password123')
  await page.getByTestId('auth-submit').click()
  await expect(page.getByTestId('session-identity')).toBeVisible()
  // 預設視圖是聊天(非文件視圖);明確點一下確保停留在此,呼應「使用者位於非文件視圖」的前提。
  await page.getByTestId('nav-chat').click()
  await expect(page.getByTestId('nav-chat')).toHaveAttribute('aria-current', 'page')

  // 第一次輪詢仍是 processing,不該有任何通知。
  await expect.poll(() => toastLog(page)).toEqual([])

  docStatus = 'ready'
  const expectedText = `文件「${DOC_TITLE}」已就緒，可以開始提問了`
  await expect(page.locator('.toast--success')).toContainText(expectedText)
  // 仍在聊天視圖,沒有切去文件頁——通知確實跨視圖可見。
  await expect(page.getByTestId('nav-chat')).toHaveAttribute('aria-current', 'page')

  // 再放過兩個輪詢週期(輪詢在無 processing 文件後就停止,但保留餘裕觀察是否重複觸發)。
  await page.waitForTimeout(4500)
  const log = await toastLog(page)
  // toast 節點的 textContent 還含收合鈕的「×」字元,用 includes 比對訊息本體。
  expect(log.filter((text) => text.includes(expectedText))).toHaveLength(1)
})
