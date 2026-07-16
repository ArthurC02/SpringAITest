import { apiFetch } from './http'
import type { WorkflowResult } from '../types'

// listWorkflows 已退場：工作流唯讀清單/執行分頁隨系統設定重構移除（設計 §1.3）。
// invokeWorkflow 仍被 AppShell 的 askKnowledgeBase(rag_qa) 使用，保留。
export function invokeWorkflow(
  name: string,
  input: Record<string, unknown>,
): Promise<WorkflowResult> {
  return apiFetch<WorkflowResult>(`/api/workflows/${encodeURIComponent(name)}`, {
    method: 'POST',
    body: JSON.stringify({ input }),
  })
}
