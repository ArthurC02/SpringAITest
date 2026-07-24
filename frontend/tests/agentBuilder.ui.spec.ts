import { expect, test, type Route } from '@playwright/test'

const agentId = '11111111-1111-4111-8111-111111111111'

const agent = {
  id: agentId,
  slug: 'finance-agent',
  name: 'Finance Agent',
  description: 'Checks invoices',
  enabled: true,
  draft_version: 1,
  draft_validated_version: null,
  published_revision: null,
  created_at: '2026-07-24T00:00:00Z',
  updated_at: '2026-07-24T00:00:00Z',
  draft: {
    system_prompt: 'Review invoices carefully.',
    execution_roles: ['worker'],
    capabilities: ['analysis'],
    output_contract: { type: 'object' },
    audience: ['USER', 'ADMIN'],
    allowed_tools: [],
    skill_bindings: [],
    knowledge_sources: [],
    business_rules: { version: 1, rules: [] },
    runtime_limits: {
      max_tool_rounds: 3,
      max_context_rounds: 2,
      timeout_seconds: 90,
      token_budget: 4000,
      step_budget: 20,
    },
    runtime_workflow: {
      id: '00000000-0000-4000-8000-000000000001',
      revision: 1,
    },
  },
}

async function json(route: Route, body: unknown, headers: Record<string, string> = {}) {
  await route.fulfill({ status: 200, contentType: 'application/json', headers, body: JSON.stringify(body) })
}

test('Agent Builder honors governed catalogs, ETag, conflict lock, and dialog keyboard behavior', async ({
  page,
}) => {
  let draftSaveStatus = 200
  let validateIfMatch: string | null = null

  await page.route('**/api/**', async (route) => {
    const request = route.request()
    const path = new URL(request.url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') {
      return json(route, {
        token: 'ui-test-token',
        username: 'admin',
        role: 'ADMIN',
        tenantCode: 'demo',
      })
    }
    if (path === '/api/features') return json(route, { agentBuilderEnabled: true })
    if (path === '/api/documents' || path === '/api/chat/history') return json(route, [])
    if (path === '/api/agents' && request.method() === 'GET') return json(route, [agent])
    if (path === `/api/agents/${agentId}` && request.method() === 'GET') {
      return json(route, agent, { ETag: '"1"' })
    }
    if (path === `/api/agents/${agentId}/revisions`) return json(route, [])
    if (path === '/api/skills/catalog') {
      return json(route, [
        {
          name: 'builtin-rag',
          description: 'Repo builtin without persisted revision',
          required_role: 'USER',
          source: 'builtin',
          revision: 1,
          bindable: false,
          kind: 'flow',
        },
        {
          name: 'tenant-review',
          description: 'Tenant persisted Skill',
          required_role: 'USER',
          source: 'custom',
          revision: 3,
          bindable: true,
          kind: 'agentic',
        },
      ])
    }
    if (path === '/api/tools') {
      return json(route, [
        {
          name: 'retrieve',
          kind: 'http',
          description: 'Search authorized tenant knowledge',
          risk: 'read',
          returns: 'ranked passages',
        },
      ])
    }
    if (path === `/api/agents/${agentId}/validate`) {
      validateIfMatch = request.headers()['if-match'] ?? null
      return json(route, { valid: true, errors: [] })
    }
    if (path === `/api/agents/${agentId}/draft` && request.method() === 'PUT') {
      if (draftSaveStatus === 409) {
        return route.fulfill({
          status: 409,
          contentType: 'application/json',
          body: JSON.stringify({
            timestamp: '2026-07-24T00:00:00Z',
            status: 409,
            message: 'draft 版本衝突',
            fieldErrors: {},
          }),
        })
      }
      return json(route, agent, { ETag: '"2"' })
    }
    return json(route, [])
  })

  await page.goto('/')
  await page.getByTestId('auth-username').fill('admin')
  await page.getByTestId('auth-password').fill('password123')
  await page.getByTestId('auth-submit').click()
  await expect(page.getByTestId('nav-agents')).toBeVisible()
  await page.getByTestId('nav-agents').click()
  await page.getByRole('button', { name: '編輯' }).click()

  const builtin = page.locator('.agent-skills__row').filter({ hasText: 'builtin-rag' })
  const custom = page.locator('.agent-skills__row').filter({ hasText: 'tenant-review' })
  await expect(builtin.getByRole('checkbox')).toBeDisabled()
  await expect(builtin).toContainText('不可綁')
  await expect(custom.getByRole('checkbox')).toBeEnabled()

  const tool = page.locator('.agent-skills__row').filter({ hasText: 'retrieve' })
  await expect(tool).toContainText('Search authorized tenant knowledge')
  await expect(tool).toContainText('風險：read')
  await expect(page.getByLabel('Output contract（JSON object）')).toHaveValue(
    '{\n  "type": "object"\n}',
  )

  await page.getByRole('button', { name: '驗證', exact: true }).click()
  await expect.poll(() => validateIfMatch).toBe('"1"')

  const previewTrigger = page.getByRole('button', { name: '發布預覽' })
  await previewTrigger.click()
  const dialog = page.getByRole('dialog', { name: '發布預覽' })
  await expect(dialog).toBeVisible()
  await expect(dialog).toContainText('Agent revision')
  await expect(dialog).toContainText('r1')
  const cancel = dialog.getByRole('button', { name: '取消' })
  const publish = dialog.getByRole('button', { name: '確認發布' })
  await expect(cancel).toBeFocused()
  await cancel.press('Tab')
  await expect(publish).toBeFocused()
  await publish.press('Escape')
  await expect(dialog).toBeHidden()
  await expect(previewTrigger).toBeFocused()

  draftSaveStatus = 409
  const name = page.getByLabel('名稱')
  await name.fill('Changed name')
  await page.getByRole('button', { name: '儲存草稿' }).click()
  await expect(page.getByRole('alert')).toContainText('已被其他人更新')
  await expect(name).toBeDisabled()
  await expect(page.getByRole('button', { name: '驗證', exact: true })).toBeDisabled()
})

test('USER cannot see or enter the Agents workspace when the Builder flag is enabled', async ({
  page,
}) => {
  let featuresRequested = false
  let agentApiRequested = false

  await page.route('**/api/**', async (route) => {
    const request = route.request()
    const path = new URL(request.url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') {
      return json(route, {
        token: 'user-ui-test-token',
        username: 'user',
        role: 'USER',
        tenantCode: 'demo',
      })
    }
    if (path === '/api/features') {
      featuresRequested = true
      return json(route, { agentBuilderEnabled: true })
    }
    if (path.startsWith('/api/agents')) {
      agentApiRequested = true
      return route.fulfill({ status: 403, contentType: 'application/json', body: '{}' })
    }
    if (path === '/api/documents' || path === '/api/chat/history') return json(route, [])
    return json(route, [])
  })

  await page.goto('/')
  await page.getByTestId('auth-username').fill('user')
  await page.getByTestId('auth-password').fill('password123')
  await page.getByTestId('auth-submit').click()

  await expect.poll(() => featuresRequested).toBe(true)
  await expect(page.getByTestId('nav-agents')).toHaveCount(0)
  await expect(page.locator('.agents-workspace')).toHaveCount(0)
  expect(agentApiRequested).toBe(false)
})

test('Business Rule editor round-trips canonical AST and uses server validation/simulation', async ({
  page,
}) => {
  let currentAgent = structuredClone(agent)
  let savedRuleSet: unknown = null
  let simulationFacts: unknown = null
  let simulationRequests = 0
  let simulationStarted = false
  let releaseSimulation: (() => void) | undefined
  let validationRequests = 0
  let validationStarted = false
  let releaseValidation: (() => void) | undefined

  await page.route('**/api/**', async (route) => {
    const request = route.request()
    const path = new URL(request.url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') {
      return json(route, {
        token: 'rule-editor-token',
        username: 'admin',
        role: 'ADMIN',
        tenantCode: 'demo',
      })
    }
    if (path === '/api/features') return json(route, { agentBuilderEnabled: true })
    if (path === '/api/documents' || path === '/api/chat/history') return json(route, [])
    if (path === '/api/agents' && request.method() === 'GET') return json(route, [currentAgent])
    if (path === `/api/agents/${agentId}` && request.method() === 'GET') {
      return json(route, currentAgent, { ETag: `"${currentAgent.draft_version}"` })
    }
    if (path === `/api/agents/${agentId}/revisions`) return json(route, [])
    if (path === '/api/skills/catalog' || path === '/api/tools') return json(route, [])
    if (path === '/api/agents/catalog/rule-facts') {
      return json(route, {
        version: 1,
        gates: ['pre-action'],
        limits: { maxDepth: 3, maxNodes: 256, maxRules: 100 },
        facts: [
          {
            name: 'context.confidence',
            type: 'number',
            provenance: 'LLM-inferred',
            trustTier: 'inferred',
            gates: ['pre-action'],
            operators: ['lt', 'exists'],
            visibleValue: true,
          },
          {
            name: 'caller.tenant_id',
            type: 'string',
            provenance: 'system',
            trustTier: 'trusted',
            gates: ['pre-action'],
            operators: ['eq'],
            visibleValue: false,
          },
          {
            name: 'action.amount',
            type: 'decimal',
            provenance: 'system',
            trustTier: 'trusted',
            gates: ['pre-action'],
            operators: ['gt'],
            visibleValue: true,
            wireFormat: 'canonical-decimal-string',
          },
        ],
        operators: [
          {
            name: 'lt',
            compatibleFactTypes: ['number'],
            value: { kind: 'scalar', types: ['number'] },
          },
          {
            name: 'exists',
            compatibleFactTypes: ['number'],
            value: { kind: 'none', types: [] },
          },
          {
            name: 'eq',
            compatibleFactTypes: ['string'],
            value: { kind: 'scalar', types: ['string'] },
          },
          {
            name: 'gt',
            compatibleFactTypes: ['decimal'],
            value: { kind: 'scalar', types: ['number', 'decimal', 'integer'] },
          },
        ],
      })
    }
    if (path === '/api/agents/catalog/rule-actions') {
      return json(route, {
        actions: [
          {
            name: 'deny',
            decision: 'deny',
            precedence: 100,
            parameters: [{ name: 'reason', type: 'string', required: false }],
          },
          {
            name: 'require_context',
            decision: 'require_context',
            precedence: 50,
            parameters: [],
          },
        ],
      })
    }
    if (path === '/api/agents/rules/validate') {
      validationRequests += 1
      const body = request.postDataJSON()
      if (validationRequests === 2) {
        validationStarted = true
        await new Promise<void>((resolve) => {
          releaseValidation = resolve
        })
      }
      return json(route, {
        valid: true,
        canonicalRuleSet:
          validationRequests === 2
            ? {
                ...body.ruleSet,
                rules: body.ruleSet.rules.map((rule: Record<string, unknown>, index: number) =>
                  index === 0 ? { ...rule, name: 'STALE CANONICAL NAME' } : rule,
                ),
              }
            : body.ruleSet,
        errors: [],
      })
    }
    if (path === '/api/agents/rules/simulate') {
      simulationRequests += 1
      const body = request.postDataJSON()
      simulationFacts = body.facts
      if (simulationRequests === 2) {
        simulationStarted = true
        await new Promise<void>((resolve) => {
          releaseSimulation = resolve
        })
      }
      return json(route, {
        valid: true,
        errors: [],
        simulation: {
          decision: { action: 'require_context' },
          matchedRules: ['low-confidence'],
          trace: [{ path: 'rules[0].when', result: true }],
        },
      })
    }
    if (path === `/api/agents/${agentId}/draft` && request.method() === 'PUT') {
      const draft = request.postDataJSON()
      savedRuleSet = draft.business_rules
      currentAgent = {
        ...currentAgent,
        draft_version: currentAgent.draft_version + 1,
        draft,
      }
      return json(route, currentAgent, { ETag: `"${currentAgent.draft_version}"` })
    }
    return json(route, [])
  })

  await page.goto('/')
  await page.getByTestId('auth-username').fill('admin')
  await page.getByTestId('auth-password').fill('password123')
  await page.getByTestId('auth-submit').click()
  await page.getByTestId('nav-agents').click()
  await page.getByRole('button', { name: '編輯' }).click()

  await page.getByRole('button', { name: '低信心時要求更多 Context' }).click()
  const card = page.locator('.rule-card').first()
  await expect(card).toContainText('自然語言摘要（非執行權威）')
  await card.getByLabel('rules[0].when 類型').selectOption('all')
  await card.getByRole('button', { name: '＋ 新增條件' }).click()
  await page.getByText('進階：唯讀 canonical JSON').click()
  await expect(page.getByLabel('Business Rules canonical JSON')).toContainText('"all"')
  await expect(page.getByLabel('Business Rules canonical JSON')).toContainText('"onUnknown"')

  await page.getByRole('button', { name: '以正式 Validator 驗證' }).click()
  await expect(page.getByText('Business Rules 驗證通過。')).toBeVisible()

  await page.getByText('Simulator（使用正式 evaluator，不會呼叫真實工具）').click()
  await page.getByLabel('提供 context.confidence (number)').check()
  await page.locator('#sim-context\\.confidence').fill('0.4')
  await page.getByRole('button', { name: '執行模擬' }).click()
  await expect(page.locator('.rule-simulation-result')).toContainText('require_context')
  await expect(page.locator('.rule-simulation-result')).toContainText('low-confidence')
  expect(simulationFacts).toEqual({ 'context.confidence': 0.4 })
  await expect(page.locator('#sim-caller\\.tenant_id')).toHaveCount(0)

  await page.getByLabel('提供 action.amount (decimal)').check()
  await page.locator('#sim-action\\.amount').fill('9007199254740993.01')
  await expect(page.locator('.rule-simulation-result')).toHaveCount(0)
  const simulateButton = page.getByRole('button', { name: '執行模擬' })
  await simulateButton.click()
  await expect.poll(() => simulationStarted).toBe(true)
  expect(simulationFacts).toEqual({
    'context.confidence': 0.4,
    'action.amount': '9007199254740993.01',
  })
  const ruleName = card.locator('.rule-card__identity input').first()
  await ruleName.fill('Changed during simulation')
  releaseSimulation?.()
  await expect(simulateButton).toBeEnabled()
  await expect(page.locator('.rule-simulation-result')).toHaveCount(0)

  const validateButton = page.locator('.business-rules__actions button').nth(1)
  await validateButton.click()
  await expect.poll(() => validationStarted).toBe(true)
  await ruleName.fill('Changed during validation')
  releaseValidation?.()
  await expect(validateButton).toBeEnabled()
  await expect(page.locator('.business-rules .notice-text')).toHaveCount(0)
  await expect(ruleName).toHaveValue('Changed during validation')

  await page.getByRole('button', { name: '儲存草稿' }).click()
  await expect.poll(() => savedRuleSet).not.toBeNull()
  expect(savedRuleSet).toEqual(currentAgent.draft.business_rules)
  await expect(page.getByLabel('Business Rules canonical JSON')).toContainText('"all"')
})
