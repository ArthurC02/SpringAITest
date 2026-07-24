export interface Message {
  id: string
  role: 'user' | 'assistant'
  content: string
  /** true 代表這則 assistant 泡泡其實是錯誤訊息（以純文字、紅底顯示）。 */
  error?: boolean
}

/** 後端角色。註冊/登入回傳，決定側欄「系統設定」是否顯示。 */
export type Role = 'ADMIN' | 'USER'

/** Skill 的作者格式；catalog / CRUD / revision API 都以 additive 欄位提供。 */
export type SkillKind = 'flow' | 'agentic'

/** 登入成功後存進 localStorage 的一整包身分（單一 JSON key）。 */
export interface Session {
  token: string
  username: string
  role: Role
  tenantCode: string
}

/**
 * 文件清單一列。後端回 snake_case，這裡照實宣告，不做 camelCase 轉換層
 * （契約規定；轉換只會多一層心智負擔）。POST 202 只回 {id,title,status}，
 * 樂觀插入時其餘欄位由前端補預設值。
 */
export interface DocumentInfo {
  id: string
  title: string
  status: 'processing' | 'ready' | 'failed'
  chunk_count: number
  created_at: string
}

/**
 * POST /api/skills/{name}/invoke 回應。名稱鍵是 `skill` 不是 `workflow`
 * （workflow 服務的 SkillInvokeResponse，platform 原樣透傳）——兩條路徑形狀確實不同，
 * 別為了少一個型別硬統一。output 形狀相同（含 trace）。
 */
export interface SkillResult {
  skill: string
  output: Record<string, unknown>
}

/** 分析摘要（snake_case，照實宣告）。 */
export interface AnalysisSummary {
  document_count: number
  chunk_count: number
  latest_titles: string[]
}

/** 系統設定一列（此端點契約為 camelCase）。 */
export interface ConfigEntry {
  key: string
  value: string
  updatedAt: string
}

/**
 * 簡單模式的表單狀態（存於 skill.simple_form，camelCase 契約，可重回簡單模式編輯）。
 * templateId = 範本骨架名（basedOn，如 `template-stats`）；form 值全為表單原字串。
 */
export interface SkillSimpleForm {
  templateId: string
  form: Partial<Record<'name' | 'description' | 'rule' | 'topK', string>>
}

/** Skill 清單一列（此端點契約為 snake_case，比照 workflows/documents）。 */
export interface SkillInfo {
  name: string
  description: string
  required_role: string
  current_revision: number
  updated_at: string
  /** 軟刪後 enabled=false。 */
  enabled: boolean
  /** 未攜帶時是舊 server，相容視為 flow。 */
  kind?: SkillKind
  /** 簡單模式建立/更新才有（camelCase）；package 匯入品、純 YAML 手寫品為 null/缺席。 */
  simpleForm?: SkillSimpleForm | null
}

/** 單筆 Skill；definition = Skill YAML 原文（權威格式，見規格書 §3）。 */
export interface Skill extends SkillInfo {
  definition: string
}

/** GET /api/skills/{name}/revisions 一列（唯讀稽核用，依 revision 遞減）。 */
export interface SkillRevision {
  revision: number
  definition: string
  definition_sha256: string
  created_by: string
  created_at: string
  /** 每個 revision 自己的格式；同一 skill 的歷史可能混合 flow 與 agentic。 */
  kind: SkillKind
  /** agentic revision 的 package hash；僅稽核用途，絕不含 package bytes。 */
  package_sha256?: string
  /** server 是否仍保存本 revision 的 package，可否回復 agentic 歷史。 */
  has_package?: boolean
}

/** input_schema 的一個欄位（規格 §3.1：{query: {type: str, required: true, min_length: 1}}）。 */
export interface SkillInputField {
  type: string
  required?: boolean
  min_length?: number
}

/** GET /api/skills/catalog 一列：可執行的 skill（內建或租戶自訂）。 */
export interface SkillCatalogEntry {
  name: string
  description: string
  required_role: string
  source: 'builtin' | 'custom'
  revision: number | null
  /**
   * 是否可固定成 Agent revision binding。builtin 目前沒有 backend persisted immutable
   * revision，故為 false；只有 server 明確確認 persisted revision 的 custom Skill 才為 true。
   */
  bindable: boolean
  input_schema?: Record<string, SkillInputField> | null
  /** 內建骨架項（template-* / kb-query）的 YAML 原文；compose patch 用。custom 為 undefined。 */
  definition?: string
  /**
   * Skill 種類（Platform additive 透傳；缺席 → 視為 flow）。agentic 走 package 編輯器，
   * flow 走既有 YAML/simple editor。實務上 catalog 尚未帶此欄，故編輯路由以 definition 的
   * `kind: agentic` 為可靠訊號（skillKind()），此欄為前向相容。
   */
  kind?: SkillKind
}

/** POST /api/skills/validate 的一條錯誤；line 為 YAML 行號（引擎給得出來時才有）。 */
export interface SkillValidationError {
  code: string
  message: string
  line?: number
}

/** POST /api/skills/validate 回應（一律 200，valid=false 也是 200）。 */
export interface SkillValidation {
  valid: boolean
  errors: SkillValidationError[]
}

// ── Agent Builder（D1）：領域欄位 snake_case；錯誤走 ApiError（camelCase）。 ─────

/** Worker／Verifier eligibility；可複選（見規格 §2.1）。 */
export type AgentExecutionRole = 'worker' | 'verifier'

/**
 * Agent draft 的 Skill 綁定寫入形狀（backend 權威）。`revision_policy` 缺席 = 跟隨最新，
 * 發布時於伺服器固定成確切 revision（規格 §3.2），Skill 之後更新不影響已發布 Agent。
 */
export interface AgentSkillBinding {
  skill: string
  revision_policy?: string
}

export interface AgentRuntimeLimits {
  max_tool_rounds: number
  max_context_rounds: number
  timeout_seconds: number
  token_budget: number
  step_budget: number
}

export interface AgentWorkflowRef {
  id: string
  revision: number
}

export type AgentOutputContract = Record<string, unknown>

export type RuleFactType =
  | 'string'
  | 'enum'
  | 'number'
  | 'integer'
  | 'decimal'
  | 'boolean'
  | 'collection'
  | string

/** Exact base-10 decimal transported as a JSON string; never parse with Number. */
export type RuleDecimalString = string

export type RuleGate =
  | 'preflight'
  | 'post-context'
  | 'pre-action'
  | 'post-action'
  | 'pre-response'
  | string

export interface RuleConditionLeaf {
  fact: string
  op: string
  value?: unknown
}

export interface RuleConditionAll {
  all: RuleCondition[]
}

export interface RuleConditionAny {
  any: RuleCondition[]
}

export interface RuleConditionNot {
  not: RuleCondition
}

export type RuleCondition =
  | RuleConditionLeaf
  | RuleConditionAll
  | RuleConditionAny
  | RuleConditionNot

export interface RuleAction {
  action: string
  [parameter: string]: unknown
}

export interface AgentBusinessRule {
  id: string
  name: string
  enabled: boolean
  priority: number
  when: RuleCondition
  then: RuleAction[]
  onUnknown?: RuleAction[]
}

/** Canonical JSON AST persisted in the Agent draft. */
export interface AgentBusinessRules {
  version: number
  rules: AgentBusinessRule[]
}

export interface RuleOperatorCatalogEntry {
  name: string
  label?: string
  value_type?: RuleFactType | 'none' | 'same'
  value_count?: number
  description?: string
  compatibleFactTypes?: RuleFactType[]
  compatible_fact_types?: RuleFactType[]
  value?: {
    kind: 'none' | 'scalar' | 'list' | 'range' | string
    types?: RuleFactType[]
  }
}

export interface RuleFactCatalogEntry {
  name: string
  label?: string
  description?: string
  type: RuleFactType
  provenance: string
  trustTier?: string
  trust_tier?: string
  gates: RuleGate[]
  operators?: Array<string | RuleOperatorCatalogEntry>
  enumValues?: unknown[]
  enum_values?: unknown[]
  values?: unknown[]
  itemType?: RuleFactType
  item_type?: RuleFactType
  visibleValue?: boolean
  visible_value?: boolean
  wireFormat?: string
  wire_format?: string
}

export interface RuleActionParameter {
  name: string
  label?: string
  type: RuleFactType
  required?: boolean
  enumValues?: unknown[]
  enum_values?: unknown[]
  values?: unknown[]
  description?: string
  maxItems?: number
  max_items?: number
}

export interface RuleActionCatalogEntry {
  name: string
  label?: string
  description?: string
  parameters?: RuleActionParameter[] | Record<string, Omit<RuleActionParameter, 'name'>>
  decision?: string
  precedence?: number
}

export interface RuleCatalogEnvelope<T> {
  facts?: T[]
  actions?: T[]
  operators?: RuleOperatorCatalogEntry[]
  gates?: RuleGate[]
  items?: T[]
  decimalWireFormat?: {
    type: 'string'
    format: string
    allowExponent: boolean
    maxPrecision: number
    maxScale: number
    maxIntegerDigits: number
  }
  limits?: {
    maxDepth?: number
    maxNodes?: number
    maxRules?: number
    maxStringLength?: number
    maxCollectionItems?: number
    maxErrors?: number
    maxDecimalPrecision?: number
    maxDecimalScale?: number
    maxDecimalIntegerDigits?: number
  }
}

export interface RuleValidationIssue {
  path: string
  code?: string
  message: string
}

export interface RuleValidationResult {
  valid: boolean
  canonicalRuleSet?: AgentBusinessRules
  canonical_rule_set?: AgentBusinessRules
  errors: RuleValidationIssue[]
}

export interface RuleSimulation {
  decision?: unknown
  matchedRules?: unknown[]
  matched_rules?: unknown[]
  trace?: unknown
  [key: string]: unknown
}

export interface RuleSimulationResult extends RuleValidationResult {
  simulation?: RuleSimulation
}

/** GET /api/tools 的安全作者目錄；不包含 endpoint、token 或其他連線秘密。 */
export interface AgentToolCatalogEntry {
  name: string
  kind: 'http' | 'local'
  description: string
  risk: 'low' | 'read' | 'write' | 'privileged'
  returns: string
}

/** 已發布 revision 的 Skill 綁定讀取形狀（backend 權威，含固定的 skill_revision）。 */
export interface AgentRevisionBinding {
  skill: string
  skill_revision: number
  position: number
  enabled: boolean
}

/**
 * Agent 可編輯草稿（建立精靈／編輯器的欄位）。集合欄位（allowed_tools／knowledge_sources／
 * skill_bindings）以空陣列明確表示「無授權」——絕不用 null 代表全開（規格 §3.3 fail closed）。
 */
export interface AgentDraft {
  name: string
  slug: string
  description: string
  system_prompt: string
  execution_roles: AgentExecutionRole[]
  capabilities: string[]
  output_contract: AgentOutputContract
  audience: string[]
  allowed_tools: string[]
  knowledge_sources: string[]
  skill_bindings: AgentSkillBinding[]
  business_rules: AgentBusinessRules
  runtime_limits: AgentRuntimeLimits
  /** 建立時可省略，由 server 固定到 system-owned Default Agent-Runtime Workflow。 */
  runtime_workflow?: AgentWorkflowRef
}

/** GET /api/agents 一列（清單）。`published_revision=null` 表示尚未發布。 */
export interface AgentSummary {
  id: string
  name: string
  slug: string
  description: string
  enabled: boolean
  published_revision: number | null
  updated_at: string
}

/** GET /api/agents/{id}：含目前草稿與 optimistic-concurrency 版本號（另配 ETag header）。 */
export interface Agent extends AgentSummary {
  draft_version: number
  draft: AgentDraft
}

/** validate 的一條錯誤；`field` 有值時定位到該欄位內聯顯示，否則彙總。 */
export interface AgentValidationError {
  field?: string
  message: string
}

/**
 * POST /api/agents/{id}/validate 回應（backend 權威形狀）。比照 skill validate：
 * 一律 HTTP 200，valid=false 也是 200。錯誤以 `errors:[{field,message}]` 承載。
 */
export interface AgentValidation {
  valid: boolean
  errors: AgentValidationError[]
}

/** GET /api/agents/{id}/revisions 一列（唯讀稽核，依 revision 遞減）。 */
export interface AgentRevision {
  revision: number
  created_by: string
  created_at: string
  definition_sha256: string
  skill_bindings: AgentRevisionBinding[]
}

/** Configuration Set 的七個可覆寫鍵（snake_case，含 dot 命名空間）。 */
export type ConfigKey =
  | 'retrieval.top_k'
  | 'kb_query.top_k'
  | 'kb_query.max_retrieval_attempts'
  | 'workflow.timeout_seconds'
  | 'llm.model'
  | 'intent.confidence_threshold'
  | 'llm.temperature'

/** 只存覆寫值：未覆寫的鍵回落全域預設。model 為 string、其餘為 number。 */
export type ConfigurationValues = Partial<Record<ConfigKey, number | string>>

/**
 * Configuration Set 清單一列（snake_case，比照 skill）。`values` 在清單可能不帶
 * （後端 ConfigurationSetInfo 只回 id/name/is_active/updated_at）——編輯時一律 GET {id} 取完整值。
 */
export interface ConfigurationSetInfo {
  id: string
  name: string
  is_active: boolean
  updated_at: string
  values?: ConfigurationValues
}

/** 單筆 Configuration Set（GET {id}）：含 values、created_at 與 created_by。 */
export interface ConfigurationSet extends ConfigurationSetInfo {
  values: ConfigurationValues
  created_at: string
  created_by: string
}

/** GET /api/nodes 一列：節點契約（reads/writes 是 state 鍵名）。 */
export interface NodeInfo {
  name: string
  version: string
  description: string
  reads: string[]
  writes: string[]
  requires_tools: string[]
}

/**
 * invoke 回應 output.trace 的一列（workflow 服務的 TraceEntry，snake_case）。
 * summary 只有鍵名不含值，是刻意的稽核設計，不要改成顯示內容。
 */
export interface TraceEntry {
  node_name: string
  start_time: string
  end_time: string
  latency_ms: number
  status: string
  input_summary?: string
  output_summary?: string
  error_code?: string
  component_version?: string
  failure_codes?: string[]
}
