// Audience principals are validated against a regex that is duplicated verbatim from the server:
// `src/agentBuilder.ts:19` AUDIENCE_GROUP_ID === platform `src/Platform.Service/Dtos/UserContext.cs:34`
// CanonicalGroupIdRegex, whose 126-char middle class also encodes UserGroupContract.MaxGroupIdLength = 128.
// The frontend is not the authority; these boundary cases exist so that relaxing/tightening the
// server rule without updating this copy fails here instead of silently blocking legal authoring.
import { expect, test } from 'vitest'
import {
  audiencePrincipalError,
  businessRuleCount,
  createEmptyAgentDraft,
  isSkillBindable,
  nextAgentRevision,
  normalizeAgentDraft,
  normalizeAudiencePrincipals,
  parseOutputContract,
} from '../src/agentBuilder'
import {
  putAgentDraft,
  simulateBusinessRules,
  validateAgent,
  validateBusinessRules,
} from '../src/api/agents'
import { ApiError } from '../src/api/http'
import {
  actionCatalogItems,
  canonicalRulesFromValidation,
  createRuleAction,
  defaultTypedValue,
  factCatalogItems,
  factsForGate,
  operatorEntries,
  ruleUiDepthLimit,
  summarizeRule,
} from '../src/ruleBuilder'
import type {
  AgentBusinessRules,
  RuleActionCatalogEntry,
  RuleFactCatalogEntry,
  SkillCatalogEntry,
} from '../src/types'

test.describe('Agent Builder model contracts', () => {
  test('merges response identity with canonical draft and preserves governed fields', () => {
    const normalized = normalizeAgentDraft(
      {
        ...createEmptyAgentDraft(),
        name: 'wrong nested name',
        slug: 'wrong-nested-slug',
        description: 'wrong nested description',
        capabilities: ['analysis'],
        audience: ['USER', 'ADMIN'],
        output_contract: { type: 'object' },
        runtime_limits: {
          max_tool_rounds: 3,
          max_context_rounds: 2,
          timeout_seconds: 90,
          token_budget: 4000,
          step_budget: 20,
        },
        runtime_workflow: { id: 'workflow-id', revision: 4 },
      },
      { name: 'Finance Agent', slug: 'finance-agent', description: 'Checks invoices' },
    )

    expect(normalized.name).toBe('Finance Agent')
    expect(normalized.slug).toBe('finance-agent')
    expect(normalized.description).toBe('Checks invoices')
    expect(normalized.capabilities).toEqual(['analysis'])
    expect(normalized.audience).toEqual(['role:USER', 'role:ADMIN'])
    expect(normalized.output_contract).toEqual({ type: 'object' })
    expect(normalized.runtime_limits.timeout_seconds).toBe(90)
    expect(normalized.runtime_workflow).toEqual({ id: 'workflow-id', revision: 4 })
  })

  test('defaults new agents to explicit fail-closed sets, tenant roles, and revision r1', () => {
    const draft = createEmptyAgentDraft()
    expect(draft.allowed_tools).toEqual([])
    expect(draft.knowledge_sources).toEqual([])
    expect(draft.capabilities).toEqual([])
    expect(draft.audience).toEqual(['role:USER', 'role:ADMIN'])
    expect(businessRuleCount(draft.business_rules)).toBe(0)
    // A never-published Agent previews r1, not r0; an existing one previews published + 1.
    expect(nextAgentRevision(null)).toBe(1)
    expect(nextAgentRevision(4)).toBe(5)
  })

  test('uses server bindable metadata instead of source/name guesses', () => {
    const base: SkillCatalogEntry = {
      name: 'same-name',
      description: '',
      required_role: 'USER',
      source: 'custom',
      revision: 2,
      bindable: false,
    }
    expect(isSkillBindable(base)).toBe(false)
    expect(isSkillBindable({ ...base, source: 'builtin', bindable: true })).toBe(true)
  })

  test('accepts only JSON objects for output contracts', () => {
    expect(parseOutputContract('{"type":"object"}')).toEqual({
      value: { type: 'object' },
      error: null,
    })
    expect(parseOutputContract('[]').value).toBeNull()
    expect(parseOutputContract('{').error).toBeTruthy()
  })

  test('normalizes legacy role principals and validates canonical role/group audiences', () => {
    expect(
      normalizeAudiencePrincipals([
        ' USER ',
        'role:admin',
        'group:finance-reviewers',
        'role:USER',
      ]),
    ).toEqual(['role:USER', 'role:ADMIN', 'group:finance-reviewers'])
    expect(
      audiencePrincipalError(['role:USER', 'role:ADMIN', 'group:finance-reviewers']),
    ).toBeNull()
    expect(audiencePrincipalError(['USER'])).toContain('格式錯誤')
    expect(audiencePrincipalError(['group:*'])).toContain('wildcard')
    expect(audiencePrincipalError(['group:Finance Reviewers'])).toContain('格式錯誤')
    expect(audiencePrincipalError(['role:OWNER'])).toContain('格式錯誤')
    // An empty audience is deliberately not a client-side error: the server owns "who may see this".
    expect(audiencePrincipalError([])).toBeNull()
    // Group id length boundary: the shared regex admits 1 + 126 + 1 = 128 characters, matching
    // platform's UserGroupContract.MaxGroupIdLength. 129 must fail on both sides.
    expect(audiencePrincipalError([`group:${'a'.repeat(128)}`])).toBeNull()
    expect(audiencePrincipalError([`group:${'a'.repeat(129)}`])).toContain('格式錯誤')
    // A single character is the shortest legal id; separators may not sit on either edge.
    expect(audiencePrincipalError(['group:a'])).toBeNull()
    expect(audiencePrincipalError(['group:-finance'])).toContain('格式錯誤')
    expect(audiencePrincipalError(['group:finance-'])).toContain('格式錯誤')
  })

  test('preserves canonical Business Rule AST including nested groups and unknown handling', () => {
    const businessRules: AgentBusinessRules = {
      version: 1,
      rules: [
        {
          id: 'refund',
          name: 'Refund approval',
          enabled: true,
          priority: 100,
          when: {
            all: [
              { fact: 'action.type', op: 'eq', value: 'refund' },
              {
                any: [
                  { fact: 'action.amount', op: 'gt', value: '5000' },
                  { not: { fact: 'caller.role', op: 'eq', value: 'ADMIN' } },
                ],
              },
            ],
          },
          then: [{ action: 'require_approval', role: 'ADMIN' }],
          onUnknown: [{ action: 'deny', reason: 'missing fact' }],
        },
      ],
    }
    const normalized = normalizeAgentDraft(
      { ...createEmptyAgentDraft(), business_rules: businessRules },
      { name: 'Agent', slug: 'agent', description: '' },
    )
    expect(normalized.business_rules).toEqual(businessRules)
  })
})

test.describe('Business Rule catalog-driven rendering helpers', () => {
  const fact: RuleFactCatalogEntry = {
    name: 'action.amount',
    type: 'decimal',
    provenance: 'system',
    trustTier: 'trusted',
    gates: ['pre-action'],
    operators: ['gt', 'between'],
  }
  const factsResponse = {
    gates: ['preflight', 'pre-action'],
    facts: [fact],
    operators: [
      {
        name: 'gt',
        compatibleFactTypes: ['decimal'],
        value: { kind: 'scalar', types: ['number', 'decimal', 'integer'] },
      },
      {
        name: 'between',
        compatibleFactTypes: ['decimal'],
        value: { kind: 'range', types: ['number', 'decimal', 'integer'] },
      },
    ],
  }
  const actions: RuleActionCatalogEntry[] = [
    {
      name: 'require_approval',
      parameters: [{ name: 'role', type: 'string', required: true }],
    },
  ]

  test('uses gate and operator metadata without frontend evaluator semantics', () => {
    expect(factCatalogItems(factsResponse)).toEqual([fact])
    expect(factsForGate([fact], 'preflight')).toEqual([])
    expect(operatorEntries(fact, factsResponse)).toEqual([
      expect.objectContaining({ name: 'gt', value_type: 'decimal', value_count: 1 }),
      expect.objectContaining({ name: 'between', value_type: 'decimal', value_count: 2 }),
    ])
    expect(actionCatalogItems({ actions })).toEqual(actions)
  })

  test('natural-language summary is derived from, but does not replace, AST', () => {
    const rule = {
      id: 'approval',
      name: 'Approval',
      enabled: true,
      priority: 100,
      when: { fact: 'action.amount', op: 'gt', value: '5000' } as const,
      then: [{ action: 'require_approval', role: 'ADMIN' }],
      onUnknown: [{ action: 'deny' }],
    }
    expect(summarizeRule(rule, [fact], actions)).toContain('action.amount')
    expect(summarizeRule(rule, [fact], actions)).toContain('5000')
    expect(summarizeRule(rule, [fact], actions)).toContain('deny')
  })

  test('accepts either canonical response casing', () => {
    const ruleSet: AgentBusinessRules = { version: 1, rules: [] }
    expect(canonicalRulesFromValidation({ canonicalRuleSet: ruleSet })).toBe(ruleSet)
    expect(canonicalRulesFromValidation({ canonical_rule_set: ruleSet })).toBe(ruleSet)
  })

  test('keeps decimal authoring values as exact strings while retaining number behavior', () => {
    expect(defaultTypedValue('decimal')).toBe('0')
    expect(defaultTypedValue('number')).toBe(0)
    expect(defaultTypedValue('integer')).toBe(0)
    expect(defaultTypedValue('boolean')).toBe(false)
    expect(defaultTypedValue('string')).toBe('')
    // A catalog enum wins over the type default and is handed back byte-identically —
    // a decimal seed must never round-trip through Number.
    expect(defaultTypedValue('decimal', ['9007199254740993.01'])).toBe('9007199254740993.01')
    // collection is resolved before the enum branch: a multi-select starts empty, not preselected.
    expect(defaultTypedValue('collection', ['a', 'b'])).toEqual([])
  })

  test('nesting depth follows the catalog limit and fails safe to 3, never to 0', () => {
    expect(ruleUiDepthLimit({ ...factsResponse, limits: { maxDepth: 5 } })).toBe(5)
    // Absent catalog, absent limits, absent field, and every nonsensical value fall back to 3 —
    // a 0/NaN limit must never collapse the editor to "no nesting at all".
    expect(ruleUiDepthLimit(null)).toBe(3)
    expect(ruleUiDepthLimit([fact])).toBe(3)
    expect(ruleUiDepthLimit(factsResponse)).toBe(3)
    expect(ruleUiDepthLimit({ limits: {} })).toBe(3)
    expect(ruleUiDepthLimit({ limits: { maxDepth: 0 } })).toBe(3)
    expect(ruleUiDepthLimit({ limits: { maxDepth: Number.NaN } })).toBe(3)
    expect(ruleUiDepthLimit({ limits: { maxDepth: 2.5 } })).toBe(3)
  })

  test('omits optional action parameters until the author supplies them', () => {
    expect(
      createRuleAction({
        name: 'deny',
        parameters: [{ name: 'reason', type: 'string', required: false }],
      }),
    ).toEqual({ action: 'deny' })
    expect(createRuleAction(actions[0])).toEqual({ action: 'require_approval', role: '' })
  })
})

test.describe('Agent API concurrency contract', () => {
  test('a missing or stale ETag surfaces the server ApiError instead of a silent overwrite', async () => {
    const originalFetch = globalThis.fetch
    const observed: Array<{ path: string; ifMatch: string | null }> = []
    const responses: Array<{ status: number; body: unknown }> = [
      {
        status: 428,
        body: {
          timestamp: '2026-07-24T00:00:00Z',
          status: 428,
          message: '需要 If-Match 才能修改草稿。',
          fieldErrors: {},
        },
      },
      {
        status: 409,
        body: {
          timestamp: '2026-07-24T00:00:00Z',
          status: 409,
          message: 'draft 版本衝突',
          fieldErrors: { draft_version: '此 Agent 已被其他人更新。' },
        },
      },
    ]
    globalThis.fetch = async (input, init) => {
      observed.push({ path: String(input), ifMatch: new Headers(init?.headers).get('If-Match') })
      const next = responses.shift()!
      return new Response(JSON.stringify(next.body), {
        status: next.status,
        headers: { 'Content-Type': 'application/json' },
      })
    }

    try {
      // No captured ETag → the header is omitted entirely, which is exactly what the server 428s on.
      const missing = await validateAgent('agent-id', null).catch((error: unknown) => error)
      expect(missing).toBeInstanceOf(ApiError)
      expect((missing as ApiError).status).toBe(428)
      expect((missing as ApiError).message).toBe('需要 If-Match 才能修改草稿。')

      // Stale ETag → 409 keeps the server message and fieldErrors so the conflict banner can render.
      const stale = await putAgentDraft('agent-id', createEmptyAgentDraft(), '"1"').catch(
        (error: unknown) => error,
      )
      expect(stale).toBeInstanceOf(ApiError)
      expect((stale as ApiError).status).toBe(409)
      expect((stale as ApiError).message).toBe('draft 版本衝突')
      expect((stale as ApiError).fieldErrors).toEqual({
        draft_version: '此 Agent 已被其他人更新。',
      })
    } finally {
      globalThis.fetch = originalFetch
    }

    expect(observed).toEqual([
      { path: '/api/agents/agent-id/validate', ifMatch: null },
      { path: '/api/agents/agent-id/draft', ifMatch: '"1"' },
    ])
  })

  test('Business Rule validation and simulation use public Platform paths and canonical envelopes', async () => {
    const originalFetch = globalThis.fetch
    const requests: Array<{ path: string; body: unknown }> = []
    globalThis.fetch = async (input, init) => {
      requests.push({
        path: String(input),
        body: init?.body ? JSON.parse(String(init.body)) : undefined,
      })
      return new Response(JSON.stringify({ valid: true, errors: [], simulation: {} }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      })
    }
    const rules: AgentBusinessRules = { version: 1, rules: [] }

    try {
      await validateBusinessRules('pre-action', rules)
      await simulateBusinessRules('post-context', rules, { 'context.confidence': 0.4 })
    } finally {
      globalThis.fetch = originalFetch
    }

    expect(requests).toEqual([
      {
        path: '/api/agents/rules/validate',
        body: { gate: 'pre-action', ruleSet: rules },
      },
      {
        path: '/api/agents/rules/simulate',
        body: {
          gate: 'post-context',
          ruleSet: rules,
          facts: { 'context.confidence': 0.4 },
        },
      },
    ])
  })
})
