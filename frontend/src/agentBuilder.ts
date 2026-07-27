import { object } from './wire'
import type {
  AgentBusinessRules,
  AgentDraft,
  AgentOutputContract,
  AgentRuntimeLimits,
  ConfigEntry,
  SkillCatalogEntry,
} from './types'

const EMPTY_RULES: AgentBusinessRules = { version: 1, rules: [] }

/**
 * 新建 Agent 的 runtime limits 起始值(可由系統設定 `agent.defaults.*` 覆寫)。
 * 數值刻意等於 workflow `app/settings.py` 的 runtime_default_*,所以「用預設值建立」
 * 與舊行為(全 0 → server 補預設)執行結果相同,只是使用者現在看得到、改得動。
 */
export const AGENT_DEFAULT_RUNTIME_LIMITS: AgentRuntimeLimits = {
  max_tool_rounds: 8,
  max_context_rounds: 3,
  timeout_seconds: 60,
  token_budget: 16000,
  step_budget: 24,
}

/** 新建 Agent 的 audience 起始值:同租戶 USER + ADMIN(不是空集合,避免建完沒人能用)。 */
export const AGENT_DEFAULT_AUDIENCE: readonly string[] = ['role:USER', 'role:ADMIN']

/** 新建 Agent 的 System Prompt 骨架(可由系統設定 `agent.defaults.system_prompt` 覆寫)。 */
export const AGENT_DEFAULT_SYSTEM_PROMPT = `你是一位協助處理內部業務問題的助理。

目標:根據使用者的問題,從被授權的知識來源與 Skill 取得依據,再給出可執行的答案。
語氣:專業、簡潔、使用繁體中文;不確定時明說不確定,不要猜測或編造。
邊界:只回答被授權範圍內的問題;需要外部資料時使用被允許的工具,不要臆測數字。`

/** 系統設定 key 命名:全平台共用一份(app_config 無 tenant 欄位),不是每租戶一份。 */
export function agentDefaultConfigKey(field: string): string {
  return `agent.defaults.${field}`
}

const SYSTEM_PROMPT_KEY = agentDefaultConfigKey('system_prompt')

/** key ↔ fallback 的單一真相;「一般設定」表格用它合併顯示尚未落地的 key。 */
export const AGENT_DEFAULT_CONFIG_FALLBACKS: Readonly<Record<string, string>> = {
  ...Object.fromEntries(
    Object.entries(AGENT_DEFAULT_RUNTIME_LIMITS).map(([field, value]) => [
      agentDefaultConfigKey(field),
      String(value),
    ]),
  ),
  [SYSTEM_PROMPT_KEY]: AGENT_DEFAULT_SYSTEM_PROMPT,
}

/**
 * 用系統設定覆寫建立模式的初始草稿。壞值(缺、空白、非數字、負數、小數)一律沿用內建 fallback —
 * 壞設定不可以產生壞草稿。伺服器的 runtime_limits 只收整數(backend `AgentCanonicalizer.
 * ValidateLimit` 用 `TryGetValue<int>`),放行 2.5 只會產出必定驗證失敗的草稿。
 * 只在建立模式、使用者尚未編輯前呼叫。
 */
export function applyAgentDefaultConfig(
  draft: AgentDraft,
  entries: readonly ConfigEntry[],
): AgentDraft {
  const byKey = new Map(entries.map((entry) => [entry.key, entry.value]))
  const runtime_limits = { ...draft.runtime_limits }
  for (const field of Object.keys(AGENT_DEFAULT_RUNTIME_LIMITS) as (keyof AgentRuntimeLimits)[]) {
    const raw = byKey.get(agentDefaultConfigKey(field))?.trim()
    const parsed = Number(raw)
    if (raw && Number.isInteger(parsed) && parsed >= 0) runtime_limits[field] = parsed
  }
  const prompt = byKey.get(SYSTEM_PROMPT_KEY)?.trim()
  return { ...draft, runtime_limits, system_prompt: prompt || draft.system_prompt }
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

/**
 * 建立精靈的起始草稿:身分欄位留白由使用者填,技術性欄位一律給可用的預設值
 * (audience 依產品契約開給同 tenant 的 USER 與 ADMIN),避免建出不可用的 Agent。
 */
export function createEmptyAgentDraft(): AgentDraft {
  return {
    name: '',
    slug: '',
    description: '',
    system_prompt: AGENT_DEFAULT_SYSTEM_PROMPT,
    execution_roles: ['worker'],
    capabilities: [],
    output_contract: {},
    audience: [...AGENT_DEFAULT_AUDIENCE],
    allowed_tools: [],
    knowledge_sources: [],
    skill_bindings: [],
    business_rules: { ...EMPTY_RULES, rules: [] },
    runtime_limits: { ...AGENT_DEFAULT_RUNTIME_LIMITS },
  }
}

function strings(value: unknown, fallback: string[] = []): string[] {
  return Array.isArray(value)
    ? value.filter((item): item is string => typeof item === 'string')
    : [...fallback]
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
