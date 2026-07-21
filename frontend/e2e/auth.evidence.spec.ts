import { randomUUID } from 'node:crypto'
import { expect, test } from '@playwright/test'
import {
  evidenceUsers,
  expectSensitiveBrowserStorageCleared,
  historyCount,
  logoutFromTopBar,
  preventCompetingDocument401,
  saveSafeScreenshot,
  seedInvalidSessionAndChatStorage,
  sendCopilotTurn,
  signIn,
  tokenFingerprint,
  waitForCompletedCopilotTurn,
} from './helpers/auth'

test.describe('E-01 browser authentication evidence', () => {
  test('account switch rebuilds the Copilot agent with B identity', async ({ page }, testInfo) => {
    const pageErrors: string[] = []
    page.on('pageerror', (error) => pageErrors.push(error.message))

    await signIn(page, evidenceUsers.a)
    const aMarker = `evidence-a-${randomUUID()}`
    const aTurn = await sendCopilotTurn(page, aMarker)
    expect(aTurn.responseStatus).toBe(200)
    const aFingerprint = await tokenFingerprint(aTurn.request)
    await waitForCompletedCopilotTurn(page)
    await saveSafeScreenshot(page, testInfo, 'copilot-user-a')

    await logoutFromTopBar(page)
    await expect(page.getByTestId('auth-page')).toBeVisible()
    await expectSensitiveBrowserStorageCleared(page)

    await signIn(page, evidenceUsers.b)
    await expect(page.getByTestId('session-identity')).not.toContainText(evidenceUsers.a.username)
    const bHistoryBefore = await historyCount(page)
    const bMarker = `evidence-b-${randomUUID()}`
    const bTurn = await sendCopilotTurn(page, bMarker)
    expect(bTurn.responseStatus).toBe(200)
    const bFingerprint = await tokenFingerprint(bTurn.request)
    expect(bFingerprint).not.toBe(aFingerprint)
    await waitForCompletedCopilotTurn(page)
    let bHistoryAfter = bHistoryBefore
    await expect
      .poll(async () => {
        bHistoryAfter = await historyCount(page)
        return bHistoryAfter
      })
      .toBeGreaterThan(bHistoryBefore)
    await saveSafeScreenshot(page, testInfo, 'copilot-user-b')

    await testInfo.attach('e01-account-switch.json', {
      contentType: 'application/json',
      body: Buffer.from(
        JSON.stringify({
          // Fingerprints prove an agent rebuild without retaining a reusable JWT.
          aCopilotAuthorizationSha256: aFingerprint,
          bCopilotAuthorizationSha256: bFingerprint,
          bScopedHistory: { before: bHistoryBefore, after: bHistoryAfter },
        }),
      ),
    })

    // pageerror is the browser's uncaught-exception signal. Ordinary non-2xx fetch handling
    // is asserted separately below and must not be hidden by this check.
    expect(pageErrors).toEqual([])
  })

  test('a real platform AG-UI 401 invokes the global logout and clears storage', async ({ page }, testInfo) => {
    const pageErrors: string[] = []
    page.on('pageerror', (error) => pageErrors.push(error.message))

    await signIn(page, evidenceUsers.a)
    await seedInvalidSessionAndChatStorage(page)
    await preventCompetingDocument401(page)
    await page.reload()
    await expect(page.getByTestId('session-identity')).toContainText(evidenceUsers.a.username)

    const invalidTurn = await sendCopilotTurn(page, `evidence-invalid-${randomUUID()}`)
    // This is intentionally not a route mock: the full-compose platform JWT middleware
    // must reject the malformed browser token.
    expect(invalidTurn.responseStatus).toBe(401)

    await expect(page.getByTestId('auth-page')).toBeVisible()
    await expectSensitiveBrowserStorageCleared(page)
    await saveSafeScreenshot(page, testInfo, 'copilot-agui-401-logout')
    await testInfo.attach('e01-agui-401.json', {
      contentType: 'application/json',
      body: Buffer.from(JSON.stringify({ platformAguiStatus: invalidTurn.responseStatus })),
    })
    expect(pageErrors).toEqual([])
  })
})
