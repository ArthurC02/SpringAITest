import { useMemo, useRef, useState } from 'react'
import {
  listRuleActions,
  listRuleFacts,
  simulateBusinessRules,
  validateBusinessRules,
} from '../api/agents'
import {
  actionCatalogItems,
  actionParameters,
  canonicalRulesFromValidation,
  conditionChildren,
  conditionKind,
  createBusinessRule,
  createRuleAction,
  DEFAULT_RULE_GATE,
  defaultTypedValue,
  factCatalogItems,
  factsForGate,
  isRuleLeaf,
  metadataValues,
  newLeaf,
  operatorEntries,
  operatorNeedsValue,
  ruleUiDepthLimit,
  summarizeRule,
} from '../ruleBuilder'
import type {
  AgentBusinessRule,
  AgentBusinessRules,
  RuleAction,
  RuleActionCatalogEntry,
  RuleCondition,
  RuleFactCatalogEntry,
  RuleFactType,
  RuleGate,
  RuleOperatorCatalogEntry,
  RuleSimulationResult,
  RuleValidationResult,
} from '../types'
import { useResource } from '../hooks/useResource'
import ErrorText from './ErrorText'
import Skeleton from './Skeleton'
import { useConfirm } from './ConfirmDialog'

interface Props {
  value: AgentBusinessRules
  disabled: boolean
  onChange: (value: AgentBusinessRules) => void
}

function groupCondition(kind: 'all' | 'any', children: RuleCondition[]): RuleCondition {
  return kind === 'all' ? { all: children } : { any: children }
}

function gateLabel(gate: RuleGate): string {
  const labels: Record<string, string> = {
    preflight: 'Preflight',
    'post-context': '取得 Context 後',
    'pre-action': '動作前',
    'post-action': '動作後',
    'pre-response': '回應前',
  }
  return labels[gate] ?? gate
}

function InputForType({
  id,
  type,
  value,
  values,
  allowEmpty = false,
  ariaLabel,
  disabled,
  onChange,
}: {
  id: string
  type: RuleFactType
  value: unknown
  values?: unknown[]
  allowEmpty?: boolean
  ariaLabel?: string
  disabled: boolean
  onChange: (value: unknown) => void
}) {
  const choices = values ?? []
  if (type === 'collection' && choices.length) {
    const selected = new Set(Array.isArray(value) ? value.map(String) : [])
    return (
      <select
        id={id}
        className="input"
        aria-label={ariaLabel}
        multiple
        value={[...selected]}
        disabled={disabled}
        onChange={(event) =>
          onChange(
            Array.from(event.target.selectedOptions, (option) => {
              const choice = choices.find((item) => String(item) === option.value)
              return choice ?? option.value
            }),
          )
        }
      >
        {choices.map((choice) => (
          <option key={String(choice)} value={String(choice)}>
            {String(choice)}
          </option>
        ))}
      </select>
    )
  }
  if (choices.length) {
    return (
      <select
        id={id}
        className="input"
        aria-label={ariaLabel}
        value={String(value ?? '')}
        disabled={disabled}
        onChange={(event) => {
          const selected = choices.find((choice) => String(choice) === event.target.value)
          onChange(selected ?? event.target.value)
        }}
      >
        {allowEmpty && <option value="">未設定</option>}
        {choices.map((choice) => (
          <option key={String(choice)} value={String(choice)}>
            {String(choice)}
          </option>
        ))}
      </select>
    )
  }
  if (type === 'boolean') {
    return (
      <select
        id={id}
        className="input"
        aria-label={ariaLabel}
        value={String(value === true)}
        disabled={disabled}
        onChange={(event) => onChange(event.target.value === 'true')}
      >
        <option value="true">true</option>
        <option value="false">false</option>
      </select>
    )
  }
  if (type === 'decimal') {
    return (
      <input
        id={id}
        className="input"
        aria-label={ariaLabel}
        type="text"
        inputMode="decimal"
        autoComplete="off"
        value={typeof value === 'string' ? value : String(value ?? '')}
        disabled={disabled}
        onChange={(event) => onChange(event.target.value)}
      />
    )
  }
  if (type === 'number' || type === 'integer') {
    return (
      <input
        id={id}
        className="input"
        aria-label={ariaLabel}
        type="number"
        step={type === 'integer' ? 1 : 'any'}
        value={typeof value === 'number' ? value : 0}
        disabled={disabled}
        onChange={(event) => onChange(Number(event.target.value))}
      />
    )
  }
  if (type === 'collection') {
    return (
      <input
        id={id}
        className="input"
        aria-label={ariaLabel}
        value={Array.isArray(value) ? value.join(', ') : ''}
        placeholder="以逗號分隔"
        disabled={disabled}
        onChange={(event) =>
          onChange(
            event.target.value
              .split(',')
              .map((part) => part.trim())
              .filter(Boolean),
          )
        }
      />
    )
  }
  return (
    <input
      id={id}
      className="input"
      aria-label={ariaLabel}
      value={typeof value === 'string' ? value : String(value ?? '')}
      disabled={disabled}
      onChange={(event) => onChange(event.target.value)}
    />
  )
}

function RuleValueEditor({
  id,
  fact,
  operator,
  value,
  disabled,
  onChange,
}: {
  id: string
  fact: RuleFactCatalogEntry | undefined
  operator: RuleOperatorCatalogEntry | undefined
  value: unknown
  disabled: boolean
  onChange: (value: unknown) => void
}) {
  if (!operatorNeedsValue(operator)) return <span className="muted">此運算子不需要值</span>
  const type =
    operator?.value_type && !['none', 'same'].includes(operator.value_type)
      ? operator.value_type
      : (fact?.type ?? 'string')
  if ((operator?.value_count ?? 1) === 2) {
    const pair = Array.isArray(value)
      ? value
      : [defaultTypedValue(type), defaultTypedValue(type)]
    return (
      <div className="rule-value-pair" aria-label="值範圍">
        <InputForType
          id={`${id}-from`}
          type={type}
          value={pair[0]}
          disabled={disabled}
          onChange={(next) => onChange([next, pair[1]])}
        />
        <span>至</span>
        <InputForType
          id={`${id}-to`}
          type={type}
          value={pair[1]}
          disabled={disabled}
          onChange={(next) => onChange([pair[0], next])}
        />
      </div>
    )
  }
  return (
    <InputForType
      id={id}
      type={type}
      value={value}
      values={fact ? metadataValues(fact) : []}
      disabled={disabled}
      onChange={onChange}
    />
  )
}

function ConditionEditor({
  condition,
  depth,
  path,
  facts,
  factCatalog,
  disabled,
  removable,
  onChange,
  onRemove,
}: {
  condition: RuleCondition
  depth: number
  path: string
  facts: RuleFactCatalogEntry[]
  factCatalog: Awaited<ReturnType<typeof listRuleFacts>> | null
  disabled: boolean
  removable: boolean
  onChange: (condition: RuleCondition) => void
  onRemove: () => void
}) {
  const kind = conditionKind(condition)
  const maxDepth = ruleUiDepthLimit(factCatalog)

  function switchKind(next: 'all' | 'any' | 'not' | 'leaf') {
    if (next === kind) return
    if (next === 'leaf') {
      onChange(newLeaf(facts, factCatalog))
      return
    }
    const existing = isRuleLeaf(condition) ? condition : conditionChildren(condition)[0]
    const child = existing ?? newLeaf(facts, factCatalog)
    if (next === 'not') onChange({ not: child })
    else onChange(groupCondition(next, [child]))
  }

  if (isRuleLeaf(condition)) {
    const fact = facts.find((item) => item.name === condition.fact)
    const operators = operatorEntries(fact, factCatalog)
    const operator = operators.find((item) => item.name === condition.op)
    return (
      <div className="rule-condition rule-condition--leaf" data-rule-path={path}>
        <div className="rule-condition__toolbar">
          <span className="muted">條件</span>
          {depth < maxDepth && (
            <select
              className="input rule-condition__kind"
              aria-label={`${path} 類型`}
              value="leaf"
              disabled={disabled}
              onChange={(event) =>
                switchKind(event.target.value as 'all' | 'any' | 'not' | 'leaf')
              }
            >
              <option value="leaf">單一條件</option>
              <option value="all">AND 群組</option>
              <option value="any">OR 群組</option>
              <option value="not">NOT 群組</option>
            </select>
          )}
          {removable && (
            <button className="btn" type="button" disabled={disabled} onClick={onRemove}>
              移除條件
            </button>
          )}
        </div>
        <div className="rule-condition__row">
          <label>
            <span>Fact</span>
            <select
              className="input"
              value={condition.fact}
              disabled={disabled}
              onChange={(event) => {
                const selected = facts.find((item) => item.name === event.target.value)
                onChange(newLeaf(selected ? [selected] : [], factCatalog))
              }}
            >
              {!facts.some((item) => item.name === condition.fact) && condition.fact && (
                <option value={condition.fact}>{condition.fact}（目前 gate 不可用）</option>
              )}
              {facts.map((item) => (
                <option key={item.name} value={item.name}>
                  {item.label ?? item.name} · {item.type}
                </option>
              ))}
            </select>
          </label>
          <label>
            <span>運算子</span>
            <select
              className="input"
              value={condition.op}
              disabled={disabled}
              onChange={(event) => {
                const selected = operators.find((item) => item.name === event.target.value)
                const next: RuleCondition = { fact: condition.fact, op: event.target.value }
                if (operatorNeedsValue(selected)) {
                  next.value = defaultTypedValue(
                    selected?.value_type && !['none', 'same'].includes(selected.value_type)
                      ? selected.value_type
                      : (fact?.type ?? 'string'),
                    fact ? metadataValues(fact) : [],
                  )
                }
                onChange(next)
              }}
            >
              {!operators.some((item) => item.name === condition.op) && condition.op && (
                <option value={condition.op}>{condition.op}（catalog 已移除）</option>
              )}
              {operators.map((item) => (
                <option key={item.name} value={item.name}>
                  {item.label ?? item.name}
                </option>
              ))}
            </select>
          </label>
          <label>
            <span>值</span>
            <RuleValueEditor
              id={`${path}-value`}
              fact={fact}
              operator={operator}
              value={condition.value}
              disabled={disabled}
              onChange={(value) => onChange({ ...condition, value })}
            />
          </label>
        </div>
        {fact && (
          <p className="muted rule-condition__meta">
            來源：{fact.provenance} · 信任層級：{fact.trustTier ?? fact.trust_tier ?? '未標示'}
          </p>
        )}
      </div>
    )
  }

  const children = conditionChildren(condition)
  return (
    <fieldset className="rule-condition rule-condition--group" data-rule-path={path}>
      <legend>{kind === 'all' ? '全部成立（AND）' : kind === 'any' ? '任一成立（OR）' : '不成立（NOT）'}</legend>
      <div className="rule-condition__toolbar">
        <select
          className="input rule-condition__kind"
          aria-label={`${path} 群組類型`}
          value={kind}
          disabled={disabled}
          onChange={(event) => switchKind(event.target.value as 'all' | 'any' | 'not' | 'leaf')}
        >
          <option value="leaf">單一條件</option>
          <option value="all">AND 群組</option>
          <option value="any">OR 群組</option>
          <option value="not">NOT 群組</option>
        </select>
        {removable && (
          <button className="btn" type="button" disabled={disabled} onClick={onRemove}>
            移除群組
          </button>
        )}
      </div>
      <div className="rule-condition__children">
        {children.map((child, index) => (
          <ConditionEditor
            key={`${path}-${index}`}
            condition={child}
            depth={depth + 1}
            path={
              kind === 'not' ? `${path}.not` : `${path}.${kind === 'all' ? 'all' : 'any'}[${index}]`
            }
            facts={facts}
            factCatalog={factCatalog}
            disabled={disabled}
            removable={kind !== 'not' && children.length > 1}
            onChange={(next) => {
              if (kind === 'not') onChange({ not: next })
              else {
                const nextChildren = children.map((item, itemIndex) =>
                  itemIndex === index ? next : item,
                )
                onChange(groupCondition(kind === 'all' ? 'all' : 'any', nextChildren))
              }
            }}
            onRemove={() => {
              if (kind === 'not') return
              onChange(
                groupCondition(
                  kind === 'all' ? 'all' : 'any',
                  children.filter((_, itemIndex) => itemIndex !== index),
                ),
              )
            }}
          />
        ))}
      </div>
      {kind !== 'not' && (
        <button
          className="btn"
          type="button"
          disabled={disabled}
          onClick={() =>
            onChange(
              groupCondition(kind === 'all' ? 'all' : 'any', [
                ...children,
                newLeaf(facts, factCatalog),
              ]),
            )
          }
        >
          ＋ 新增條件
        </button>
      )}
      {depth >= maxDepth && <p className="muted">已達 UI 巢狀上限（{maxDepth} 層）。</p>}
    </fieldset>
  )
}

function ActionEditor({
  action,
  index,
  path,
  catalog,
  disabled,
  onChange,
  onRemove,
}: {
  action: RuleAction
  index: number
  path: string
  catalog: RuleActionCatalogEntry[]
  disabled: boolean
  onChange: (action: RuleAction) => void
  onRemove: () => void
}) {
  const metadata = catalog.find((item) => item.name === action.action)
  return (
    <div className="rule-action" data-rule-path={path}>
      <label>
        <span>動作 {index + 1}</span>
        <select
          className="input"
          value={action.action}
          disabled={disabled}
          onChange={(event) => {
            const selected = catalog.find((item) => item.name === event.target.value)
            onChange(selected ? createRuleAction(selected) : { action: event.target.value })
          }}
        >
          {!metadata && action.action && (
            <option value={action.action}>{action.action}（catalog 已移除）</option>
          )}
          {catalog.map((item) => (
            <option key={item.name} value={item.name}>
              {item.label ?? item.name}
            </option>
          ))}
        </select>
      </label>
      {actionParameters(metadata).map((parameter) => (
        <label key={parameter.name}>
          <span>
            {parameter.label ?? parameter.name}
            {parameter.required ? ' *' : ''}
          </span>
          <InputForType
            id={`${path}-${parameter.name}`}
            type={parameter.type}
            value={action[parameter.name]}
            values={metadataValues(parameter)}
            allowEmpty={!parameter.required}
            disabled={disabled}
            onChange={(value) => {
              if (
                !parameter.required &&
                (value === '' || (Array.isArray(value) && value.length === 0))
              ) {
                const next = { ...action }
                delete next[parameter.name]
                onChange(next)
                return
              }
              onChange({ ...action, [parameter.name]: value })
            }}
          />
        </label>
      ))}
      <button className="btn" type="button" disabled={disabled} onClick={onRemove}>
        移除動作
      </button>
    </div>
  )
}

function templateRule(
  facts: RuleFactCatalogEntry[],
  factCatalog: Awaited<ReturnType<typeof listRuleFacts>> | null,
  actions: RuleActionCatalogEntry[],
  factName: string,
  operatorName: string,
  value: unknown,
  actionName: string,
  actionParameter?: [string, unknown],
): AgentBusinessRule | null {
  const fact = facts.find((item) => item.name === factName)
  const actionMetadata = actions.find((item) => item.name === actionName)
  const operator = operatorEntries(fact, factCatalog).find((item) => item.name === operatorName)
  if (!fact || !operator || !actionMetadata) return null
  const rule = createBusinessRule([fact], factCatalog, [actionMetadata], 0)
  const action = createRuleAction(actionMetadata)
  if (actionParameter) action[actionParameter[0]] = actionParameter[1]
  return {
    ...rule,
    when: { fact: factName, op: operatorName, value },
    then: [action],
  }
}

export default function BusinessRuleEditor({ value, disabled, onChange }: Props) {
  const confirm = useConfirm()
  const factResource = useResource(listRuleFacts)
  const actionResource = useResource(listRuleActions)
  const facts = useMemo(() => factCatalogItems(factResource.data), [factResource.data])
  const actions = useMemo(() => actionCatalogItems(actionResource.data), [actionResource.data])
  const gate: RuleGate = DEFAULT_RULE_GATE
  const valueRef = useRef(value)
  valueRef.current = value
  const factsInputRef = useRef<Record<string, unknown>>({})
  const validationGenerationRef = useRef(0)
  const simulationGenerationRef = useRef(0)
  const [validation, setValidation] = useState<RuleValidationResult | null>(null)
  const [simulation, setSimulation] = useState<RuleSimulationResult | null>(null)
  const [factsInput, setFactsInput] = useState<Record<string, unknown>>({})
  const [validating, setValidating] = useState(false)
  const [simulating, setSimulating] = useState(false)
  const [requestError, setRequestError] = useState<string | null>(null)
  const availableFacts = useMemo(() => factsForGate(facts, gate), [facts, gate])

  function change(next: AgentBusinessRules) {
    validationGenerationRef.current += 1
    simulationGenerationRef.current += 1
    setValidating(false)
    setSimulating(false)
    onChange(next)
    setValidation(null)
    setSimulation(null)
    setRequestError(null)
  }

  function changeFacts(
    update: (current: Record<string, unknown>) => Record<string, unknown>,
  ) {
    setFactsInput((current) => {
      const next = update(current)
      factsInputRef.current = next
      return next
    })
    simulationGenerationRef.current += 1
    setSimulating(false)
    setSimulation(null)
    setRequestError(null)
  }

  function replaceRule(index: number, rule: AgentBusinessRule) {
    change({ ...value, rules: value.rules.map((item, i) => (i === index ? rule : item)) })
  }

  async function removeRule(index: number, rule: AgentBusinessRule) {
    if (
      !(await confirm(`刪除規則「${rule.name || rule.id}」？此變更要儲存草稿後才會生效。`, {
        danger: true,
        confirmLabel: '刪除規則',
      }))
    )
      return
    change({ ...value, rules: value.rules.filter((_, i) => i !== index) })
  }

  async function validate() {
    const generation = ++validationGenerationRef.current
    const requestedValue = JSON.stringify(value)
    setValidating(true)
    setRequestError(null)
    try {
      const result = await validateBusinessRules(gate, value)
      if (
        generation !== validationGenerationRef.current ||
        requestedValue !== JSON.stringify(valueRef.current)
      ) {
        return
      }
      setValidation(result)
      const canonical = canonicalRulesFromValidation(result)
      if (
        result.valid &&
        canonical &&
        JSON.stringify(canonical) !== requestedValue
      ) {
        simulationGenerationRef.current += 1
        setSimulating(false)
        setSimulation(null)
        valueRef.current = canonical
        onChange(canonical)
      }
    } catch (error) {
      if (
        generation === validationGenerationRef.current &&
        requestedValue === JSON.stringify(valueRef.current)
      ) {
        setRequestError((error as Error).message)
      }
    } finally {
      if (generation === validationGenerationRef.current) {
        setValidating(false)
      }
    }
  }

  async function simulate() {
    const generation = ++simulationGenerationRef.current
    const requestedValue = JSON.stringify(value)
    const requestedFacts = JSON.stringify(factsInput)
    setSimulating(true)
    setRequestError(null)
    try {
      const result = await simulateBusinessRules(gate, value, factsInput)
      if (
        generation !== simulationGenerationRef.current ||
        requestedValue !== JSON.stringify(valueRef.current) ||
        requestedFacts !== JSON.stringify(factsInputRef.current)
      ) {
        return
      }
      setSimulation(result)
    } catch (error) {
      if (
        generation === simulationGenerationRef.current &&
        requestedValue === JSON.stringify(valueRef.current) &&
        requestedFacts === JSON.stringify(factsInputRef.current)
      ) {
        setRequestError((error as Error).message)
      }
    } finally {
      if (generation === simulationGenerationRef.current) {
        setSimulating(false)
      }
    }
  }

  const templates = [
    {
      name: '高額退款需主管核准',
      rule: templateRule(
        availableFacts,
        factResource.data ?? null,
        actions,
        'action.amount',
        'gt',
        '5000',
        'require_approval',
        ['role', 'ADMIN'],
      ),
    },
    {
      name: '低信心時要求更多 Context',
      rule: templateRule(
        availableFacts,
        factResource.data ?? null,
        actions,
        'context.confidence',
        'lt',
        0.7,
        'require_context',
      ),
    },
    {
      name: '缺少引用時拒絕回應',
      rule: templateRule(
        availableFacts,
        factResource.data ?? null,
        actions,
        'result.has_citations',
        'is_false',
        undefined,
        'deny',
      ),
    },
  ]

  return (
    <section className="agent-block business-rules">
      <div className="business-rules__head">
        <div>
          <h4 className="agent-block__title">Business Rules 公式編輯器</h4>
          <p className="muted">
            編輯內容直接就是 canonical AST；執行語意、型別檢查與模擬結果以 server 為準。
          </p>
        </div>
        <div>
          <span className="muted">政策 Gate</span>
          <strong> {gateLabel(gate)}</strong>
          <p className="muted">v1 Agent 規則固定在動作前執行；其他 Gate 將由版本化契約另行提供。</p>
        </div>
      </div>

      <ErrorText msg={factResource.error ?? actionResource.error ?? requestError} />
      {!factResource.data && !factResource.error ? (
        <Skeleton rows={3} />
      ) : (
        <>
          <div className="rule-templates" aria-label="代表性規則範本">
            <span className="muted">從範本開始：</span>
            {templates.map((template) => (
              <button
                key={template.name}
                className="btn"
                type="button"
                disabled={disabled || !template.rule}
                title={!template.rule ? '目前 Gate 的 catalog 不支援此範本' : undefined}
                onClick={() => {
                  if (!template.rule) return
                  change({
                    ...value,
                    rules: [
                      ...value.rules,
                      {
                        ...template.rule,
                        name: template.name,
                        priority: Math.max(0, 100 - value.rules.length * 10),
                      },
                    ],
                  })
                }}
              >
                {template.name}
              </button>
            ))}
          </div>

          {value.rules.length === 0 ? (
            <p className="agent-set__empty" role="note">
              尚無 Business Rule。空 rules 代表不會產生任何規則決策。
            </p>
          ) : (
            <ol className="rule-list">
              {value.rules.map((rule, ruleIndex) => (
                <li className="rule-card" key={rule.id}>
                  <div className="rule-card__head">
                    <label className="agent-check">
                      <input
                        type="checkbox"
                        checked={rule.enabled}
                        disabled={disabled}
                        onChange={(event) =>
                          replaceRule(ruleIndex, { ...rule, enabled: event.target.checked })
                        }
                      />
                      啟用
                    </label>
                    <div className="rule-card__identity">
                      <label>
                        <span>規則名稱</span>
                        <input
                          className="input"
                          value={rule.name}
                          disabled={disabled}
                          onChange={(event) =>
                            replaceRule(ruleIndex, { ...rule, name: event.target.value })
                          }
                        />
                      </label>
                      <label>
                        <span>穩定 ID</span>
                        <input
                          className="input"
                          value={rule.id}
                          disabled={disabled}
                          onChange={(event) =>
                            replaceRule(ruleIndex, { ...rule, id: event.target.value })
                          }
                        />
                      </label>
                      <label>
                        <span>優先序</span>
                        <input
                          className="input"
                          type="number"
                          step={1}
                          value={rule.priority}
                          disabled={disabled}
                          onChange={(event) =>
                            replaceRule(ruleIndex, {
                              ...rule,
                              priority: Number(event.target.value),
                            })
                          }
                        />
                      </label>
                    </div>
                    <button
                      className="btn btn--danger"
                      type="button"
                      disabled={disabled}
                      onClick={() => void removeRule(ruleIndex, rule)}
                    >
                      刪除規則
                    </button>
                  </div>

                  <h5>條件</h5>
                  <ConditionEditor
                    condition={rule.when}
                    depth={1}
                    path={`rules[${ruleIndex}].when`}
                    facts={availableFacts}
                    factCatalog={factResource.data ?? null}
                    disabled={disabled}
                    removable={false}
                    onChange={(when) => replaceRule(ruleIndex, { ...rule, when })}
                    onRemove={() => undefined}
                  />

                  <h5>動作</h5>
                  <div className="rule-actions">
                    {rule.then.map((action, actionIndex) => (
                      <ActionEditor
                        key={`${rule.id}-action-${actionIndex}`}
                        action={action}
                        index={actionIndex}
                        path={`rules[${ruleIndex}].then[${actionIndex}]`}
                        catalog={actions}
                        disabled={disabled}
                        onChange={(next) =>
                          replaceRule(ruleIndex, {
                            ...rule,
                            then: rule.then.map((item, i) => (i === actionIndex ? next : item)),
                          })
                        }
                        onRemove={() =>
                          replaceRule(ruleIndex, {
                            ...rule,
                            then: rule.then.filter((_, i) => i !== actionIndex),
                          })
                        }
                      />
                    ))}
                  </div>
                  <button
                    className="btn"
                    type="button"
                    disabled={disabled || actions.length === 0}
                    onClick={() =>
                      replaceRule(ruleIndex, {
                        ...rule,
                        then: [...rule.then, createRuleAction(actions[0])],
                      })
                    }
                  >
                    ＋ 新增動作
                  </button>

                  <h5>Unknown 安全處置</h5>
                  <p className="muted">
                    Fact 缺少、無效或目前不可取得時使用；正式 Validator 會補上 fail-closed 預設。
                  </p>
                  <div className="rule-actions">
                    {(rule.onUnknown ?? []).map((action, actionIndex) => (
                      <ActionEditor
                        key={`${rule.id}-unknown-${actionIndex}`}
                        action={action}
                        index={actionIndex}
                        path={`rules[${ruleIndex}].onUnknown[${actionIndex}]`}
                        catalog={actions}
                        disabled={disabled}
                        onChange={(next) =>
                          replaceRule(ruleIndex, {
                            ...rule,
                            onUnknown: (rule.onUnknown ?? []).map((item, i) =>
                              i === actionIndex ? next : item,
                            ),
                          })
                        }
                        onRemove={() =>
                          replaceRule(ruleIndex, {
                            ...rule,
                            onUnknown: (rule.onUnknown ?? []).filter(
                              (_, i) => i !== actionIndex,
                            ),
                          })
                        }
                      />
                    ))}
                  </div>
                  <button
                    className="btn"
                    type="button"
                    disabled={disabled || actions.length === 0}
                    onClick={() =>
                      replaceRule(ruleIndex, {
                        ...rule,
                        onUnknown: [
                          ...(rule.onUnknown ?? []),
                          createRuleAction(
                            actions.find((item) => item.name === 'deny') ?? actions[0],
                          ),
                        ],
                      })
                    }
                  >
                    ＋ 新增 Unknown 處置
                  </button>

                  <p className="rule-summary">
                    <strong>自然語言摘要（非執行權威）：</strong>
                    {summarizeRule(rule, facts, actions)}
                  </p>
                </li>
              ))}
            </ol>
          )}

          <div className="business-rules__actions">
            <button
              className="btn btn--info"
              type="button"
              disabled={disabled || availableFacts.length === 0 || actions.length === 0}
              onClick={() =>
                change({
                  ...value,
                  rules: [
                    ...value.rules,
                    createBusinessRule(
                      availableFacts,
                      factResource.data ?? null,
                      actions,
                      value.rules.length,
                    ),
                  ],
                })
              }
            >
              ＋ 新增空白規則
            </button>
            <button
              className="btn"
              type="button"
              disabled={validating || simulating}
              onClick={() => void validate()}
            >
              {validating ? '規則驗證中…' : '以正式 Validator 驗證'}
            </button>
          </div>

          {validation && (
            <div
              className={validation.valid ? 'notice-text' : 'agent-errors'}
              role={validation.valid ? 'status' : 'alert'}
            >
              {validation.valid ? (
                <span>Business Rules 驗證通過。</span>
              ) : (
                <>
                  <p>驗證失敗：</p>
                  <ul className="rule-validation-list">
                    {validation.errors.map((issue, index) => (
                      <li key={`${issue.path}-${issue.code ?? ''}-${index}`}>
                        <code>{issue.path || '$'}</code>
                        {issue.code ? ` [${issue.code}]` : ''}：{issue.message}
                      </li>
                    ))}
                  </ul>
                </>
              )}
            </div>
          )}

          <details className="rule-advanced">
            <summary>進階：唯讀 canonical JSON</summary>
            <pre aria-label="Business Rules canonical JSON">
              {JSON.stringify(value, null, 2)}
            </pre>
          </details>

          <details className="rule-simulator">
            <summary>Simulator（使用正式 evaluator，不會呼叫真實工具）</summary>
            <div className="rule-simulator__facts">
              {availableFacts
                .filter((fact) => fact.visibleValue !== false && fact.visible_value !== false)
                .map((fact) => {
                  const provided = Object.hasOwn(factsInput, fact.name)
                  return (
                    <div className="rule-simulator__fact" key={fact.name}>
                      <label className="agent-check">
                        <input
                          type="checkbox"
                          checked={provided}
                          disabled={simulating}
                          onChange={(event) =>
                            changeFacts((current) => {
                              if (event.target.checked) {
                                return {
                                  ...current,
                                  [fact.name]: defaultTypedValue(
                                    fact.type,
                                    metadataValues(fact),
                                  ),
                                }
                              }
                              const next = { ...current }
                              delete next[fact.name]
                              return next
                            })
                          }
                        />
                        提供 {fact.label ?? fact.name} <small>({fact.type})</small>
                      </label>
                      <InputForType
                        id={`sim-${fact.name}`}
                        ariaLabel={`${fact.label ?? fact.name} 模擬值`}
                        type={fact.type}
                        value={
                          factsInput[fact.name] ??
                          defaultTypedValue(fact.type, metadataValues(fact))
                        }
                        values={metadataValues(fact)}
                        disabled={simulating || !provided}
                        onChange={(factValue) =>
                          changeFacts((current) => ({ ...current, [fact.name]: factValue }))
                        }
                      />
                    </div>
                  )
                })}
            </div>
            <button
              className="btn btn--info"
              type="button"
              disabled={simulating || validating}
              onClick={() => void simulate()}
            >
              {simulating ? '模擬中…' : '執行模擬'}
            </button>
            {simulation && (
              <div
                className={simulation.valid ? 'rule-simulation-result' : 'agent-errors'}
                role="status"
              >
                {simulation.valid ? (
                  <>
                    <dl>
                      <div>
                        <dt>決策</dt>
                        <dd>{JSON.stringify(simulation.simulation?.decision ?? null)}</dd>
                      </div>
                      <div>
                        <dt>命中規則</dt>
                        <dd>
                          {JSON.stringify(
                            simulation.simulation?.matchedRules ??
                              simulation.simulation?.matched_rules ??
                              [],
                          )}
                        </dd>
                      </div>
                    </dl>
                    <details>
                      <summary>Decision trace / 完整結果</summary>
                      <pre>{JSON.stringify(simulation.simulation ?? simulation, null, 2)}</pre>
                    </details>
                  </>
                ) : (
                  <ul>
                    {simulation.errors.map((issue, index) => (
                      <li key={`${issue.path}-${index}`}>
                        <code>{issue.path || '$'}</code>：{issue.message}
                      </li>
                    ))}
                  </ul>
                )}
              </div>
            )}
          </details>
        </>
      )}
    </section>
  )
}
