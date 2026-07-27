import type {
  AgentBusinessRule,
  AgentBusinessRules,
  RuleAction,
  RuleActionCatalogEntry,
  RuleActionParameter,
  RuleCatalogEnvelope,
  RuleCondition,
  RuleConditionLeaf,
  RuleFactCatalogEntry,
  RuleGate,
  RuleOperatorCatalogEntry,
} from './types'

export const DEFAULT_RULE_GATE: RuleGate = 'pre-action'
/** Fail-safe only: used when the catalog omits `limits.maxDepth` or reports a nonsensical value. */
const FALLBACK_RULE_UI_DEPTH = 3

/**
 * UI nesting budget. The catalog is the source of truth (server validate/publish stays the
 * authority), so an oversized catalog value deliberately just widens authoring.
 */
export function ruleUiDepthLimit(
  response:
    | RuleFactCatalogEntry[]
    | RuleCatalogEnvelope<RuleFactCatalogEntry>
    | null
    | undefined,
): number {
  const limit = response && !Array.isArray(response) ? response.limits?.maxDepth : undefined
  return typeof limit === 'number' && Number.isInteger(limit) && limit > 0
    ? limit
    : FALLBACK_RULE_UI_DEPTH
}

export function factCatalogItems(
  response:
    | RuleFactCatalogEntry[]
    | RuleCatalogEnvelope<RuleFactCatalogEntry>
    | null
    | undefined,
): RuleFactCatalogEntry[] {
  if (Array.isArray(response)) return response
  return response?.facts ?? response?.items ?? []
}

export function actionCatalogItems(
  response:
    | RuleActionCatalogEntry[]
    | RuleCatalogEnvelope<RuleActionCatalogEntry>
    | null
    | undefined,
): RuleActionCatalogEntry[] {
  if (Array.isArray(response)) return response
  return response?.actions ?? response?.items ?? []
}

export function factsForGate(facts: RuleFactCatalogEntry[], gate: RuleGate): RuleFactCatalogEntry[] {
  return facts.filter((fact) => !fact.gates?.length || fact.gates.includes(gate))
}

export function operatorEntries(
  fact: RuleFactCatalogEntry | undefined,
  response:
    | RuleFactCatalogEntry[]
    | RuleCatalogEnvelope<RuleFactCatalogEntry>
    | null
    | undefined,
): RuleOperatorCatalogEntry[] {
  const operators = fact?.operators
  if (operators?.length) {
    return operators.map((operator) => {
      const entry =
        typeof operator === 'string'
          ? response && !Array.isArray(response)
            ? response.operators?.find((item) => item.name === operator) ?? { name: operator }
            : { name: operator }
          : operator
      return normalizeOperator(entry, fact?.type)
    })
  }
  return response && !Array.isArray(response)
    ? (response.operators ?? [])
        .filter((operator) => {
          const compatible = operator.compatibleFactTypes ?? operator.compatible_fact_types
          return !fact || !compatible?.length || compatible.includes(fact.type)
        })
        .map((operator) => normalizeOperator(operator, fact?.type))
    : []
}

function normalizeOperator(
  operator: RuleOperatorCatalogEntry,
  factType?: string,
): RuleOperatorCatalogEntry {
  if (!operator.value) return operator
  const catalogTypes = operator.value.types ?? []
  const resolvedType =
    factType && catalogTypes.includes(factType)
      ? factType
      : (catalogTypes[0] ?? 'same')
  return {
    ...operator,
    value_type:
      operator.value_type ??
      (operator.value.kind === 'none'
        ? 'none'
        : operator.value.kind === 'list'
          ? 'collection'
          : resolvedType),
    value_count:
      operator.value_count ??
      (operator.value.kind === 'none' ? 0 : operator.value.kind === 'range' ? 2 : 1),
  }
}

export function actionParameters(action: RuleActionCatalogEntry | undefined): RuleActionParameter[] {
  if (!action?.parameters) return []
  if (Array.isArray(action.parameters)) return action.parameters
  return Object.entries(action.parameters).map(([name, parameter]) => ({ name, ...parameter }))
}

export function isRuleLeaf(condition: RuleCondition): condition is RuleConditionLeaf {
  return 'fact' in condition
}

export function conditionKind(condition: RuleCondition): 'all' | 'any' | 'not' | 'leaf' {
  if ('all' in condition) return 'all'
  if ('any' in condition) return 'any'
  if ('not' in condition) return 'not'
  return 'leaf'
}

export function conditionChildren(condition: RuleCondition): RuleCondition[] {
  if ('all' in condition) return condition.all
  if ('any' in condition) return condition.any
  if ('not' in condition) return [condition.not]
  return []
}

/** fact／action 參數的允許值:catalog 兩種命名並存(camel/snake),一律由此讀取。 */
export function metadataValues(metadata: {
  enumValues?: unknown[]
  enum_values?: unknown[]
  values?: unknown[]
}): unknown[] {
  return metadata.enumValues ?? metadata.enum_values ?? metadata.values ?? []
}

export function defaultTypedValue(type: string, values: unknown[] = []): unknown {
  if (type === 'collection') return []
  if (values.length > 0) return values[0]
  if (type === 'boolean') return false
  if (type === 'decimal') return '0'
  if (type === 'number' || type === 'integer') return 0
  return ''
}

export function operatorNeedsValue(operator: RuleOperatorCatalogEntry | undefined): boolean {
  if (operator?.value_count === 0 || operator?.value_type === 'none') return false
  // Backward-compatible rendering for a catalog that exposes operator ids only.
  return !['is_true', 'is_false', 'exists', 'not_exists', 'is_empty'].includes(
    operator?.name ?? '',
  )
}

export function newLeaf(
  facts: RuleFactCatalogEntry[],
  catalog:
    | RuleFactCatalogEntry[]
    | RuleCatalogEnvelope<RuleFactCatalogEntry>
    | null
    | undefined,
): RuleConditionLeaf {
  const fact = facts[0]
  const operator = operatorEntries(fact, catalog)[0]
  const leaf: RuleConditionLeaf = {
    fact: fact?.name ?? '',
    op: operator?.name ?? '',
  }
  if (operatorNeedsValue(operator)) {
    leaf.value = defaultTypedValue(
      operator?.value_type && !['none', 'same'].includes(operator.value_type)
        ? operator.value_type
        : (fact?.type ?? 'string'),
      fact?.enumValues ?? fact?.enum_values ?? fact?.values,
    )
  }
  return leaf
}

function id(): string {
  return globalThis.crypto?.randomUUID?.() ?? `rule-${Date.now()}`
}

export function createBusinessRule(
  facts: RuleFactCatalogEntry[],
  factCatalog:
    | RuleFactCatalogEntry[]
    | RuleCatalogEnvelope<RuleFactCatalogEntry>
    | null
    | undefined,
  actions: RuleActionCatalogEntry[],
  position: number,
): AgentBusinessRule {
  const action = actions[0]
  const deny = actions.find((item) => item.name === 'deny')
  return {
    id: id(),
    name: `規則 ${position + 1}`,
    enabled: true,
    priority: Math.max(0, 100 - position * 10),
    when: newLeaf(facts, factCatalog),
    then: action ? [createRuleAction(action)] : [],
    onUnknown: deny
      ? [{ ...createRuleAction(deny), reason: 'required fact is missing, invalid, or unavailable' }]
      : [],
  }
}

export function createRuleAction(catalog: RuleActionCatalogEntry): RuleAction {
  const result: RuleAction = { action: catalog.name }
  for (const parameter of actionParameters(catalog)) {
    if (!parameter.required) continue
    result[parameter.name] = defaultTypedValue(
      parameter.type,
      parameter.enumValues ?? parameter.enum_values ?? parameter.values,
    )
  }
  return result
}

function displayValue(value: unknown): string {
  if (Array.isArray(value)) return value.map(displayValue).join('、')
  if (typeof value === 'string') return `「${value || '…'}」`
  if (value === undefined) return ''
  return JSON.stringify(value)
}

export function summarizeRule(
  rule: AgentBusinessRule,
  facts: RuleFactCatalogEntry[],
  actions: RuleActionCatalogEntry[],
): string {
  function conditionText(condition: RuleCondition): string {
    if (isRuleLeaf(condition)) {
      const fact = facts.find((item) => item.name === condition.fact)
      const operator = operatorEntries(fact, facts).find((item) => item.name === condition.op)
      const suffix = operatorNeedsValue(operator) ? ` ${displayValue(condition.value)}` : ''
      return `${fact?.label ?? (condition.fact || '未選 fact')} ${operator?.label ?? (condition.op || '未選運算子')}${suffix}`
    }
    if ('not' in condition) return `不是（${conditionText(condition.not)}）`
    const children = 'all' in condition ? condition.all : condition.any
    const joiner = 'all' in condition ? ' 且 ' : ' 或 '
    return children.length ? children.map(conditionText).join(joiner) : '尚無條件'
  }

  const actionText = rule.then.length
    ? rule.then
        .map((action) => {
          const metadata = actions.find((item) => item.name === action.action)
          const params = Object.entries(action)
            .filter(([key]) => key !== 'action')
            .map(([key, value]) => `${key}=${displayValue(value)}`)
            .join('、')
          return `${metadata?.label ?? (action.action || '未選動作')}${params ? `（${params}）` : ''}`
        })
        .join('；')
    : '尚無動作'
  const unknownText = rule.onUnknown?.length
    ? ` 無法判斷時：${rule.onUnknown.map((action) => action.action).join('、')}。`
    : ''
  return `如果 ${conditionText(rule.when)}，則 ${actionText}。${unknownText}`
}

export function canonicalRulesFromValidation(
  result: { canonicalRuleSet?: AgentBusinessRules; canonical_rule_set?: AgentBusinessRules },
): AgentBusinessRules | undefined {
  return result.canonicalRuleSet ?? result.canonical_rule_set
}
