import { apiFetch } from './http'
import { newIdempotencyKey, normalizeAgentRun, normalizeAgentRunEventPage } from './agentRuns'
import type { AgentRun, AgentRunEventPage } from '../types'

const runPath = (id: string) => `/api/orchestrator-runs/${encodeURIComponent(id)}`
export { newIdempotencyKey }
export async function startOrchestratorRun(orchestratorId: string, message: string, conversationId: string, key: string): Promise<AgentRun> {
  return normalizeAgentRun(await apiFetch(`/api/admin/orchestrators/${encodeURIComponent(orchestratorId)}/runs`, { method: 'POST', headers: { 'Idempotency-Key': key }, body: JSON.stringify({ message, conversationId }) }))
}
export async function getOrchestratorRun(id: string): Promise<AgentRun> { return normalizeAgentRun(await apiFetch(runPath(id))) }
export async function getOrchestratorRunEvents(id: string, after: number): Promise<AgentRunEventPage> { return normalizeAgentRunEventPage(await apiFetch(`${runPath(id)}/events?afterSequence=${Math.max(0, after)}&limit=100`)) }
export async function cancelOrchestratorRun(id: string, key: string): Promise<AgentRun> { return normalizeAgentRun(await apiFetch(`${runPath(id)}/cancel`, { method: 'POST', headers: { 'Idempotency-Key': key } })) }
