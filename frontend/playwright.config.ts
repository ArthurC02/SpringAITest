import { defineConfig, devices } from '@playwright/test'
import path from 'node:path'

// Full-compose is started by the release-evidence harness. This configuration deliberately
// does not start Vite or Docker: a missing browser/service is an outer-gate BLOCKED result,
// not a test that silently falls back to a mocked route.
const evidenceDir = path.resolve(process.env.EVIDENCE_DIR ?? 'test-results')

export default defineConfig({
  testDir: './e2e',
  fullyParallel: false,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  timeout: 60_000,
  expect: { timeout: 20_000 },
  outputDir: path.join(evidenceDir, 'playwright'),
  reporter: [
    ['line'],
    ['junit', { outputFile: path.join(evidenceDir, 'junit', 'playwright.xml') }],
  ],
  use: {
    baseURL: process.env.PW_BASE_URL ?? 'http://127.0.0.1:5173',
    // Playwright traces retain request headers, including Authorization. Safe evidence uses
    // redacted JSON attachments plus screenshots instead; never emit a reusable JWT trace.
    trace: 'off',
    screenshot: 'off',
    video: 'off',
  },
  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
    },
  ],
})
