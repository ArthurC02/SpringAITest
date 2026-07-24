import { expect, test } from '@playwright/test'
import {
  businessRuleCount,
  createEmptyAgentDraft,
  isAgentEditorLocked,
  isSkillBindable,
  nextAgentRevision,
  normalizeAgentDraft,
  parseOutputContract,
} from '../src/agentBuilder'
import {
  simulateBusinessRules,
  validateAgent,
  validateBusinessRules,
} from '../src/api/agents'
import {
  actionCatalogItems,
  canonicalRulesFromValidation,
  createRuleAction,
  defaultTypedValue,
  factCatalogItems,
  factsForGate,
  operatorEntries,
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
        audience: ['finance'],
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
    expect(normalized.audience).toEqual(['finance'])
    expect(normalized.output_contract).toEqual({ type: 'object' })
    expect(normalized.runtime_limits.timeout_seconds).toBe(90)
    expect(normalized.runtime_workflow).toEqual({ id: 'workflow-id', revision: 4 })
  })

  test('defaults new agents to explicit fail-closed sets and tenant roles', () => {
    const draft = createEmptyAgentDraft()
    expect(draft.allowed_tools).toEqual([])
    expect(draft.knowledge_sources).toEqual([])
    expect(draft.capabilities).toEqual([])
    expect(draft.audience).toEqual(['USER', 'ADMIN'])
    expect(businessRuleCount(draft.business_rules)).toBe(0)
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

  test('locks editing for pending writes and unresolved conflicts', () => {
    expect(isAgentEditorLocked(false, false, false)).toBe(false)
    expect(isAgentEditorLocked(false, true, false)).toBe(true)
    expect(isAgentEditorLocked(false, false, true)).toBe(true)
    expect(isAgentEditorLocked(true, false, false)).toBe(true)
  })

  test('previews the next immutable Agent revision', () => {
    expect(nextAgentRevision(null)).toBe(1)
    expect(nextAgentRevision(4)).toBe(5)
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
    const exact = '9007199254740993.01'
    expect(JSON.parse(JSON.stringify({ amount: exact }))).toEqual({ amount: exact })
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
  test('validate forwards the current ETag in If-Match', async () => {
    const originalFetch = globalThis.fetch
    let observedIfMatch: string | null = null
    let observedPath = ''
    globalThis.fetch = async (input, init) => {
      observedPath = String(input)
      observedIfMatch = new Headers(init?.headers).get('If-Match')
      return new Response(JSON.stringify({ valid: true, errors: [] }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      })
    }

    try {
      await expect(validateAgent('agent-id', '"7"')).resolves.toEqual({ valid: true, errors: [] })
    } finally {
      globalThis.fetch = originalFetch
    }

    expect(observedPath).toBe('/api/agents/agent-id/validate')
    expect(observedIfMatch).toBe('"7"')
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
