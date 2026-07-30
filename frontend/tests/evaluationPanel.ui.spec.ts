import { expect, test, type Route } from '@playwright/test'

// E4 Evaluation cockpit (EvaluationPanel, mounted inside OperationsGovernanceView).
// Covers the two review gaps that had no frontend coverage:
// (a) RUN_EVAL_ENABLED off (404 from every eval-suites/eval-runs proxy route) must render a
//     plain empty state — never an error toast, and never a logout (404 is not 401).
// (b) A failed trigger-run POST must keep the same Idempotency-Key on retry; the eventual
//     success must consume it (cleared from sessionStorage) so a later, different attempt
//     mints a fresh one.

async function json(route: Route, body: unknown, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
}

async function login(page: import('@playwright/test').Page) {
  await page.goto('/')
  await page.getByTestId('auth-username').fill('admin')
  await page.getByTestId('auth-password').fill('password123')
  await page.getByTestId('auth-submit').click()
  await page.getByTestId('nav-operations').click()
}

test('Evaluation panel shows a disabled empty state on 404, with no error toast and no logout', async ({ page }) => {
  await page.route('**/api/**', async (route) => {
    const request = route.request()
    const path = new URL(request.url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') {
      return json(route, {
        token: 'eval-404-token',
        username: 'admin',
        role: 'ADMIN',
        tenantCode: 'demo',
        capabilities: ['workflow.manage'],
      })
    }
    if (path === '/api/features') return json(route, { agentWriteToolsEnabled: true })
    if (path === '/api/admin/operations/eval-suites' || path === '/api/admin/operations/eval-runs') {
      return json(
        route,
        { timestamp: '2026-07-30T00:00:00Z', status: 404, message: 'not found', fieldErrors: {} },
        404,
      )
    }
    return json(route, [])
  })

  await login(page)

  await expect(
    page.getByText('Evaluation is not enabled for this tenant (RUN_EVAL_ENABLED off).'),
  ).toBeVisible()
  await expect(page.locator('.toast')).toHaveCount(0)
  // A 404 is not a 401 — the session must survive, not bounce back to the login form.
  await expect(page.getByTestId('session-identity')).toBeVisible()
  await expect(page.getByTestId('auth-username')).toHaveCount(0)
})

test('a failed eval run trigger keeps the same Idempotency-Key on retry; success consumes it', async ({ page }) => {
  const suite = {
    suite_id: 'CSR-EVAL-001',
    current_revision: 2,
    created_at: '2026-01-01T00:00:00Z',
    updated_at: '2026-01-01T00:00:00Z',
  }
  const requestKeys: Array<string | undefined> = []
  let posts = 0

  await page.route('**/api/**', async (route) => {
    const request = route.request()
    const path = new URL(request.url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') {
      return json(route, {
        token: 'eval-trigger-token',
        username: 'admin',
        role: 'ADMIN',
        tenantCode: 'demo',
        capabilities: ['workflow.manage'],
      })
    }
    if (path === '/api/features') return json(route, { agentWriteToolsEnabled: true })
    if (path === '/api/admin/operations/eval-suites') return json(route, [suite])
    if (path === '/api/admin/operations/eval-runs' && request.method() === 'GET') return json(route, [])
    if (path === '/api/admin/operations/eval-runs' && request.method() === 'POST') {
      posts += 1
      requestKeys.push(request.headers()['idempotency-key'])
      if (posts === 1) {
        return json(
          route,
          { timestamp: '2026-07-30T00:00:00Z', status: 502, message: 'downstream outcome unknown', fieldErrors: {} },
          502,
        )
      }
      return json(route, {
        id: 'r1',
        suite_id: 'CSR-EVAL-001',
        suite_revision: 2,
        candidate: { kind: 'skill', identity_sha256: 'abc' },
        runner_version: 'v1',
        started_at: '2026-07-30T00:00:00Z',
        completed_at: '2026-07-30T00:00:05Z',
        pass_count: 1,
        fail_count: 0,
        error_count: 0,
        cases: null,
      })
    }
    return json(route, [])
  })

  await login(page)
  // `getByLabel('Suite')` is ambiguous with RegressionPanel's own "Suite" field further down
  // the same view — target the eval trigger form's select/input by id instead.
  await page.locator('#eval-suite').selectOption('CSR-EVAL-001')
  await page.locator('#eval-candidate-name').fill('kb-query')

  await page.getByRole('button', { name: 'Run eval' }).click()
  await page.getByRole('dialog', { name: '確認操作' }).getByRole('button', { name: 'Run' }).click()
  await expect(page.getByRole('alert')).toContainText('downstream outcome unknown')
  expect(requestKeys).toHaveLength(1)
  expect(requestKeys[0]).toBeTruthy()

  await page.getByRole('button', { name: 'Run eval' }).click()
  await page.getByRole('dialog', { name: '確認操作' }).getByRole('button', { name: 'Run' }).click()
  await expect.poll(() => requestKeys.length).toBe(2)
  expect(requestKeys[1]).toBe(requestKeys[0])

  await expect(page.locator('.toast--success')).toBeVisible()
  // consume() clears the stored attempt on success, so a later, differently-identified
  // attempt would mint a fresh key rather than replaying this one.
  const stored = await page.evaluate(() =>
    sessionStorage.getItem('springai-operations:eval-run-idempotency'),
  )
  expect(stored).toBeNull()
})
