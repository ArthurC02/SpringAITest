import { defineConfig } from '@playwright/test'

/** 使用 mock API 的瀏覽器 UI/API regression tests。 */
export default defineConfig({
  testDir: './tests',
  testIgnore: '**/*.unit.spec.ts',
  fullyParallel: true,
  reporter: 'line',
  use: {
    baseURL: 'http://127.0.0.1:4174',
  },
  webServer: {
    command: 'npm run dev -- --host 127.0.0.1 --port 4174',
    url: 'http://127.0.0.1:4174',
    reuseExistingServer: true,
    timeout: 30_000,
  },
})
