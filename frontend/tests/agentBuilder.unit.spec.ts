// Audience principals are validated against a regex that is duplicated verbatim from the server:
// `src/agentBuilder.ts:19` AUDIENCE_GROUP_ID === platform `src/Platform.Service/Dtos/UserContext.cs:34`
// CanonicalGroupIdRegex, whose 126-char middle class also encodes UserGroupContract.MaxGroupIdLength = 128.
// The frontend is not the authority; these boundary cases exist so that relaxing/tightening the
// server rule without updating this copy fails here instead of silently blocking legal authoring.
import { expect, test } from 'vitest'
import {
  AGENT_DEFAULT_CONFIG_FALLBACKS,
  AGENT_DEFAULT_RUNTIME_LIMITS,
  AGENT_DEFAULT_SYSTEM_PROMPT,
  applyAgentDefaultConfig,
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
  publishAgent,
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
  AgentBusinessRule,
  AgentBusinessRules,
  AgentDraft,
  RuleActionCatalogEntry,
  RuleFactCatalogEntry,
  SkillCatalogEntry,
} from '../src/types'

test.describe('Agent Builder model contracts', () => {
  const identity = { name: 'Agent', slug: 'agent', description: '' }

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

  test('drops governed list entries the server would reject instead of importing them', () => {
    const normalized = normalizeAgentDraft(
      {
        ...createEmptyAgentDraft(),
        execution_roles: ['worker', 'admin', 'verifier'],
        skill_bindings: [
          { skill: 'invoice-reader', revision_policy: 'latest' },
          null,
          'invoice-reader',
          {},
          { skill: 3 },
        ],
      } as unknown as Partial<AgentDraft>,
      identity,
    )
    // Only the two canonical execution roles survive; a surviving binding keeps its own fields.
    expect(normalized.execution_roles).toEqual(['worker', 'verifier'])
    expect(normalized.skill_bindings).toEqual([
      { skill: 'invoice-reader', revision_policy: 'latest' },
    ])
    // A non-array from an old/broken server becomes the fail-closed empty set, never undefined.
    expect(
      normalizeAgentDraft(
        { skill_bindings: 'invoice-reader' } as unknown as Partial<AgentDraft>,
        identity,
      ).skill_bindings,
    ).toEqual([])
  })

  test('keeps runtime_workflow only when an id and a positive revision both survive', () => {
    const workflowOf = (runtime_workflow: unknown) =>
      normalizeAgentDraft(
        { ...createEmptyAgentDraft(), runtime_workflow } as unknown as Partial<AgentDraft>,
        identity,
      ).runtime_workflow

    // r1 is the lowest revision a published workflow can have, so it must be kept.
    expect(workflowOf({ id: 'workflow-id', revision: 1 })).toEqual({
      id: 'workflow-id',
      revision: 1,
    })
    // Half a reference is worse than none: the server would pin nothing, so the field is dropped
    // and the create path falls back to the system-owned Default Agent-Runtime Workflow.
    expect(workflowOf({ id: 'workflow-id', revision: 0 })).toBeUndefined()
    expect(workflowOf({ id: 'workflow-id', revision: -1 })).toBeUndefined()
    expect(workflowOf({ id: 'workflow-id' })).toBeUndefined()
    expect(workflowOf({ id: '', revision: 4 })).toBeUndefined()
    expect(workflowOf({ revision: 4 })).toBeUndefined()
  })

  test('defaults new agents to explicit fail-closed sets, tenant roles, and revision r1', () => {
    const draft = createEmptyAgentDraft()
    expect(draft.allowed_tools).toEqual([])
    expect(draft.knowledge_sources).toEqual([])
    expect(draft.capabilities).toEqual([])
    expect(draft.audience).toEqual(['role:USER', 'role:ADMIN'])
    expect(businessRuleCount(draft.business_rules)).toBe(0)
    // Governed sets stay fail-closed, but the purely technical fields must ship usable values —
    // an all-zero runtime + blank prompt used to create an Agent nobody could actually run.
    expect(draft.runtime_limits).toEqual(AGENT_DEFAULT_RUNTIME_LIMITS)
    expect(Object.values(draft.runtime_limits).every((v) => v > 0)).toBe(true)
    expect(draft.system_prompt).toBe(AGENT_DEFAULT_SYSTEM_PROMPT)
    // A never-published Agent previews r1, not r0; an existing one previews published + 1.
    expect(nextAgentRevision(null)).toBe(1)
    expect(nextAgentRevision(4)).toBe(5)
  })

  test('counts the rules array itself, and a malformed rule set reads as zero rules', () => {
    const rule: AgentBusinessRule = {
      id: 'refund',
      name: 'Refund approval',
      enabled: true,
      priority: 100,
      when: { fact: 'action.amount', op: 'gt', value: '5000' },
      then: [{ action: 'require_approval', role: 'ADMIN' }],
    }
    // Disabled rules still count: the badge reports authored rules, not the evaluator's view.
    expect(businessRuleCount({ version: 1, rules: [rule, { ...rule, enabled: false }] })).toBe(2)
    // Never throws on a rule set an older server left without an array — the editor must still open.
    expect(businessRuleCount({ version: 1 } as unknown as AgentBusinessRules)).toBe(0)
  })

  test('system config overrides create-mode defaults, but a bad value never yields a bad draft', () => {
    const base = createEmptyAgentDraft()
    const entry = (key: string, value: string) => ({ key, value, updatedAt: '' })

    const applied = applyAgentDefaultConfig(base, [
      entry('agent.defaults.max_tool_rounds', ' 4 '),
      entry('agent.defaults.timeout_seconds', '0'),
      entry('agent.defaults.system_prompt', '你是財務助理。'),
      entry('unrelated.key', '999'),
    ])
    expect(applied.runtime_limits.max_tool_rounds).toBe(4)
    expect(applied.runtime_limits.timeout_seconds).toBe(0) // 0 is a legal "let Runtime decide"
    expect(applied.system_prompt).toBe('你是財務助理。')
    // Untouched keys keep the built-in fallback.
    expect(applied.runtime_limits.token_budget).toBe(AGENT_DEFAULT_RUNTIME_LIMITS.token_budget)

    // Missing / blank / non-numeric / negative / fractional all fall back instead of corrupting
    // the draft. Fractions matter: backend `AgentCanonicalizer.ValidateLimit` reads the limit with
    // `TryGetValue<int>`, so letting 2.5 through would seed a draft that can never validate.
    const rejected = applyAgentDefaultConfig(base, [
      entry('agent.defaults.max_tool_rounds', 'lots'),
      entry('agent.defaults.max_context_rounds', '-1'),
      entry('agent.defaults.step_budget', '   '),
      entry('agent.defaults.timeout_seconds', '2.5'),
      entry('agent.defaults.system_prompt', '  '),
    ])
    expect(rejected.runtime_limits).toEqual(AGENT_DEFAULT_RUNTIME_LIMITS)
    expect(rejected.system_prompt).toBe(AGENT_DEFAULT_SYSTEM_PROMPT)
    expect(applyAgentDefaultConfig(base, [])).toEqual(base)

    // The "一般設定" table renders exactly these keys when the server has none of them yet.
    expect(Object.keys(AGENT_DEFAULT_CONFIG_FALLBACKS).sort()).toEqual([
      'agent.defaults.max_context_rounds',
      'agent.defaults.max_tool_rounds',
      'agent.defaults.step_budget',
      'agent.defaults.system_prompt',
      'agent.defaults.timeout_seconds',
      'agent.defaults.token_budget',
    ])
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

  test('reads a bare array catalog and the generic items envelope, not just the named key', () => {
    // Platform may answer with the array itself, `{facts|actions:[...]}`, or a generic envelope;
    // all three are the same catalog to the editor.
    expect(factCatalogItems([fact])).toEqual([fact])
    expect(factCatalogItems({ items: [fact] })).toEqual([fact])
    expect(actionCatalogItems(actions)).toEqual(actions)
    expect(actionCatalogItems({ items: actions })).toEqual(actions)
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

  test('summarizes nested any/all/not groups instead of stopping at the first leaf', () => {
    const nested: AgentBusinessRule = {
      id: 'refund',
      name: 'Refund approval',
      enabled: true,
      priority: 100,
      when: {
        all: [
          {
            any: [
              { fact: 'action.amount', op: 'gt', value: '5000' },
              { fact: 'action.amount', op: 'between', value: ['1', '2'] },
            ],
          },
          { not: { fact: 'caller.role', op: 'eq', value: 'ADMIN' } },
        ],
      },
      then: [{ action: 'require_approval', role: 'ADMIN' }],
    }
    // Characterizes today's output: 且／或 joiners, 不是（…） for not, and facts/operators the
    // catalog does not know (caller.role) fall back to their raw ids rather than disappearing.
    expect(summarizeRule(nested, [fact], actions)).toBe(
      '如果 action.amount gt 「5000」 或 action.amount between 「1」、「2」 且 不是（caller.role eq 「ADMIN」），則 require_approval（role=「ADMIN」）。',
    )
    // An empty group must read as "nothing authored yet", never as an always-true condition.
    expect(summarizeRule({ ...nested, when: { all: [] } }, [fact], actions)).toContain('尚無條件')
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

  test('publish layers expected_draft_version on the same If-Match contract as draft and validate', async () => {
    const originalFetch = globalThis.fetch
    const observed: Array<{ path: string; ifMatch: string | null; body: unknown }> = []
    const statuses = [428, 412, 428, 409]
    globalThis.fetch = async (input, init) => {
      observed.push({
        path: String(input),
        ifMatch: new Headers(init?.headers).get('If-Match'),
        body: init?.body ? JSON.parse(String(init.body)) : undefined,
      })
      const status = statuses.shift()!
      return new Response(
        JSON.stringify({
          timestamp: '2026-07-24T00:00:00Z',
          status,
          message: `版本衝突 ${status}`,
          fieldErrors: {},
        }),
        { status, headers: { 'Content-Type': 'application/json' } },
      )
    }
    const statusOf = async (call: Promise<unknown>): Promise<number> => {
      const error = await call.catch((thrown: unknown) => thrown)
      expect(error).toBeInstanceOf(ApiError)
      return (error as ApiError).status
    }

    try {
      // Publish is the endpoint the other two never covered, in both ETag states...
      expect(await statusOf(publishAgent('agent-id', 3, null))).toBe(428)
      expect(await statusOf(publishAgent('agent-id', 3, '"7"'))).toBe(412)
      // ...and the off-diagonal combinations must behave identically, not just the diagonal.
      expect(await statusOf(putAgentDraft('agent-id', createEmptyAgentDraft(), null))).toBe(428)
      expect(await statusOf(validateAgent('agent-id', '"1"'))).toBe(409)
    } finally {
      globalThis.fetch = originalFetch
    }

    expect(observed.map(({ path, ifMatch }) => ({ path, ifMatch }))).toEqual([
      { path: '/api/agents/agent-id/publish', ifMatch: null },
      { path: '/api/agents/agent-id/publish', ifMatch: '"7"' },
      { path: '/api/agents/agent-id/draft', ifMatch: null },
      { path: '/api/agents/agent-id/validate', ifMatch: '"1"' },
    ])
    // Publish carries the version it saw inside the body too — the ETag alone is not the guard.
    expect(observed[0].body).toEqual({ expected_draft_version: 3 })
    expect(observed[1].body).toEqual({ expected_draft_version: 3 })
    expect(observed[3].body).toBeUndefined()
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
