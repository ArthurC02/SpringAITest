import { createHash } from 'node:crypto'
import { expect, type Page, type Request, type TestInfo } from '@playwright/test'

const sessionKey = 'springai:session'
const chatKeys = [
  'springai-chat:messages',
  'springai-chat:conversationId',
  'springai-chat:userId',
]

export interface Credentials {
  username: string
  password: string
  tenantCode: string
}

export const evidenceUsers = {
  a: {
    username: process.env.EVIDENCE_USER_A ?? 'user-a',
    password: process.env.EVIDENCE_PASSWORD_A ?? 'password123',
    tenantCode: process.env.EVIDENCE_TENANT_A ?? 'demo-a',
  },
  b: {
    username: process.env.EVIDENCE_USER_B ?? 'user-b',
    password: process.env.EVIDENCE_PASSWORD_B ?? 'password123',
    tenantCode: process.env.EVIDENCE_TENANT_B ?? 'demo-b',
  },
} satisfies Record<'a' | 'b', Credentials>

export async function signIn(page: Page, credentials: Credentials): Promise<void> {
  await page.goto('/')
  await expect(page.getByTestId('auth-page')).toBeVisible()
  await page.getByTestId('auth-username').fill(credentials.username)
  await page.getByTestId('auth-password').fill(credentials.password)
  await page.getByTestId('auth-submit').click()
  await expect(page.getByTestId('session-identity')).toContainText(
    `${credentials.username} @ ${credentials.tenantCode}`,
  )
}

export async function openCopilot(page: Page): Promise<void> {
  const sidebar = page.getByTestId('copilot-sidebar')
  const input = sidebar.getByTestId('copilot-chat-textarea')
  const window = sidebar.locator('.copilotKitWindow')
  if (!(await window.evaluate((element) => element.classList.contains('open')))) {
    await sidebar.locator('.copilot-launcher').click()
    await expect(window).toHaveClass(/\bopen\b/)
  }
  await expect(input).toBeVisible()
}

/**
 * CopilotKit's development inspector occupies the top-right corner in a dev build. Close the
 * visible sidebar first, then use the native keyboard activation path for the top-bar button;
 * this remains an accessibility-valid user interaction without force-clicking through an
 * overlay.
 */
export async function closeCopilot(page: Page): Promise<void> {
  const sidebar = page.getByTestId('copilot-sidebar')
  const window = sidebar.locator('.copilotKitWindow')
  if (await window.evaluate((element) => element.classList.contains('open'))) {
    const closeButton = sidebar.getByRole('button', { name: 'Close', exact: true })
    await closeButton.press('Enter')
    await expect(window).not.toHaveClass(/\bopen\b/)
  }
}

export async function logoutFromTopBar(page: Page): Promise<void> {
  await closeCopilot(page)
  const logout = page.getByTestId('logout-button')
  await logout.focus()
  await expect(logout).toBeFocused()
  await logout.press('Enter')
}

/**
 * AppShell fetches documents, and the default chat view eagerly loads chat history,
 * immediately after a reload. Both are ordinary authenticated apiFetch calls, so with an
 * invalid session they would 401 and trigger the global logout before the test's own
 * intentional AG-UI call does. Abort only these unrelated requests so a malformed session is
 * exercised by the real AG-UI middleware rather than racing a generic apiFetch 401. The target
 * `/api/copilot/agui` request is never intercepted or fulfilled.
 */
export async function preventCompetingDocument401(page: Page): Promise<void> {
  await page.route('**/api/documents', (route) => route.abort('failed'))
  await page.route('**/api/chat/history/page**', (route) => route.abort('failed'))
}

/**
 * Sends a real request through CopilotKit's HttpAgent. The request is observed only in
 * memory; callers may retain the SHA-256 fingerprint but must never attach its header.
 */
export async function sendCopilotTurn(
  page: Page,
  message: string,
): Promise<{ request: Request; responseStatus: number }> {
  await openCopilot(page)
  const requestPromise = page.waitForRequest(
    (request) => request.url().includes('/api/copilot/agui') && request.method() === 'POST',
  )
  const responsePromise = page.waitForResponse(
    (response) => response.url().includes('/api/copilot/agui') && response.request().method() === 'POST',
  )

  const sidebar = page.getByTestId('copilot-sidebar')
  await sidebar.getByTestId('copilot-chat-textarea').fill(message)
  await sidebar.getByTestId('copilot-chat-textarea').press('Enter')

  const [request, response] = await Promise.all([requestPromise, responsePromise])
  return {
    request,
    responseStatus: response.status(),
  }
}

export async function waitForCompletedCopilotTurn(page: Page): Promise<void> {
  const sidebar = page.getByTestId('copilot-sidebar')
  const assistantMessages = sidebar.locator('.copilotKitAssistantMessage')
  await expect.poll(async () => assistantMessages.count()).toBeGreaterThan(0)
  await expect.poll(async () => (await assistantMessages.last().textContent())?.trim().length ?? 0).toBeGreaterThan(0)
}

export async function tokenFingerprint(request: Request): Promise<string> {
  const authorization = await request.headerValue('authorization')
  // Deliberately do not interpolate authorization in a failure message or artifact.
  expect(authorization, 'Copilot request must carry an Authorization header').toBeTruthy()
  return createHash('sha256').update(authorization!).digest('hex')
}

export async function historyCount(page: Page): Promise<number> {
  return page.evaluate(async (key) => {
    const rawSession = localStorage.getItem(key)
    if (!rawSession) throw new Error('No active browser session')
    const { token } = JSON.parse(rawSession) as { token: string }
    const response = await fetch('/api/chat/history', {
      headers: { Authorization: `Bearer ${token}` },
    })
    if (!response.ok) throw new Error(`History request failed with HTTP ${response.status}`)
    const history = (await response.json()) as unknown[]
    return history.length
  }, sessionKey)
}

export async function seedInvalidSessionAndChatStorage(page: Page): Promise<void> {
  await page.evaluate(
    ({ key, keys }) => {
      const rawSession = localStorage.getItem(key)
      if (!rawSession) throw new Error('Cannot replace a missing browser session')
      const session = JSON.parse(rawSession) as Record<string, unknown>
      // This intentionally malformed token must reach the real platform middleware after reload.
      session.token = 'evidence-invalid-token'
      localStorage.setItem(key, JSON.stringify(session))
      for (const chatKey of keys) localStorage.setItem(chatKey, 'evidence-stale-state')
    },
    { key: sessionKey, keys: chatKeys },
  )
}

export async function expectSensitiveBrowserStorageCleared(page: Page): Promise<void> {
  await expect
    .poll(() =>
      page.evaluate(
        ({ key, keys }) => [localStorage.getItem(key), ...keys.map((chatKey) => localStorage.getItem(chatKey))],
        { key: sessionKey, keys: chatKeys },
      ),
    )
    .toEqual([null, null, null, null])
}

export async function saveSafeScreenshot(
  page: Page,
  testInfo: TestInfo,
  name: string,
): Promise<void> {
  // The application never renders tokens. Keep screenshots limited to the login or visible
  // Copilot state and leave artifact secret scanning to the outer evidence harness.
  await page.screenshot({ path: testInfo.outputPath('screenshots', `${name}.png`), fullPage: true })
}
