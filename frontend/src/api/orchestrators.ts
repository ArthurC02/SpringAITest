import { apiFetch, apiFetchWithEtag } from './http'
import { object, type JsonObject } from '../wire'
import type { Orchestrator, OrchestratorDraft, OrchestratorRevision, OrchestratorSummary } from '../types'

const base = '/api/admin/orchestrators'
const idPath = (id: string) => `${base}/${encodeURIComponent(id)}`
type WireOrchestrator = JsonObject & { id: string; name: string; description: string; enabled: boolean; draft_version: number; published_revision: number | null; definition: JsonObject }

function ref(value: unknown): OrchestratorDraft['workflow'] { const item = object(value); return { id: typeof item.id === 'string' ? item.id : '', revision: typeof item.revision === 'number' ? item.revision : 0 } }
function agentRef(value: unknown): { agentId: string; revision: number } { const item = object(value); return { agentId: typeof item.agentId === 'string' ? item.agentId : '', revision: typeof item.revision === 'number' ? item.revision : 0 } }

/** The backend persists one typed definition object; the UI gets a convenient explicit draft view. */
export function decodeOrchestrator(value: WireOrchestrator): Orchestrator {
  const definition = object(value.definition)
  const context = object(definition.context)
  const budgets = object(definition.budgets)
  const policy = object(definition.policy)
  const workerPolicy = object(definition.workerPolicy)
  return {
    id: value.id, name: value.name, description: value.description, enabled: value.enabled,
    draft_version: value.draft_version, published_revision: value.published_revision, updated_at: String(value.updated_at ?? ''),
    draft: {
      name: value.name, description: value.description,
      instructions: typeof definition.instructions === 'string' ? definition.instructions : '',
      policy: {
        dispatchMode: 'bounded-parallel',
        joinPolicy: policy.joinPolicy === 'allow-partial' || policy.joinPolicy === 'repair' ? policy.joinPolicy : 'fail-fast',
        repairPolicy: policy.repairPolicy === 'redispatch' ? 'redispatch' : 'fail',
        aggregationPolicy: 'verified-only', denialPolicy: 'fail-closed',
      },
      workflow: ref(definition.workflow),
      workerPool: Array.isArray(definition.workerPool) ? definition.workerPool.map(agentRef) : [],
      workerPolicy: {
        requiredAudience: Array.isArray(workerPolicy.requiredAudience) ? workerPolicy.requiredAudience.filter((x): x is string => typeof x === 'string') : [],
        requiredCapabilities: Array.isArray(workerPolicy.requiredCapabilities) ? workerPolicy.requiredCapabilities.filter((x): x is string => typeof x === 'string') : [],
        selection: 'pinned-only',
      },
      context: {
        readOnly: true,
        allowedTools: Array.isArray(context.allowedTools) ? context.allowedTools.filter((x): x is string => typeof x === 'string') : [],
        knowledgeSources: Array.isArray(context.knowledgeSources) ? context.knowledgeSources.filter((x): x is string => typeof x === 'string') : [],
      },
      audience: Array.isArray(definition.audience) ? definition.audience.filter((x): x is string => typeof x === 'string') : [],
      capabilities: Array.isArray(definition.capabilities) ? definition.capabilities.filter((x): x is string => typeof x === 'string') : [],
      verifier: {
        ...agentRef(definition.verifier), variant: 'read-only', independent: true,
        outputContract: { type: 'verification-report' },
      },
      budgets: {
        maxContextRounds: Number(budgets.maxContextRounds ?? 0), maxTasks: Number(budgets.maxTasks ?? 0),
        maxChildRuns: Number(budgets.maxChildRuns ?? 0), maxConcurrency: Number(budgets.maxConcurrency ?? 0),
        maxRepairRounds: Number(budgets.maxRepairRounds ?? 0), tokenBudget: Number(budgets.tokenBudget ?? 0),
        timeoutSeconds: Number(budgets.timeoutSeconds ?? 0),
      },
    },
  }
}

/** Keep the four Backend-required fields explicit and use verifier.agentId (never ambiguous `id`). */
export function encodeOrchestratorUpsert(draft: OrchestratorDraft): JsonObject {
  return { name: draft.name, description: draft.description, definition: {
    instructions: draft.instructions, policy: draft.policy, workflow: draft.workflow,
    workerPool: draft.workerPool, workerPolicy: draft.workerPolicy,
    context: draft.context, audience: draft.audience,
    capabilities: draft.capabilities, verifier: draft.verifier, budgets: draft.budgets,
  } }
}

export async function listOrchestrators(): Promise<OrchestratorSummary[]> { return apiFetch(base) }
export async function createOrchestrator(draft: OrchestratorDraft): Promise<Orchestrator> { return decodeOrchestrator(await apiFetch<WireOrchestrator>(base, { method: 'POST', body: JSON.stringify(encodeOrchestratorUpsert(draft)) })) }
export async function getOrchestrator(id: string): Promise<{ data: Orchestrator; etag: string | null }> { const result = await apiFetchWithEtag<WireOrchestrator>(idPath(id)); return { data: decodeOrchestrator(result.data), etag: result.etag } }
export function putOrchestratorDraft(id: string, draft: OrchestratorDraft, etag: string | null): Promise<Orchestrator> { return apiFetch<WireOrchestrator>(`${idPath(id)}/draft`, { method: 'PUT', headers: etag ? { 'If-Match': etag } : undefined, body: JSON.stringify(encodeOrchestratorUpsert(draft)) }).then(decodeOrchestrator) }
export async function validateOrchestrator(id: string, etag: string | null): Promise<{ valid: boolean; errors: { message: string }[] }> { const result = await apiFetch<{ valid: boolean; errors?: string[] }>(`${idPath(id)}/validate`, { method: 'POST', headers: etag ? { 'If-Match': etag } : undefined }); return { valid: result.valid, errors: (result.errors ?? []).map((message) => ({ message })) } }
export function publishOrchestrator(id: string, expectedDraftVersion: number, etag: string | null): Promise<Orchestrator> { return apiFetch<WireOrchestrator>(`${idPath(id)}/publish`, { method: 'POST', headers: etag ? { 'If-Match': etag } : undefined, body: JSON.stringify({ expected_draft_version: expectedDraftVersion }) }).then(decodeOrchestrator) }
export function listOrchestratorRevisions(id: string): Promise<OrchestratorRevision[]> { return apiFetch(`${idPath(id)}/revisions`) }
export function restoreOrchestratorRevision(id: string, revision: number): Promise<Orchestrator> { return apiFetch<WireOrchestrator>(`${idPath(id)}/revisions/${revision}/restore`, { method: 'POST' }).then(decodeOrchestrator) }
