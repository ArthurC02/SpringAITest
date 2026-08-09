import { expect, test, type Route } from '@playwright/test'

// E4 Evaluation cockpit (EvaluationPanel, mounted inside OperationsGovernanceView).
// Load-outcome classes (404-disabled / non-404 error / enabled-but-empty / enabled-with-data),
// the lazy suite+run drill-down, budget validation bounds, the client-side delta, and the
// trigger-run idempotency contract:
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
    page.getByText('此系統目前未開放評測功能。'),
  ).toBeVisible()
  await expect(page.locator('.toast')).toHaveCount(0)
  // A 404 is not a 401 — the session must survive, not bounce back to the login form.
  await expect(page.getByTestId('session-identity')).toBeVisible()
  await expect(page.getByTestId('auth-username')).toHaveCount(0)
})

test('a non-404 eval load failure renders the error line, never the "not enabled" empty state', async ({ page }) => {
  await page.route('**/api/**', async (route) => {
    const request = route.request()
    const path = new URL(request.url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') {
      return json(route, {
        token: 'eval-500-token',
        username: 'admin',
        role: 'ADMIN',
        tenantCode: 'demo',
        capabilities: ['workflow.manage'],
      })
    }
    if (path === '/api/features') return json(route, { agentWriteToolsEnabled: true })
    if (path === '/api/admin/operations/eval-suites') {
      return json(
        route,
        { timestamp: '2026-07-30T00:00:00Z', status: 500, message: 'eval store unavailable', fieldErrors: {} },
        500,
      )
    }
    return json(route, [])
  })

  await login(page)

  // Only `isNotFound` degrades to the disabled empty state — every other status rethrows and
  // must surface as a real error line, or a broken eval store would read as "flag is off".
  await expect(page.locator('.error-text')).toHaveText('eval store unavailable')
  await expect(
    page.getByText('此系統目前未開放評測功能。'),
  ).toHaveCount(0)
  // Read failures never toast — only write actions go through runWithToast.
  await expect(page.locator('.toast')).toHaveCount(0)
})

test('an enabled tenant with zero suites and zero runs shows both empty-list messages', async ({ page }) => {
  await page.route('**/api/**', async (route) => {
    const request = route.request()
    const path = new URL(request.url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') {
      return json(route, {
        token: 'eval-empty-token',
        username: 'admin',
        role: 'ADMIN',
        tenantCode: 'demo',
        capabilities: ['workflow.manage'],
      })
    }
    if (path === '/api/features') return json(route, { agentWriteToolsEnabled: true })
    // Enabled tenant with nothing recorded: both eval endpoints answer 200 [] like every route here.
    return json(route, [])
  })

  await login(page)

  await expect(page.getByText('尚無評測組合紀錄。')).toBeVisible()
  await expect(page.getByText('尚無評測執行紀錄。')).toBeVisible()
  // Empty (200 []) is the enabled-but-idle class, not the 404-disabled class.
  await expect(
    page.getByText('此系統目前未開放評測功能。'),
  ).toHaveCount(0)
})

test('expanding a suite row and a run row fetches detail; "Use for regression gate" pins the run id', async ({ page }) => {
  const runId = '3fa85f64-5717-4562-b3fc-2c963f66afa6'
  const suite = {
    suite_id: 'CSR-EVAL-001',
    current_revision: 2,
    created_at: '2026-01-01T00:00:00Z',
    updated_at: '2026-01-01T00:00:00Z',
  }
  const run = {
    id: runId,
    suite_id: 'CSR-EVAL-001',
    suite_revision: 2,
    candidate: { kind: 'skill', identity_sha256: 'abc' },
    runner_version: 'e2-runner-1',
    started_at: '2026-07-30T00:00:00Z',
    completed_at: '2026-07-30T00:00:05Z',
    pass_count: 1,
    fail_count: 1,
    error_count: 0,
    cases: null,
  }

  await page.route('**/api/**', async (route) => {
    const request = route.request()
    const path = new URL(request.url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') {
      return json(route, {
        token: 'eval-detail-token',
        username: 'admin',
        role: 'ADMIN',
        tenantCode: 'demo',
        capabilities: ['workflow.manage'],
      })
    }
    if (path === '/api/features') return json(route, { agentWriteToolsEnabled: true })
    if (path === '/api/admin/operations/eval-suites') return json(route, [suite])
    if (path === '/api/admin/operations/eval-suites/CSR-EVAL-001') {
      return json(route, {
        ...suite,
        revisions: [
          {
            revision: 2,
            cases_sha256: 'abc123def456789012',
            case_count: 6,
            created_by: 'admin',
            created_at: '2026-01-01T00:00:00Z',
          },
        ],
      })
    }
    if (path === '/api/admin/operations/eval-runs') return json(route, [run])
    if (path === `/api/admin/operations/eval-runs/${runId}`) {
      return json(route, {
        ...run,
        cases: [
          { case_id: 'case-1', canonical_identity: 'identity-1', verdict: 'PASS', metrics: { latency_ms: 120 }, failure_reason: null },
          { case_id: 'case-2', canonical_identity: 'identity-2', verdict: 'FAIL', metrics: { latency_ms: 80 }, failure_reason: 'mismatch' },
        ],
      })
    }
    return json(route, [])
  })

  await login(page)

  // SHA/case count only exist on the detail response — the list row must fetch them on expand.
  await page.getByRole('button', { name: /CSR-EVAL-001/ }).click()
  await expect(page.getByText('abc123def456', { exact: true })).toBeVisible()
  await expect(
    page.locator('.agent-test-console__summary div', { hasText: '案例數' }).locator('dd'),
  ).toHaveText('6')

  await page.getByRole('button', { name: /3fa85f64/ }).click()
  // The per-case table is nested inside the expanded run row, so `table table tr` picks the
  // case row itself rather than also matching the wrapper row that contains it.
  const failedCase = page.locator('table table tr', { hasText: 'case-2' })
  await expect(failedCase).toContainText('FAIL')
  await expect(failedCase).toContainText('80 ms')
  await expect(failedCase).toContainText('mismatch')

  // The drill-down hands the run id to RegressionPanel's E3 trusted-path field.
  await page.getByRole('button', { name: '套用到品質迴歸關卡' }).click()
  await expect(page.locator('#ops-eval-run-id')).toHaveValue(runId)
})

test('the optional budget field accepts only whole numbers in 1..300000 and blocks submit outside it', async ({ page }) => {
  const suite = {
    suite_id: 'CSR-EVAL-001',
    current_revision: 2,
    created_at: '2026-01-01T00:00:00Z',
    updated_at: '2026-01-01T00:00:00Z',
  }

  await page.route('**/api/**', async (route) => {
    const request = route.request()
    const path = new URL(request.url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') {
      return json(route, {
        token: 'eval-budget-token',
        username: 'admin',
        role: 'ADMIN',
        tenantCode: 'demo',
        capabilities: ['workflow.manage'],
      })
    }
    if (path === '/api/features') return json(route, { agentWriteToolsEnabled: true })
    if (path === '/api/admin/operations/eval-suites') return json(route, [suite])
    return json(route, [])
  })

  await login(page)
  await page.locator('#eval-suite').selectOption('CSR-EVAL-001')
  await page.locator('#eval-candidate-name').fill('kb-query')

  const budget = page.locator('#eval-budget')
  const budgetError = page.locator('#eval-budget-err')
  const runEval = page.getByRole('button', { name: '執行評測' })

  // Blank (omitted from the request) plus both inclusive bounds of MAX_BUDGET_MS = 300_000.
  for (const valid of ['', '1', '300000']) {
    await budget.fill(valid)
    await expect(budgetError).toHaveCount(0)
    await expect(runEval).toBeEnabled()
  }
  // Just outside each bound, plus a non-integer that Number.isFinite would have let through.
  for (const invalid of ['0', '300001', '1.5']) {
    await budget.fill(invalid)
    await expect(budgetError).toHaveText('預算(毫秒)必須是 1 到 300000 之間的整數。')
    await expect(runEval).toBeDisabled()
  }
})

test('selecting a baseline and candidate run renders the per-case delta and the cross-suite warning', async ({ page }) => {
  const baselineId = '11111111-1111-4111-8111-111111111111'
  const candidateId = '22222222-2222-4222-8222-222222222222'
  function run(id: string, suiteId: string, cases: unknown[]) {
    return {
      id,
      suite_id: suiteId,
      suite_revision: 1,
      candidate: { kind: 'skill', identity_sha256: 'abc' },
      runner_version: 'e2-runner-1',
      started_at: '2026-07-30T00:00:00Z',
      completed_at: '2026-07-30T00:00:05Z',
      pass_count: 1,
      fail_count: 1,
      error_count: 0,
      cases,
    }
  }
  const baseline = run(baselineId, 'CSR-EVAL-001', [
    { case_id: 'case-1', canonical_identity: 'identity-1', verdict: 'PASS', metrics: { latency_ms: 10 }, failure_reason: null },
    { case_id: 'case-2', canonical_identity: 'identity-2', verdict: 'FAIL', metrics: { latency_ms: 10 }, failure_reason: 'x' },
  ])
  const candidate = run(candidateId, 'CSR-EVAL-002', [
    { case_id: 'case-1', canonical_identity: 'identity-1', verdict: 'FAIL', metrics: { latency_ms: 12 }, failure_reason: 'broke' },
    { case_id: 'case-3', canonical_identity: 'identity-3', verdict: 'PASS', metrics: { latency_ms: 8 }, failure_reason: null },
  ])

  await page.route('**/api/**', async (route) => {
    const request = route.request()
    const path = new URL(request.url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') {
      return json(route, {
        token: 'eval-delta-token',
        username: 'admin',
        role: 'ADMIN',
        tenantCode: 'demo',
        capabilities: ['workflow.manage'],
      })
    }
    if (path === '/api/features') return json(route, { agentWriteToolsEnabled: true })
    if (path === '/api/admin/operations/eval-runs') {
      return json(route, [{ ...baseline, cases: null }, { ...candidate, cases: null }])
    }
    if (path === `/api/admin/operations/eval-runs/${baselineId}`) return json(route, baseline)
    if (path === `/api/admin/operations/eval-runs/${candidateId}`) return json(route, candidate)
    return json(route, [])
  })

  await login(page)
  await page.locator('#eval-baseline-run').selectOption(baselineId)
  await page.locator('#eval-candidate-run').selectOption(candidateId)

  // Comparison is client-side only and still runs across suites — it warns instead of refusing.
  await expect(
    page.getByText('所選執行來自不同組合(CSR-EVAL-001 對 CSR-EVAL-002)'),
  ).toBeVisible()
  await expect(page.locator('tr', { hasText: 'case-1' })).toContainText('退步(通過→失敗)')
  await expect(page.locator('tr', { hasText: 'case-2' })).toContainText('已移除案例')
  await expect(page.locator('tr', { hasText: 'case-3' })).toContainText('新案例')
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

  await page.getByRole('button', { name: '執行評測' }).click()
  await page.getByRole('dialog', { name: '確認操作' }).getByRole('button', { name: '執行' }).click()
  await expect(page.getByRole('alert')).toContainText('downstream outcome unknown')
  expect(requestKeys).toHaveLength(1)
  expect(requestKeys[0]).toBeTruthy()

  await page.getByRole('button', { name: '執行評測' }).click()
  await page.getByRole('dialog', { name: '確認操作' }).getByRole('button', { name: '執行' }).click()
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
