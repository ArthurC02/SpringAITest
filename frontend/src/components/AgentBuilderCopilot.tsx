import { useMemo } from 'react'
import { useCopilotAction, useCopilotReadable } from '@copilotkit/react-core'
import {
  listRuleActions,
  listRuleFacts,
  type RuleActionCatalogResponse,
  type RuleFactCatalogResponse,
} from '../api/agents'
import { businessRuleCount } from '../agentBuilder'
import {
  actionCatalogItems,
  createBusinessRule,
  createRuleAction,
  DEFAULT_RULE_GATE,
  factCatalogItems,
  factsForGate,
  metadataValues,
  operatorEntries,
  operatorNeedsValue,
  summarizeRule,
} from '../ruleBuilder'
import type {
  AgentBusinessRule,
  AgentBusinessRules,
  AgentDraft,
  AgentToolCatalogEntry,
  RuleConditionLeaf,
  RuleFactCatalogEntry,
  RuleOperatorCatalogEntry,
  SkillCatalogEntry,
} from '../types'
import { useResource } from '../hooks/useResource'

interface Props {
  form: AgentDraft
  creating: boolean
  /** AgentEditor 的 isAgentEditorLocked():唯讀/忙碌/版本衝突時副駕一律不得改表單。 */
  locked: boolean
  /** 已過濾成「可綁定」的 Skill(副駕只能從這裡挑)。 */
  skills: SkillCatalogEntry[]
  tools: AgentToolCatalogEntry[]
  onPatch: (patch: Partial<AgentDraft>) => void
  onRevealAdvanced: () => void
}

export const AGENT_COPILOT_LOCKED_MESSAGE =
  '目前無法修改這份草稿(唯讀、儲存中或有版本衝突),請先處理後再試。'

/**
 * 欄位白話解釋。內容改寫自編輯器上實際的 hint 文字,是副駕「解釋欄位在問什麼」的唯一來源 ——
 * 不要在這裡寫出與 UI/伺服器行為不符的說明。
 */
export const AGENT_FIELD_GLOSSARY: Readonly<Record<string, string>> = {
  name: '這個 Agent 在租戶內顯示的名稱,給人看的。',
  slug: '穩定的英數識別字,API 用;建立後不能改。',
  description: '一句話說明它的用途與適用情境。',
  system_prompt: '給 Agent 的角色、目標、語氣與行為指引;等於「你是誰、該怎麼回答」。',
  execution_roles: 'Worker=實際做事;Verifier=檢查別人的產出。可以複選。',
  capabilities: '能力標籤,讓 Orchestrator 依能力挑選這個 Agent;不填就不會被能力條件選中。',
  output_contract: '輸出結果的 JSON 欄位約定;不確定就留空 object。',
  audience: '誰可以啟動它,用 role:USER、role:ADMIN 或 group:<群組>;完全不填 = 任何人都不能用。',
  allowed_tools: '允許使用的工具,只能從系統 Tool Catalog 勾選;不勾 = 沒有任何工具權限,不是全開。',
  knowledge_sources: '允許查詢的知識來源範圍;不填 = 不授權任何知識來源。',
  skill_bindings: '綁定的 Skill(既有的工作流程);發布時會固定到當時的確切版本。',
  business_rules: '不可被模型繞過的商業規則,例如「金額大於 5000 就要主管核准」;由伺服器驗證與執行。',
  runtime_limits: '單次執行的上限:工具輪數、Context 輪數、逾時秒數、token 與步數預算;填 0 代表交由 Runtime 預設。',
}

export interface AgentDraftFillInput {
  name?: string
  description?: string
  systemPrompt?: string
  capabilities?: string[]
  knowledgeSources?: string[]
  skills?: string[]
  tools?: string[]
}

export interface AgentDraftFillContext {
  current: AgentDraft
  locked: boolean
  /** 目錄中真實存在的名稱;不在其中的一律丟棄。 */
  skills: readonly string[]
  tools: readonly string[]
}

export interface AgentDraftFillPlan {
  patch: Partial<AgentDraft>
  /** 動到進階區欄位(capabilities / allowed_tools)時要展開進階設定。 */
  revealAdvanced: boolean
  message: string
}

function trimmed(value: unknown): string {
  return typeof value === 'string' ? value.trim() : ''
}

function cleanList(values: unknown): string[] {
  if (!Array.isArray(values)) return []
  const items = values.map(trimmed).filter(Boolean)
  return items.filter((item, index) => items.indexOf(item) === index)
}

/**
 * 清單欄位是整份取代,不是附加。被無聲拿掉的授權(工具/知識來源/Skill)使用者看不出來,
 * 而且縮減 allowlist 一路到發布都合法,所以移除項目必須在回覆訊息裡逐一講出來。
 */
function removedNote(before: readonly string[], after: readonly string[]): string {
  const removed = before.filter((item) => !after.includes(item))
  return removed.length ? `(已移除:${removed.join('、')})` : ''
}

function names(entries: readonly { name: string }[]): string {
  return entries.map((entry) => entry.name).join('、') || '(目前沒有可用項目)'
}

/**
 * 把自然語言產生的參數變成一份 draft patch。只接受目錄中真實存在的 skill/tool,
 * 其餘丟棄並在訊息裡明講;`locked` 時完全不產生 patch。
 */
export function planAgentDraftFill(
  input: AgentDraftFillInput,
  context: AgentDraftFillContext,
): AgentDraftFillPlan {
  if (context.locked) return { patch: {}, revealAdvanced: false, message: AGENT_COPILOT_LOCKED_MESSAGE }

  const patch: Partial<AgentDraft> = {}
  const changes: string[] = []
  const skipped: string[] = []
  let revealAdvanced = false

  const name = trimmed(input.name)
  if (name) {
    patch.name = name
    changes.push(`名稱=「${name}」`)
  }
  const description = trimmed(input.description)
  if (description) {
    patch.description = description
    changes.push(`描述=「${description}」`)
  }
  const systemPrompt = trimmed(input.systemPrompt)
  if (systemPrompt) {
    patch.system_prompt = systemPrompt
    changes.push('System Prompt')
  }

  const capabilities = cleanList(input.capabilities)
  if (capabilities.length) {
    patch.capabilities = capabilities
    changes.push(
      `Capabilities=${capabilities.join('、')}${removedNote(context.current.capabilities, capabilities)}`,
    )
    revealAdvanced = true
  }

  const knowledgeSources = cleanList(input.knowledgeSources)
  if (knowledgeSources.length) {
    patch.knowledge_sources = knowledgeSources
    changes.push(
      `知識來源=${knowledgeSources.join('、')}${removedNote(context.current.knowledge_sources, knowledgeSources)}`,
    )
  }

  const requestedSkills = cleanList(input.skills)
  const skills = requestedSkills.filter((item) => context.skills.includes(item))
  skipped.push(...requestedSkills.filter((item) => !context.skills.includes(item)))
  if (skills.length) {
    // 既有綁定物件可能帶 revision_policy,同名時原封保留。
    patch.skill_bindings = skills.map(
      (skill) => context.current.skill_bindings.find((b) => b.skill === skill) ?? { skill },
    )
    changes.push(
      `Skill 綁定=${skills.join('、')}${removedNote(
        context.current.skill_bindings.map((b) => b.skill),
        skills,
      )}`,
    )
  }

  const requestedTools = cleanList(input.tools)
  const tools = requestedTools.filter((item) => context.tools.includes(item))
  skipped.push(...requestedTools.filter((item) => !context.tools.includes(item)))
  if (tools.length) {
    patch.allowed_tools = tools
    changes.push(
      `工具 allowlist=${tools.join('、')}${removedNote(context.current.allowed_tools, tools)}`,
    )
    revealAdvanced = true
  }

  const skippedText = skipped.length
    ? `以下項目不在目錄中,已略過:${skipped.join('、')}。`
    : ''
  return {
    patch,
    revealAdvanced,
    message: changes.length
      ? `已改寫草稿:${changes.join('；')}。${skippedText}內容還在表單上,要按「儲存草稿」→「驗證」才算數。`
      : `沒有可套用的內容,草稿未變更。${skippedText}`,
  }
}

export interface AgentRuleInput {
  fact: string
  operator: string
  action: string
  value?: string
  name?: string
}

export interface AgentRuleContext {
  current: AgentBusinessRules
  locked: boolean
  facts: RuleFactCatalogResponse | null
  actions: RuleActionCatalogResponse | null
}

export interface AgentRulePlan {
  /** undefined = 不修改草稿(參數無效或唯讀)。 */
  rules?: AgentBusinessRules
  message: string
}

function coerceRuleValue(
  raw: string | undefined,
  fact: RuleFactCatalogEntry,
  operator: RuleOperatorCatalogEntry,
): { value: unknown } | { error: string } {
  if ((operator.value_count ?? 1) === 2) {
    // ponytail: 範圍運算子交給表單編輯器 —— 用一個字串猜兩端型別不值得。需要時再加雙參數。
    return {
      error: `運算子「${operator.name}」需要範圍值,請直接在「進階設定 › Business Rules」表單設定。`,
    }
  }
  const literal = (raw ?? '').trim()
  if (!literal) return { error: `運算子「${operator.name}」需要一個值,但沒有提供。` }

  const allowed = metadataValues(fact)
  if (allowed.length) {
    const match = allowed.find((item) => String(item) === literal)
    if (match === undefined) {
      return {
        error: `值「${literal}」不在 ${fact.name} 的允許值(${allowed.map(String).join('、')})中。`,
      }
    }
    return { value: match }
  }

  const type =
    operator.value_type && !['none', 'same'].includes(operator.value_type)
      ? operator.value_type
      : fact.type
  if (type === 'boolean') {
    if (literal !== 'true' && literal !== 'false') {
      return { error: `值「${literal}」不是布林值(只接受 true / false)。` }
    }
    return { value: literal === 'true' }
  }
  if (type === 'number' || type === 'integer') {
    const parsed = Number(literal)
    if (!Number.isFinite(parsed) || (type === 'integer' && !Number.isInteger(parsed))) {
      return { error: `值「${literal}」不是合法的${type === 'integer' ? '整數' : '數字'}。` }
    }
    return { value: parsed }
  }
  if (type === 'collection') return { value: literal.split(',').map((p) => p.trim()).filter(Boolean) }
  // decimal 以字串線格式傳送(與表單編輯器一致),其餘視為字串。
  return { value: literal }
}

/**
 * 由目錄 ID + 值組出一條 canonical AST 規則(全程走 ruleBuilder 的 helper,LLM 不直接生 JSON)。
 * 任一參數不在目錄中就完全不改草稿,並回報有哪些合法選項。
 */
export function planAgentBusinessRule(
  input: AgentRuleInput,
  context: AgentRuleContext,
): AgentRulePlan {
  if (context.locked) return { message: AGENT_COPILOT_LOCKED_MESSAGE }

  const facts = factsForGate(factCatalogItems(context.facts), DEFAULT_RULE_GATE)
  const actions = actionCatalogItems(context.actions)
  const fact = facts.find((item) => item.name === input.fact)
  if (!fact) {
    return { message: `fact「${input.fact}」不在目錄中,草稿未變更。可用的 fact:${names(facts)}。` }
  }
  const operators = operatorEntries(fact, context.facts)
  const operator = operators.find((item) => item.name === input.operator)
  if (!operator) {
    return {
      message: `運算子「${input.operator}」不適用於 ${fact.name},草稿未變更。可用的運算子:${names(operators)}。`,
    }
  }
  const action = actions.find((item) => item.name === input.action)
  if (!action) {
    return { message: `動作「${input.action}」不在目錄中,草稿未變更。可用的動作:${names(actions)}。` }
  }

  const when: RuleConditionLeaf = { fact: fact.name, op: operator.name }
  if (operatorNeedsValue(operator)) {
    const coerced = coerceRuleValue(input.value, fact, operator)
    if ('error' in coerced) return { message: `${coerced.error}草稿未變更。` }
    when.value = coerced.value
  }

  // createBusinessRule 吃完整 action 目錄才能補上 fail-closed 的 onUnknown deny;then 由我們指定。
  const base = createBusinessRule([fact], context.facts, actions, context.current.rules.length)
  const rule: AgentBusinessRule = {
    ...base,
    name: trimmed(input.name) || base.name,
    when,
    then: [createRuleAction(action)],
  }
  return {
    rules: { ...context.current, rules: [...context.current.rules, rule] },
    message: `已把規則「${rule.name}」加入草稿:${summarizeRule(rule, facts, actions)} 正確性以伺服器為準,請按「驗證」確認。`,
  }
}

/**
 * Agent 編輯器專用的副駕能力(headless,不輸出 DOM)。掛在編輯器內 → 只有編輯器開著時
 * 這些 readable/action 才存在,卸載即自動移除。副駕只改表單狀態:不儲存、不驗證、不發布。
 */
export default function AgentBuilderCopilot({
  form,
  creating,
  locked,
  skills,
  tools,
  onPatch,
  onRevealAdvanced,
}: Props) {
  // ponytail: 規則目錄與 BusinessRuleEditor 各取一次(兩個小 GET);要省再把 catalog 提到 AgentEditor 共用。
  const factRes = useResource(listRuleFacts)
  const actionRes = useResource(listRuleActions)
  const skillNames = useMemo(() => skills.map((skill) => skill.name), [skills])
  const toolNames = useMemo(() => tools.map((tool) => tool.name), [tools])

  useCopilotReadable(
    {
      description: '目前正在編輯的 Agent 草稿摘要(只有 Agent 編輯器開著時存在)',
      value: {
        mode: creating ? '尚未建立,填完後由使用者自己按「建立 Agent」' : '編輯既有 Agent 的草稿',
        editable: !locked,
        name: form.name,
        slug: form.slug,
        description: form.description,
        system_prompt: form.system_prompt,
        execution_roles: form.execution_roles,
        capabilities: form.capabilities,
        audience: form.audience,
        knowledge_sources: form.knowledge_sources,
        bound_skills: form.skill_bindings.map((binding) => binding.skill),
        allowed_tools: form.allowed_tools,
        business_rule_count: businessRuleCount(form.business_rules),
        runtime_limits: form.runtime_limits,
      },
    },
    [form, creating, locked],
  )

  useCopilotReadable(
    {
      description:
        'Agent 可綁定的 Skill 與可授權的 Tool 目錄。挑選一律只能用這裡出現過的 name,不可自創。',
      value: {
        skills: skills.map((skill) => ({ name: skill.name, description: skill.description })),
        tools: tools.map((tool) => ({
          name: tool.name,
          description: tool.description,
          kind: tool.kind,
          risk: tool.risk,
        })),
      },
    },
    [skills, tools],
  )

  useCopilotReadable(
    {
      description:
        `Business Rule 目錄(gate=${DEFAULT_RULE_GATE}):可用的 fact、其允許的運算子與值,以及可用的動作。addAgentBusinessRule 的參數只能取自這裡。`,
      value: {
        facts: factsForGate(factCatalogItems(factRes.data), DEFAULT_RULE_GATE).map((fact) => ({
          name: fact.name,
          label: fact.label,
          type: fact.type,
          description: fact.description,
          operators: operatorEntries(fact, factRes.data).map((operator) => operator.name),
          allowed_values: metadataValues(fact).map(String),
        })),
        actions: actionCatalogItems(actionRes.data).map((action) => ({
          name: action.name,
          label: action.label,
          description: action.description,
        })),
      },
    },
    [factRes.data, actionRes.data],
  )

  useCopilotReadable({
    description: 'Agent 編輯器每個欄位的白話解釋(使用者問「這欄在問什麼」時照此回答)',
    value: AGENT_FIELD_GLOSSARY,
  })

  useCopilotAction(
    {
      name: 'fillAgentDraft',
      description:
        '依使用者的描述填寫或修改目前開著的 Agent 草稿欄位。只改表單,不會儲存、驗證或發布 —— 那三步一律由使用者自己按。skills/tools 只能填目錄中真實存在的 name。',
      parameters: [
        { name: 'name', type: 'string', description: 'Agent 名稱', required: false },
        { name: 'description', type: 'string', description: '用途與適用情境', required: false },
        {
          name: 'systemPrompt',
          type: 'string',
          description: '角色、目標、語氣與行為指引(完整取代原內容)',
          required: false,
        },
        {
          name: 'capabilities',
          type: 'string[]',
          description:
            '能力標籤(進階欄位)。完整取代目前清單,不是附加 —— 要保留的既有項目必須一併列出,否則會被移除',
          required: false,
        },
        {
          name: 'knowledgeSources',
          type: 'string[]',
          description:
            '允許查詢的知識來源識別字。完整取代目前清單,不是附加 —— 要保留的既有來源必須一併列出,否則會被移除',
          required: false,
        },
        {
          name: 'skills',
          type: 'string[]',
          description:
            '要綁定的 Skill name,必須存在於 Skill 目錄。完整取代目前清單,不是附加 —— 要保留的既有綁定必須一併列出,否則會被移除',
          required: false,
        },
        {
          name: 'tools',
          type: 'string[]',
          description:
            '要授權的 Tool name,必須存在於 Tool Catalog(進階欄位)。完整取代目前 allowlist,不是附加 —— 要保留的既有工具必須一併列出,否則會被移除',
          required: false,
        },
      ],
      handler: async (args) => {
        const plan = planAgentDraftFill(args, {
          current: form,
          locked,
          skills: skillNames,
          tools: toolNames,
        })
        if (Object.keys(plan.patch).length === 0) return plan.message
        onPatch(plan.patch)
        if (plan.revealAdvanced) onRevealAdvanced()
        return plan.message
      },
    },
    [form, locked, skillNames, toolNames, onPatch, onRevealAdvanced],
  )

  useCopilotAction(
    {
      name: 'addAgentBusinessRule',
      description:
        '在目前的 Agent 草稿加入一條商業規則(如果 <fact> <運算子> <值>,就執行 <動作>)。fact/運算子/動作只能取自 Business Rule 目錄。只改表單,不會儲存或發布。',
      parameters: [
        { name: 'fact', type: 'string', description: '目錄中的 fact name', required: true },
        {
          name: 'operator',
          type: 'string',
          description: '該 fact 允許的運算子 name',
          required: true,
        },
        { name: 'action', type: 'string', description: '目錄中的動作 name', required: true },
        {
          name: 'value',
          type: 'string',
          description: '比較值(運算子不需要值時可省略);數字/布林也用字串表示',
          required: false,
        },
        { name: 'name', type: 'string', description: '規則名稱(給人看的)', required: false },
      ],
      handler: async ({ fact, operator, action, value, name }) => {
        if (!locked && (!factRes.data || !actionRes.data)) {
          return '規則目錄尚未載入完成(或載入失敗),草稿未變更,請稍後再試。'
        }
        const plan = planAgentBusinessRule(
          { fact, operator, action, value, name },
          {
            current: form.business_rules,
            locked,
            facts: factRes.data,
            actions: actionRes.data,
          },
        )
        if (!plan.rules) return plan.message
        onPatch({ business_rules: plan.rules })
        onRevealAdvanced()
        return plan.message
      },
    },
    [form.business_rules, locked, factRes.data, actionRes.data, onPatch, onRevealAdvanced],
  )

  return null
}
