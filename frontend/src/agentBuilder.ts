import type {
  AgentBusinessRules,
  AgentDraft,
  AgentOutputContract,
  AgentRuntimeLimits,
  SkillCatalogEntry,
} from './types'

const EMPTY_RULES: AgentBusinessRules = { version: 1, rules: [] }

const EMPTY_LIMITS: AgentRuntimeLimits = {
  max_tool_rounds: 0,
  max_context_rounds: 0,
  timeout_seconds: 0,
  token_budget: 0,
  step_budget: 0,
}

const AUDIENCE_GROUP_ID = /^[a-z0-9](?:[a-z0-9._-]{0,126}[a-z0-9])?$/

/** Authoring 相容舊 bare role，但前端送出與顯示一律使用 namespaced principals。 */
export function normalizeAudiencePrincipals(value: unknown): string[] {
  return strings(value)
    .map((entry) => entry.trim())
    .filter(Boolean)
    .map((entry) => {
      if (entry === 'USER' || entry === 'ADMIN') return `role:${entry}`
      if (entry.startsWith('role:')) {
        const role = entry.slice('role:'.length).toUpperCase()
        if (role === 'USER' || role === 'ADMIN') return `role:${role}`
      }
      return entry
    })
    .filter((entry, index, all) => all.indexOf(entry) === index)
}

export function audiencePrincipalError(audience: readonly string[]): string | null {
  for (const entry of audience) {
    if (entry === '*' || entry.includes('*')) {
      return 'Audience 不允許 wildcard；請明確選擇 role 或 group。'
    }
    if (entry === 'role:USER' || entry === 'role:ADMIN') continue
    if (entry.startsWith('group:') && AUDIENCE_GROUP_ID.test(entry.slice('group:'.length))) continue
    return `Audience principal 格式錯誤：${entry}；只接受 role:USER、role:ADMIN 或 group:<canonical-id>。`
  }
  return null
}

/** 建立精靈預設 audience 依產品契約開給同 tenant 的 USER 與 ADMIN。 */
export function createEmptyAgentDraft(): AgentDraft {
  return {
    name: '',
    slug: '',
    description: '',
    system_prompt: '',
    execution_roles: ['worker'],
    capabilities: [],
    output_contract: {},
    audience: ['role:USER', 'role:ADMIN'],
    allowed_tools: [],
    knowledge_sources: [],
    skill_bindings: [],
    business_rules: { ...EMPTY_RULES, rules: [] },
    runtime_limits: { ...EMPTY_LIMITS },
  }
}

function strings(value: unknown, fallback: string[] = []): string[] {
  return Array.isArray(value)
    ? value.filter((item): item is string => typeof item === 'string')
    : [...fallback]
}

function object(value: unknown): Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : {}
}

function finiteNumber(value: unknown): number {
  return typeof value === 'number' && Number.isFinite(value) ? value : 0
}

/**
 * AgentResponse 把 identity 放在頂層，draft 只放 canonical definition。這裡合併兩者並
 * 補齊 additive 欄位，讓舊資料／舊 server 仍能安全進入完整 Builder。
 */
export function normalizeAgentDraft(
  draft: Partial<AgentDraft> | null | undefined,
  identity: Pick<AgentDraft, 'name' | 'slug' | 'description'>,
): AgentDraft {
  const source = draft ?? {}
  const limits = object(source.runtime_limits)
  const rules = object(source.business_rules)
  const outputContract = object(source.output_contract) as AgentOutputContract
  const workflow = object(source.runtime_workflow)
  const workflowId = typeof workflow.id === 'string' ? workflow.id : ''
  const workflowRevision = finiteNumber(workflow.revision)

  return {
    ...source,
    ...identity,
    system_prompt: typeof source.system_prompt === 'string' ? source.system_prompt : '',
    execution_roles: strings(source.execution_roles).filter(
      (role): role is 'worker' | 'verifier' => role === 'worker' || role === 'verifier',
    ),
    capabilities: strings(source.capabilities),
    output_contract: outputContract,
    audience: normalizeAudiencePrincipals(source.audience ?? ['role:USER', 'role:ADMIN']),
    allowed_tools: strings(source.allowed_tools),
    knowledge_sources: strings(source.knowledge_sources),
    skill_bindings: Array.isArray(source.skill_bindings)
      ? source.skill_bindings.filter(
          (binding): binding is AgentDraft['skill_bindings'][number] =>
            binding !== null &&
            typeof binding === 'object' &&
            typeof (binding as { skill?: unknown }).skill === 'string',
        )
      : [],
    business_rules: {
      ...rules,
      version: finiteNumber(rules.version) || 1,
      rules: Array.isArray(rules.rules) ? rules.rules : [],
    },
    runtime_limits: {
      max_tool_rounds: finiteNumber(limits.max_tool_rounds),
      max_context_rounds: finiteNumber(limits.max_context_rounds),
      timeout_seconds: finiteNumber(limits.timeout_seconds),
      token_budget: finiteNumber(limits.token_budget),
      step_budget: finiteNumber(limits.step_budget),
    },
    runtime_workflow:
      workflowId && workflowRevision > 0
        ? { id: workflowId, revision: workflowRevision }
        : undefined,
  }
}

/** 只有 server 明確標成 bindable 的 Skill 才能固定進 immutable Agent revision。 */
export function isSkillBindable(skill: SkillCatalogEntry): boolean {
  return skill.bindable
}

export function businessRuleCount(rules: AgentBusinessRules): number {
  return Array.isArray(rules.rules) ? rules.rules.length : 0
}

/** 一般 publish 依目前 published revision 產生下一版；restore 有自己的 server 流程。 */
export function nextAgentRevision(publishedRevision: number | null): number {
  return (publishedRevision ?? 0) + 1
}

export function isAgentEditorLocked(
  readOnly: boolean,
  busy: boolean,
  conflict: boolean,
): boolean {
  return readOnly || busy || conflict
}

export function parseOutputContract(
  text: string,
): { value: AgentOutputContract | null; error: string | null } {
  try {
    const value = JSON.parse(text) as unknown
    if (value === null || typeof value !== 'object' || Array.isArray(value)) {
      return { value: null, error: '輸出 contract 必須是 JSON object。' }
    }
    return { value: value as AgentOutputContract, error: null }
  } catch {
    return { value: null, error: 'JSON 格式不正確。' }
  }
}
