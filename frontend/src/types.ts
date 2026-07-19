export interface Message {
  id: string
  role: 'user' | 'assistant'
  content: string
  /** true 代表這則 assistant 泡泡其實是錯誤訊息（以純文字、紅底顯示）。 */
  error?: boolean
}

/** 後端角色。註冊/登入回傳，決定側欄「系統設定」是否顯示。 */
export type Role = 'ADMIN' | 'USER'

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

/** Skill 清單一列（此端點契約為 snake_case，比照 workflows/documents）。 */
export interface SkillInfo {
  name: string
  description: string
  required_role: string
  current_revision: number
  updated_at: string
  /** 軟刪後 enabled=false。 */
  enabled: boolean
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
  input_schema?: Record<string, SkillInputField> | null
  /** 內建骨架項（template_* / kb_query）的 YAML 原文；compose patch 用。custom 為 undefined。 */
  definition?: string
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
