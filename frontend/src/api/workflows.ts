import { apiFetch } from './http'
import type { WorkflowInfo, WorkflowResult } from '../types'

export function listWorkflows(): Promise<WorkflowInfo[]> {
  return apiFetch<WorkflowInfo[]>('/api/workflows')
}

export function invokeWorkflow(
  name: string,
  input: Record<string, unknown>,
): Promise<WorkflowResult> {
  return apiFetch<WorkflowResult>(`/api/workflows/${encodeURIComponent(name)}`, {
    method: 'POST',
    body: JSON.stringify({ input }),
  })
}
