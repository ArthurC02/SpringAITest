import { apiFetch, apiFetchWithEtag } from './http'
import type {
  Agent,
  AgentDraft,
  AgentRevision,
  AgentSummary,
  AgentToolCatalogEntry,
  AgentValidation,
  AgentBusinessRules,
  RuleActionCatalogEntry,
  RuleCatalogEnvelope,
  RuleFactCatalogEntry,
  RuleGate,
  RuleSimulationResult,
  RuleValidationResult,
} from '../types'

/** GET /api/features（camelCase）。false 或請求失敗 → 呼叫端 fail-closed 隱藏 Agents 入口。 */
export interface FeatureFlags {
  agentBuilderEnabled: boolean
  agentTestRunEnabled?: boolean
  workflowDesignerEnabled?: boolean
  multiAgentDispatchEnabled?: boolean
  agentChatEnabled?: boolean
}

export function getFeatures(): Promise<FeatureFlags> {
  return apiFetch<FeatureFlags>('/api/features')
}

/** 安全的 Tool 作者目錄；server 不回傳 endpoint/token。失敗由 UI fail-closed 顯示空清單。 */
export function listAgentToolCatalog(): Promise<AgentToolCatalogEntry[]> {
  return apiFetch<AgentToolCatalogEntry[]>('/api/tools')
}

export type RuleFactCatalogResponse =
  | RuleFactCatalogEntry[]
  | RuleCatalogEnvelope<RuleFactCatalogEntry>

export type RuleActionCatalogResponse =
  | RuleActionCatalogEntry[]
  | RuleCatalogEnvelope<RuleActionCatalogEntry>

export function listRuleFacts(): Promise<RuleFactCatalogResponse> {
  return apiFetch<RuleFactCatalogResponse>('/api/agents/catalog/rule-facts')
}

export function listRuleActions(): Promise<RuleActionCatalogResponse> {
  return apiFetch<RuleActionCatalogResponse>('/api/agents/catalog/rule-actions')
}

export function validateBusinessRules(
  gate: RuleGate,
  ruleSet: AgentBusinessRules,
): Promise<RuleValidationResult> {
  return apiFetch<RuleValidationResult>('/api/agents/rules/validate', {
    method: 'POST',
    body: JSON.stringify({ gate, ruleSet }),
  })
}

export function simulateBusinessRules(
  gate: RuleGate,
  ruleSet: AgentBusinessRules,
  facts: Record<string, unknown>,
): Promise<RuleSimulationResult> {
  return apiFetch<RuleSimulationResult>('/api/agents/rules/simulate', {
    method: 'POST',
    body: JSON.stringify({ gate, ruleSet, facts }),
  })
}

export function listAgents(): Promise<AgentSummary[]> {
  return apiFetch<AgentSummary[]>('/api/agents')
}

/** 建立 Agent（含初始草稿）。回傳含 id 的 Agent；呼叫端接著切到編輯模式重讀取 ETag。 */
export function createAgent(draft: AgentDraft): Promise<Agent> {
  return apiFetch<Agent>('/api/agents', {
    method: 'POST',
    body: JSON.stringify(draft),
  })
}

/** 取單一 Agent + ETag（供 PUT draft / publish 的 If-Match 樂觀併發）。 */
export function getAgent(id: string): Promise<{ data: Agent; etag: string | null }> {
  return apiFetchWithEtag<Agent>(`/api/agents/${encodeURIComponent(id)}`)
}

/**
 * 存草稿：帶 If-Match ETag。412（或 409）= 併發衝突，呼叫端提示重新載入、不覆蓋他人更新。
 * 存後回應形狀不保證帶新 ETag，呼叫端一律重讀（getAgent）取最新 ETag/版本。
 */
export function putAgentDraft(
  id: string,
  draft: AgentDraft,
  etag: string | null,
): Promise<void> {
  return apiFetch<void>(`/api/agents/${encodeURIComponent(id)}/draft`, {
    method: 'PUT',
    headers: etag ? { 'If-Match': etag } : undefined,
    body: JSON.stringify(draft),
  })
}

/** 驗證已存草稿（一律 200，valid=false 也是 200）；field_errors 定位到欄位。 */
export function validateAgent(id: string, etag: string | null): Promise<AgentValidation> {
  return apiFetch<AgentValidation>(`/api/agents/${encodeURIComponent(id)}/validate`, {
    method: 'POST',
    headers: etag ? { 'If-Match': etag } : undefined,
  })
}

/**
 * 發布：帶 expected_draft_version + If-Match ETag（併發保護）。
 * 412/409 = 需重新載入；發布會把每個 Skill binding 固定成確切 revision（規格 §2.2/§3.2）。
 */
export function publishAgent(
  id: string,
  expectedDraftVersion: number,
  etag: string | null,
): Promise<void> {
  return apiFetch<void>(`/api/agents/${encodeURIComponent(id)}/publish`, {
    method: 'POST',
    headers: etag ? { 'If-Match': etag } : undefined,
    body: JSON.stringify({ expected_draft_version: expectedDraftVersion }),
  })
}

/** 唯讀歷史（依 revision 遞減）。 */
export function listAgentRevisions(id: string): Promise<AgentRevision[]> {
  return apiFetch<AgentRevision[]>(`/api/agents/${encodeURIComponent(id)}/revisions`)
}

/** 回溯：以舊 revision 重新發布為一個新 revision（不改寫歷史，規格 §2.2）。 */
export function restoreAgentRevision(id: string, revision: number): Promise<void> {
  return apiFetch<void>(
    `/api/agents/${encodeURIComponent(id)}/revisions/${encodeURIComponent(String(revision))}/restore`,
    { method: 'POST' },
  )
}

/** 停用（軟停用 enabled=false；revision 保留供稽核，規格 §2.2）。 */
export function deactivateAgent(id: string): Promise<void> {
  return apiFetch<void>(`/api/agents/${encodeURIComponent(id)}`, { method: 'DELETE' })
}

/** 重新啟用（enabled=true）。 */
export function enableAgent(id: string): Promise<void> {
  return apiFetch<void>(`/api/agents/${encodeURIComponent(id)}/enable`, { method: 'POST' })
}
