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
import { validateAgent } from '../src/api/agents'
import type { SkillCatalogEntry } from '../src/types'

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
})
