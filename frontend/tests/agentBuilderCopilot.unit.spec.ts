// 副駕改的是「使用者看不到內部邏輯」的表單狀態,所以會靜默出錯的兩處都在這裡設防:
// (1) LLM 幻想出目錄裡沒有的 skill/tool/fact/動作,(2) 唯讀/衝突時仍動表單。
import { expect, test } from 'vitest'
import {
  AGENT_COPILOT_LOCKED_MESSAGE,
  planAgentBusinessRule,
  planAgentDraftFill,
} from '../src/components/AgentBuilderCopilot'
import { createEmptyAgentDraft } from '../src/agentBuilder'
import type {
  AgentBusinessRule,
  AgentBusinessRules,
  RuleActionCatalogEntry,
  RuleFactCatalogEntry,
} from '../src/types'

const CATALOG = { skills: ['rag-qa', 'sales-report'], tools: ['kb_search'] }

const FACTS: RuleFactCatalogEntry[] = [
  {
    name: 'action.amount',
    label: '動作金額',
    type: 'decimal',
    provenance: 'runtime',
    gates: ['pre-action'],
    operators: [
      { name: 'gt', label: '大於', value_count: 1 },
      { name: 'lt', label: '小於', value_count: 1 },
      { name: 'between', label: '介於', value_count: 2 },
    ],
  },
  {
    name: 'context.region',
    label: '地區',
    type: 'enum',
    provenance: 'context',
    gates: ['pre-action'],
    operators: [{ name: 'eq', label: '等於', value_count: 1 }],
    enumValues: ['TW', 'JP'],
  },
  {
    name: 'context.verified',
    label: '已驗證',
    type: 'boolean',
    provenance: 'context',
    gates: ['pre-action'],
    operators: [
      { name: 'eq', label: '等於', value_count: 1 },
      { name: 'is_true', label: '為真', value_count: 0 },
    ],
  },
  {
    name: 'action.retry_count',
    label: '重試次數',
    type: 'integer',
    provenance: 'runtime',
    gates: ['pre-action'],
    operators: [{ name: 'gt', label: '大於', value_count: 1 }],
  },
]

const ACTIONS: RuleActionCatalogEntry[] = [
  { name: 'require_approval', label: '需要核准' },
  { name: 'deny', label: '拒絕' },
]

const EMPTY_RULES: AgentBusinessRules = { version: 1, rules: [] }

function ruleContext(overrides: Partial<Parameters<typeof planAgentBusinessRule>[1]> = {}) {
  return { current: EMPTY_RULES, locked: false, facts: FACTS, actions: ACTIONS, ...overrides }
}

test.describe('fillAgentDraft 計畫', () => {
  test('目錄中不存在的 skill/tool 不會進入表單,且訊息明講被略過', () => {
    const plan = planAgentDraftFill(
      { skills: ['rag-qa', 'imagined-skill'], tools: ['imagined-tool'], capabilities: ['analysis'] },
      { current: createEmptyAgentDraft(), locked: false, ...CATALOG },
    )

    expect(plan.patch.skill_bindings).toEqual([{ skill: 'rag-qa' }])
    // 全部無效 → 該欄位完全不動,不會把既有 allowlist 洗成空集合。
    expect(plan.patch.allowed_tools).toBeUndefined()
    expect(plan.message).toContain('不在目錄中')
    expect(plan.message).toContain('imagined-skill')
    expect(plan.message).toContain('imagined-tool')
    // 動到進階欄位(capabilities)就要展開進階區。
    expect(plan.revealAdvanced).toBe(true)
  })

  test('同名綁定保留既有 binding 物件(不丟 revision_policy)', () => {
    const current = createEmptyAgentDraft()
    current.skill_bindings = [{ skill: 'rag-qa', revision_policy: 'pinned' }]

    const plan = planAgentDraftFill(
      { skills: ['rag-qa', 'sales-report'] },
      { current, locked: false, ...CATALOG },
    )

    expect(plan.patch.skill_bindings).toEqual([
      { skill: 'rag-qa', revision_policy: 'pinned' },
      { skill: 'sales-report' },
    ])
  })

  // 清單欄位是整份取代:「再加一個 web_search」會把既有 allowlist 洗掉,而縮減 allowlist
  // 一路到發布都合法(沒有任何一關會擋),所以訊息必須把被移除的項目講出來。
  test('整份取代清單時,被移除的項目要出現在回覆訊息裡', () => {
    const current = createEmptyAgentDraft()
    current.allowed_tools = ['kb_search', 'doc_read']
    current.knowledge_sources = ['handbook', 'contracts']
    current.capabilities = ['analysis', 'research']
    current.skill_bindings = [{ skill: 'rag-qa' }, { skill: 'sales-report' }]

    const plan = planAgentDraftFill(
      {
        tools: ['kb_search'],
        knowledgeSources: ['handbook'],
        capabilities: ['analysis'],
        skills: ['rag-qa'],
      },
      { current, locked: false, skills: ['rag-qa', 'sales-report'], tools: ['kb_search', 'doc_read'] },
    )

    expect(plan.patch.allowed_tools).toEqual(['kb_search'])
    expect(plan.message).toContain('已移除:doc_read')
    expect(plan.message).toContain('已移除:contracts')
    expect(plan.message).toContain('已移除:research')
    expect(plan.message).toContain('已移除:sales-report')
  })

  test('純文字欄位直接進 patch,訊息逐項講明改了什麼', () => {
    const plan = planAgentDraftFill(
      { name: '客服 Agent', description: '處理退換貨問答', systemPrompt: '你是客服助理。' },
      { current: createEmptyAgentDraft(), locked: false, ...CATALOG },
    )

    expect(plan.patch.name).toBe('客服 Agent')
    expect(plan.patch.description).toBe('處理退換貨問答')
    expect(plan.patch.system_prompt).toBe('你是客服助理。')
    expect(plan.message).toContain('名稱=「客服 Agent」')
    expect(plan.message).toContain('描述=「處理退換貨問答」')
    expect(plan.message).toContain('System Prompt')
    // 三個都是基本區欄位,不必展開進階設定。
    expect(plan.revealAdvanced).toBe(false)
  })

  test('只給空白字串時該欄位完全不動', () => {
    const current = createEmptyAgentDraft()
    current.name = '既有名稱'

    const plan = planAgentDraftFill(
      { name: '   ', description: '', systemPrompt: '\n\t' },
      { current, locked: false, ...CATALOG },
    )

    expect(plan.patch).toEqual({})
    expect(plan.message).toContain('沒有可套用的內容')
  })

  // 空清單是「這次沒指定」,不是「清空」——否則 LLM 少填一個參數就等於撤掉整份授權。
  test('明確給空清單時不會把既有授權洗成空集合', () => {
    const current = createEmptyAgentDraft()
    current.capabilities = ['analysis']
    current.knowledge_sources = ['handbook']
    current.allowed_tools = ['kb_search']

    const plan = planAgentDraftFill(
      { capabilities: [], knowledgeSources: [], tools: [] },
      { current, locked: false, ...CATALOG },
    )

    expect(plan.patch).toEqual({})
    expect(plan.revealAdvanced).toBe(false)
    expect(plan.message).toContain('沒有可套用的內容')
  })

  test('locked 時完全不產生 patch', () => {
    const plan = planAgentDraftFill(
      { name: '客服 Agent', skills: ['rag-qa'], tools: ['kb_search'] },
      { current: createEmptyAgentDraft(), locked: true, ...CATALOG },
    )

    expect(plan.patch).toEqual({})
    expect(plan.revealAdvanced).toBe(false)
    expect(plan.message).toBe(AGENT_COPILOT_LOCKED_MESSAGE)
  })
})

test.describe('addAgentBusinessRule 計畫', () => {
  test('用目錄 helper 組出 canonical AST,並附 fail-closed 的 onUnknown', () => {
    const plan = planAgentBusinessRule(
      {
        fact: 'action.amount',
        operator: 'gt',
        action: 'require_approval',
        value: '5000',
        name: '高額需核准',
      },
      ruleContext(),
    )

    expect(plan.rules?.rules).toHaveLength(1)
    const rule = plan.rules!.rules[0]
    expect(rule.name).toBe('高額需核准')
    // decimal 走字串線格式(與表單編輯器一致)。
    expect(rule.when).toEqual({ fact: 'action.amount', op: 'gt', value: '5000' })
    expect(rule.then).toEqual([{ action: 'require_approval' }])
    expect(rule.onUnknown?.[0]?.action).toBe('deny')
    expect(plan.message).toContain('驗證')
  })

  test.describe('無效參數一律不改草稿', () => {
    const cases: [string, Parameters<typeof planAgentBusinessRule>[0], string][] = [
      ['fact', { fact: 'action.imagined', operator: 'gt', action: 'deny', value: '1' }, 'action.amount'],
      ['運算子', { fact: 'action.amount', operator: 'imagined_op', action: 'deny', value: '1' }, 'gt'],
      ['動作', { fact: 'action.amount', operator: 'gt', action: 'imagined', value: '1' }, 'require_approval'],
      ['列舉值', { fact: 'context.region', operator: 'eq', action: 'deny', value: 'US' }, 'TW'],
    ]
    for (const [label, input, expectedHint] of cases) {
      test(`${label}不在目錄中`, () => {
        const plan = planAgentBusinessRule(input, ruleContext())
        expect(plan.rules).toBeUndefined()
        // 訊息要列出合法選項,使用者才知道下一步。
        expect(plan.message).toContain(expectedHint)
      })
    }
  })

  test('運算子存在於目錄、但不屬於這個 fact 時不改草稿', () => {
    const plan = planAgentBusinessRule(
      { fact: 'action.amount', operator: 'eq', action: 'deny', value: '1' },
      ruleContext(),
    )
    expect(plan.rules).toBeUndefined()
    expect(plan.message).toContain('不適用於 action.amount')
    expect(plan.message).toContain('gt')
  })

  test('列舉值在允許清單中時照常建立規則', () => {
    const plan = planAgentBusinessRule(
      { fact: 'context.region', operator: 'eq', action: 'deny', value: 'TW' },
      ruleContext(),
    )
    expect(plan.rules?.rules[0]?.when).toEqual({ fact: 'context.region', op: 'eq', value: 'TW' })
  })

  // LLM 一律用字串傳值,型別要在這裡轉成 canonical AST 的原生型別,轉不出來就整條不收。
  test('布林與整數 fact 依型別轉型,轉不出來就不改草稿', () => {
    const boolPlan = planAgentBusinessRule(
      { fact: 'context.verified', operator: 'eq', action: 'deny', value: 'false' },
      ruleContext(),
    )
    expect(boolPlan.rules?.rules[0]?.when).toEqual({
      fact: 'context.verified',
      op: 'eq',
      value: false,
    })

    const intPlan = planAgentBusinessRule(
      { fact: 'action.retry_count', operator: 'gt', action: 'deny', value: '3' },
      ruleContext(),
    )
    expect(intPlan.rules?.rules[0]?.when).toEqual({
      fact: 'action.retry_count',
      op: 'gt',
      value: 3,
    })

    const invalid: [Parameters<typeof planAgentBusinessRule>[0], string][] = [
      [{ fact: 'context.verified', operator: 'eq', action: 'deny', value: 'maybe' }, '布林值'],
      [{ fact: 'action.retry_count', operator: 'gt', action: 'deny', value: '2.5' }, '整數'],
    ]
    for (const [input, hint] of invalid) {
      const plan = planAgentBusinessRule(input, ruleContext())
      expect(plan.rules).toBeUndefined()
      expect(plan.message).toContain(hint)
    }
  })

  test('不需要比較值的運算子不寫出 when.value', () => {
    const plan = planAgentBusinessRule(
      { fact: 'context.verified', operator: 'is_true', action: 'deny' },
      ruleContext(),
    )
    expect(plan.rules?.rules[0]?.when).toEqual({ fact: 'context.verified', op: 'is_true' })
  })

  test('需要範圍值的運算子交還給表單編輯器,不改草稿', () => {
    const plan = planAgentBusinessRule(
      { fact: 'action.amount', operator: 'between', action: 'deny', value: '1000' },
      ruleContext(),
    )
    expect(plan.rules).toBeUndefined()
    expect(plan.message).toContain('需要範圍值')
  })

  test('草稿已有規則時是附加,既有規則原封保留且 priority 依序遞減', () => {
    const existing: AgentBusinessRule = {
      id: 'rule-existing',
      name: '既有規則',
      enabled: true,
      priority: 100,
      when: { fact: 'action.amount', op: 'lt', value: '10' },
      then: [{ action: 'deny' }],
    }

    const plan = planAgentBusinessRule(
      { fact: 'action.amount', operator: 'gt', action: 'require_approval', value: '5000' },
      ruleContext({ current: { version: 1, rules: [existing] } }),
    )

    expect(plan.rules?.rules).toHaveLength(2)
    expect(plan.rules?.rules[0]).toEqual(existing)
    expect(plan.rules?.rules[1]?.priority).toBe(90)
    // 沒給名稱時沿用 createBusinessRule 依位置產生的預設名。
    expect(plan.rules?.rules[1]?.name).toBe('規則 2')
  })

  test('缺少必要的比較值時不改草稿', () => {
    const plan = planAgentBusinessRule(
      { fact: 'action.amount', operator: 'gt', action: 'deny' },
      ruleContext(),
    )
    expect(plan.rules).toBeUndefined()
  })

  test('locked 時不改草稿', () => {
    const plan = planAgentBusinessRule(
      { fact: 'action.amount', operator: 'gt', action: 'require_approval', value: '5000' },
      ruleContext({ locked: true }),
    )
    expect(plan.rules).toBeUndefined()
    expect(plan.message).toBe(AGENT_COPILOT_LOCKED_MESSAGE)
  })
})
