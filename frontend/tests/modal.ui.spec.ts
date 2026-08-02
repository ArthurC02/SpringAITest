import { expect, test } from '@playwright/test'

test('restores focus after a busy modal closes and its trigger becomes enabled', async ({ page }) => {
  await page.goto('/')
  await page.evaluate(async () => {
    const { mountModalHarness } = await import('/tests/modalHarness.tsx')
    mountModalHarness()
  })

  const trigger = page.getByTestId('trigger')
  await trigger.click()
  await page.getByRole('button', { name: 'Finish' }).click()
  await expect(trigger).toBeDisabled()
  await expect(trigger).not.toBeFocused()
  await page.getByRole('button', { name: 'Release' }).click()
  await expect(trigger).toBeEnabled()
  await expect(trigger).toBeFocused()
})
