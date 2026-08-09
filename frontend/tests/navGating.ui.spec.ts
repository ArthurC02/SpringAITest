import { expect, test, type Page, type Route } from '@playwright/test'

// Sidebar/view gating decision table for the feature flags that had no frontend coverage:
// D4 `workflowDesignerEnabled` x exact `workflow.manage`, D7 `agentWriteToolsEnabled`,
// D6 `agentChatEnabled`, D5 `multiAgentDispatchEnabled`, and the fail-closed catch when
// `GET /api/features` rejects. Authority is `src/components/AppShell.tsx`.
// UI hiding is UX, not security — but every case also asserts the gated API is never called.
//
// Every scenario carries a `control` entry that its flags DO open. Without one, a negative
// assertion would already be satisfied by the pre-response render (all flags start false),
// i.e. it would pass without the features response ever having been applied.

const CHAT = /聊天/
const DOCUMENTS = /文件/
const ANALYSIS = /分析/
const CONFIG = /系統設定/
// W4:功能開通狀態頁,adminOnly 側欄過濾(比照系統設定),不受任何 feature flag 影響。
const FEATURES = /功能開通狀態/
// D1/D4 now share one sidebar entry; the per-tab flag+capability gating lives inside
// `AgentPlatformView` and is asserted by the tab-level cases at the bottom of this file.
const AGENT_PLATFORM = /Agent 平台/
const APPROVALS = /Approvals/
const OPERATIONS = /Operations/

const ADMIN_BASE = [CHAT, DOCUMENTS, ANALYSIS, CONFIG, FEATURES]
const USER_BASE = [CHAT, DOCUMENTS, ANALYSIS]

interface Scenario {
  name: string
  role?: 'ADMIN' | 'USER'
  capabilities: string[]
  features: Record<string, boolean> | 'reject'
  expected: RegExp[]
  /** Nav entry proving the features response was applied before the negatives are checked. */
  control: string | null
  /** Path prefixes that must never be requested for this combination. */
  forbidden: string[]
}

async function json(route: Route, body: unknown, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
}

async function mountShell(
  page: Page,
  scenario: Pick<Scenario, 'role' | 'capabilities' | 'features'>,
): Promise<string[]> {
  const requested: string[] = []
  await page.route('**/api/**', async (route) => {
    const path = new URL(route.request().url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    requested.push(path)
    if (path === '/api/auth/login') {
      return json(route, {
        token: 'token',
        username: 'tester',
        role: scenario.role ?? 'ADMIN',
        tenantCode: 'demo',
        capabilities: scenario.capabilities,
      })
    }
    if (path === '/api/features') {
      return scenario.features === 'reject'
        ? json(route, { timestamp: '2026-07-25T00:00:00Z', status: 500, message: 'features 無法取得', fieldErrors: {} }, 500)
        : json(route, scenario.features)
    }
    return json(route, [])
  })

  await page.goto('/')
  await page.getByTestId('auth-username').fill('tester')
  await page.getByTestId('auth-password').fill('password123')
  await page.getByTestId('auth-submit').click()
  return requested
}

const scenarios: Scenario[] = [
  {
    name: 'exact workflow.manage opens the D4 entries',
    capabilities: ['workflow.manage'],
    features: { workflowDesignerEnabled: true, agentWriteToolsEnabled: true },
    expected: [...ADMIN_BASE, AGENT_PLATFORM, APPROVALS, OPERATIONS],
    control: 'nav-agentPlatform',
    forbidden: [],
  },
  {
    name: 'ADMIN alone never opens the D4 entries',
    capabilities: [],
    features: { workflowDesignerEnabled: true, agentWriteToolsEnabled: true },
    expected: [...ADMIN_BASE, APPROVALS],
    control: 'nav-approvals',
    forbidden: ['/api/admin/workflows', '/api/admin/orchestrators'],
  },
  {
    name: 'a capability that merely starts with workflow.manage is not a match',
    capabilities: ['workflow.manage.other'],
    features: { workflowDesignerEnabled: true, agentWriteToolsEnabled: true },
    expected: [...ADMIN_BASE, APPROVALS],
    control: 'nav-approvals',
    forbidden: ['/api/admin/workflows', '/api/admin/orchestrators'],
  },
  {
    name: 'the disabled D4 flag closes the entries even with the capability',
    capabilities: ['workflow.manage'],
    features: { workflowDesignerEnabled: false, agentWriteToolsEnabled: true },
    expected: [...ADMIN_BASE, APPROVALS, OPERATIONS],
    control: 'nav-operations',
    forbidden: ['/api/admin/workflows', '/api/admin/orchestrators'],
  },
  {
    name: 'disabled write tools hide both Approvals and Operations',
    capabilities: ['workflow.manage'],
    features: { workflowDesignerEnabled: true, agentWriteToolsEnabled: false },
    expected: [...ADMIN_BASE, AGENT_PLATFORM],
    control: 'nav-agentPlatform',
    forbidden: ['/api/runs', '/api/admin/operations'],
  },
  {
    name: 'enabled write tools give a plain USER Approvals but never Operations',
    role: 'USER',
    capabilities: [],
    features: { agentWriteToolsEnabled: true },
    expected: [...USER_BASE, APPROVALS],
    control: 'nav-approvals',
    forbidden: ['/api/admin/operations'],
  },
  {
    // Operations is gated on `agentWriteToolsEnabled && canManageWorkflow` with no role check,
    // so the capability alone opens it — the USER counterpart of the scenario above.
    name: 'workflow.manage opens Operations for a plain USER too',
    role: 'USER',
    capabilities: ['workflow.manage'],
    features: { agentWriteToolsEnabled: true },
    expected: [...USER_BASE, APPROVALS, OPERATIONS],
    control: 'nav-operations',
    forbidden: ['/api/admin/workflows', '/api/admin/orchestrators'],
  },
  {
    name: 'the disabled Agent Builder flag hides the workspace from an ADMIN',
    capabilities: [],
    features: { agentBuilderEnabled: false, agentWriteToolsEnabled: true },
    expected: [...ADMIN_BASE, APPROVALS],
    control: 'nav-approvals',
    forbidden: ['/api/agents'],
  },
  {
    name: 'a rejected features response fails closed on every flag',
    capabilities: ['workflow.manage'],
    features: 'reject',
    expected: ADMIN_BASE,
    control: null,
    forbidden: ['/api/agents', '/api/admin/workflows', '/api/admin/orchestrators', '/api/admin/operations', '/api/chat/orchestrators'],
  },
]

for (const scenario of scenarios) {
  test(`sidebar gating — ${scenario.name}`, async ({ page }) => {
    const requested = await mountShell(page, scenario)
    if (scenario.control) {
      await expect(page.getByTestId(scenario.control)).toBeVisible()
    } else {
      // A rejected response opens nothing, so there is no positive marker: wait until the
      // request was answered and give the catch branch a render before asserting absence.
      await expect.poll(() => requested.includes('/api/features')).toBe(true)
      await page.waitForTimeout(300)
    }
    await expect(page.locator('nav[aria-label="主選單"] button')).toHaveText(scenario.expected)
    for (const prefix of scenario.forbidden) {
      expect(requested.filter((path) => path.startsWith(prefix)), prefix).toEqual([])
    }
  })
}

// The merged workspace keeps three independent gates, so the two capability/role combinations
// that each open exactly one tab are the ones that could silently leak the other side.
test('a non-ADMIN with workflow.manage gets the D4 tabs but never the Agents tab', async ({ page }) => {
  const requested = await mountShell(page, {
    role: 'USER',
    capabilities: ['workflow.manage'],
    features: { agentBuilderEnabled: true, workflowDesignerEnabled: true },
  })
  await page.getByTestId('nav-agentPlatform').click()
  await expect(page.getByTestId('agent-platform-tab-workflows')).toBeVisible()
  await expect(page.getByTestId('agent-platform-tab-orchestrators')).toBeVisible()
  await expect(page.getByTestId('agent-platform-tab-agents')).toHaveCount(0)
  expect(requested.filter((path) => path.startsWith('/api/agents'))).toEqual([])
})

test('an ADMIN without workflow.manage gets only the Agents tab and no tab bar', async ({ page }) => {
  const requested = await mountShell(page, {
    capabilities: [],
    features: { agentBuilderEnabled: true, workflowDesignerEnabled: true },
  })
  await page.getByTestId('nav-agentPlatform').click()
  await expect.poll(() => requested.includes('/api/agents')).toBe(true)
  // A single visible tab means there is no real choice — the segmented control is not rendered.
  await expect(page.locator('.seg')).toHaveCount(0)
  expect(requested.filter((path) => path.startsWith('/api/admin/workflows'))).toEqual([])
  expect(requested.filter((path) => path.startsWith('/api/admin/orchestrators'))).toEqual([])
})

test('a disabled agentChatEnabled flag never requests the Orchestrator chat catalog', async ({ page }) => {
  const requested = await mountShell(page, {
    capabilities: [],
    features: { agentChatEnabled: false, agentWriteToolsEnabled: true },
  })
  await expect(page.getByTestId('nav-approvals')).toBeVisible()
  await expect(page.getByLabel('選擇協作 Orchestrator')).toHaveCount(0)
  // The global flag is the first gate: a non-canary deployment must not even attempt the
  // authenticated catalog route, so a 404 never has to be relied on for legacy behaviour.
  expect(requested.filter((path) => path === '/api/chat/orchestrators')).toEqual([])
})

const orchestratorWire = {
  id: 'o1',
  name: 'Root',
  description: 'Published collaboration runtime',
  enabled: true,
  draft_version: 1,
  published_revision: 1,
  updated_at: '2026-07-25T00:00:00Z',
  definition: { workflow: { id: 'w1', revision: 1 } },
}

// The console is `multiAgentDispatchEnabled && item.enabled`, so each factor gets its own
// false case: a soft-disabled Orchestrator stays console-free even on a D5 deployment.
const d5Cases = [
  { multiAgentDispatchEnabled: true, enabled: true, consoles: 1 },
  { multiAgentDispatchEnabled: false, enabled: true, consoles: 0 },
  { multiAgentDispatchEnabled: true, enabled: false, consoles: 0 },
]

for (const { multiAgentDispatchEnabled, enabled, consoles } of d5Cases) {
  test(`D5 test-run console follows multiAgentDispatchEnabled=${multiAgentDispatchEnabled} enabled=${enabled}`, async ({ page }) => {
    const wire = { ...orchestratorWire, enabled }
    let runApiRequested = false
    await page.route('**/api/**', async (route) => {
      const path = new URL(route.request().url()).pathname
      if (!path.startsWith('/api/')) return route.continue()
      if (path === '/api/auth/login') {
        return json(route, {
          token: 'token', username: 'tester', role: 'ADMIN', tenantCode: 'demo',
          capabilities: ['workflow.manage'],
        })
      }
      if (path === '/api/features') {
        return json(route, { workflowDesignerEnabled: true, multiAgentDispatchEnabled })
      }
      if (path === '/api/admin/orchestrators') return json(route, [wire])
      if (path === '/api/admin/orchestrators/o1') return json(route, wire)
      if (path.includes('/runs')) {
        runApiRequested = true
        return json(route, {}, 404)
      }
      return json(route, [])
    })

    await page.goto('/')
    await page.getByTestId('auth-username').fill('tester')
    await page.getByTestId('auth-password').fill('password123')
    await page.getByTestId('auth-submit').click()
    await page.getByTestId('nav-agentPlatform').click()
    await page.getByTestId('agent-platform-tab-orchestrators').click()
    await page.getByRole('button', { name: '編輯' }).click()
    // Reaching the editor already proves the flags resolved, and the draft-loaded actions
    // prove the editor body rendered — so the console check sees a settled tree.
    await expect(page.getByRole('heading', { name: 'Root', level: 2 })).toBeVisible()
    await expect(page.getByRole('button', { name: '驗證' })).toBeVisible()

    await expect(page.locator('.agent-test-console')).toHaveCount(consoles)
    expect(runApiRequested).toBe(false)
  })
}
