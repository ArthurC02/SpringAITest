import { expect, test, type Page, type Route } from '@playwright/test'

// C8/WS3 副駕首次登入引導（見 AppShell.tsx shouldOpenCopilotOnFirstLogin / storageKeys.ts
// COPILOT_INTRO_SHOWN_KEY）: localStorage 沒有該鍵時,`defaultOpen` 在掛載當下同步算出 true 並立刻
// 寫入鍵值(只在 render 階段做,不能用 useEffect,否則副駕已經用舊值掛載完了)；鍵已存在則尊重使用者
// 自己的收合狀態,不強制展開。launcher 按鈕本身無論開合都要有可讀文字標籤(不只是 aria-label)。

const INTRO_KEY = 'springai-copilot:introShown'

async function json(route: Route, body: unknown, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
}

async function mockApi(page: Page): Promise<void> {
  await page.route('**/api/**', async (route) => {
    const path = new URL(route.request().url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') {
      return json(route, { token: 'token', username: 'user', role: 'USER', tenantCode: 'demo', capabilities: [] })
    }
    if (path === '/api/features') return json(route, {})
    return json(route, [])
  })
}

async function login(page: Page): Promise<void> {
  await page.goto('/')
  await page.getByTestId('auth-username').fill('user')
  await page.getByTestId('auth-password').fill('password123')
  await page.getByTestId('auth-submit').click()
  await expect(page.getByTestId('session-identity')).toBeVisible()
}

function readIntroKey(page: Page): Promise<string | null> {
  return page.evaluate((key) => localStorage.getItem(key), INTRO_KEY)
}

test('localStorage 無 introShown 鍵時,登入後側欄自動展開且鍵被寫入', async ({ page }) => {
  await mockApi(page)
  await page.goto('/')
  // 新 context 預設沒有任何 localStorage,不需要額外清鍵(要在導覽後才能讀,about:blank 文件不允許讀 localStorage)。
  expect(await readIntroKey(page)).toBeNull()
  await page.getByTestId('auth-username').fill('user')
  await page.getByTestId('auth-password').fill('password123')
  await page.getByTestId('auth-submit').click()
  await expect(page.getByTestId('session-identity')).toBeVisible()

  const window = page.getByTestId('copilot-sidebar').locator('.copilotKitWindow')
  await expect(window).toHaveClass(/\bopen\b/)
  expect(await readIntroKey(page)).toBeTruthy()
})

test('localStorage 已有 introShown 鍵時,側欄保持收合', async ({ page }) => {
  await mockApi(page)
  await page.addInitScript((key) => {
    localStorage.setItem(key, '1')
  }, INTRO_KEY)
  await login(page)

  const window = page.getByTestId('copilot-sidebar').locator('.copilotKitWindow')
  await expect(window).not.toHaveClass(/\bopen\b/)
  expect(await readIntroKey(page)).toBe('1')
})

test('launcher 按鈕存在且帶可讀文字標籤', async ({ page }) => {
  await mockApi(page)
  await login(page)

  const launcher = page.getByTestId('copilot-sidebar').locator('.copilot-launcher')
  await expect(launcher).toBeVisible()
  await expect(launcher).toContainText('AI 助理')
})
