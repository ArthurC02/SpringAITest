import { apiFetch, apiFetchWithEtag } from './http'
import type {
  Workflow, WorkflowDefinition, WorkflowDraft, WorkflowKind, WorkflowNodeType,
  WorkflowRevision, WorkflowSimulation, WorkflowSummary, WorkflowUiMetadata, WorkflowValidation,
} from '../types'

const base = '/api/admin/workflows'
const idPath = (id: string) => `${base}/${encodeURIComponent(id)}`

type RawObject = Record<string, unknown>
type WireWorkflow = RawObject & { id: string; name: string; kind: WorkflowKind; enabled: boolean; draft_version: number; published_revision: number | null; definition: WorkflowDefinition; ui_metadata: WorkflowUiMetadata }
type WireValidation = { valid: boolean; definition?: WorkflowDefinition; ui_metadata?: WorkflowUiMetadata; errors?: Array<{ field?: string; message: string; node_id?: string; edge_id?: string }> }
type WireCatalog = { nodes?: WorkflowNodeType[] }

function object(value: unknown): RawObject { return value && typeof value === 'object' ? value as RawObject : {} }
function graph(value: unknown, kind: WorkflowKind): WorkflowDefinition {
  const raw = object(value)
  const runtimeVariant = raw.runtimeVariant === 'worker' || raw.runtimeVariant === 'verifier'
    ? raw.runtimeVariant
    : undefined
  return {
    schemaVersion: Number(raw.schemaVersion),
    kind,
    ...(kind === 'agent-runtime' && runtimeVariant ? { runtimeVariant } : {}),
    nodes: Array.isArray(raw.nodes) ? raw.nodes as WorkflowDefinition['nodes'] : [],
    edges: Array.isArray(raw.edges) ? raw.edges as WorkflowDefinition['edges'] : [],
    governance: object(raw.governance),
  }
}
function metadata(value: unknown): WorkflowUiMetadata { return value as WorkflowUiMetadata }

/** Backend returns a flat response; UI intentionally owns a nested draft projection. */
export function decodeWorkflow(value: WireWorkflow): Workflow {
  return {
    id: value.id, name: value.name, description: '', kind: value.kind, enabled: value.enabled,
    draft_version: value.draft_version, published_revision: value.published_revision,
    updated_at: String(value.updated_at ?? ''), draft: { definition: graph(value.definition, value.kind), ui_metadata: metadata(value.ui_metadata) },
  }
}

export function encodeWorkflowUpsert(input: { name: string; kind: WorkflowKind; draft: WorkflowDraft }): RawObject {
  return { name: input.name, kind: input.kind, definition: graph(input.draft.definition, input.kind), ui_metadata: input.draft.ui_metadata }
}

export function decodeWorkflowValidation(value: WireValidation): WorkflowValidation {
  return {
    valid: value.valid,
    canonical_definition: value.definition,
    errors: (value.errors ?? []).map((error) => ({
      scope: error.node_id ? 'node' : error.edge_id ? 'edge' : 'graph', id: error.node_id ?? error.edge_id,
      code: error.field ?? 'validation', message: error.message,
    })),
  }
}

export function decodeWorkflowSimulation(value: unknown): WorkflowSimulation {
  const raw = object(value)
  // Workflow's internal simulator uses camelCase; Backend's validation response uses snake_case.
  const diagnostics = (raw.errors ?? []) as Array<{ path?: string; field?: string; code?: string; message?: string; nodeId?: string; edgeId?: string; node_id?: string; edge_id?: string }>
  return {
    valid: raw.valid === true,
    canonical_definition: (raw.canonicalDefinition ?? raw.definition) as WorkflowDefinition | undefined,
    errors: diagnostics.map((error) => ({ scope: error.nodeId || error.node_id ? 'node' : error.edgeId || error.edge_id ? 'edge' : 'graph', id: error.nodeId ?? error.node_id ?? error.edgeId ?? error.edge_id, code: error.code ?? error.path ?? error.field ?? 'validation', message: error.message ?? 'validation failed' })),
    trace: Array.isArray(raw.trace) ? raw.trace.map((entry) => {
      const item = object(entry); return { node_id: String(item.nodeId ?? item.node_id ?? ''), status: String(item.status ?? 'unknown'), summary: typeof item.summary === 'string' ? item.summary : undefined }
    }) : undefined,
  }
}

export async function listWorkflows(): Promise<WorkflowSummary[]> {
  return apiFetch<WorkflowSummary[]>(base)
}

export async function createWorkflow(input: { name: string; kind: WorkflowKind; draft: WorkflowDraft }): Promise<Workflow> {
  return decodeWorkflow(await apiFetch<WireWorkflow>(base, { method: 'POST', body: JSON.stringify(encodeWorkflowUpsert(input)) }))
}

export async function getWorkflow(id: string): Promise<{ data: Workflow; etag: string | null }> {
  const result = await apiFetchWithEtag<WireWorkflow>(idPath(id)); return { data: decodeWorkflow(result.data), etag: result.etag }
}

export function putWorkflowDraft(id: string, workflow: Pick<Workflow, 'name' | 'kind'>, draft: WorkflowDraft, etag: string | null): Promise<Workflow> {
  return apiFetch<WireWorkflow>(`${idPath(id)}/draft`, { method: 'PUT', headers: etag ? { 'If-Match': etag } : undefined, body: JSON.stringify(encodeWorkflowUpsert({ name: workflow.name, kind: workflow.kind, draft })) }).then(decodeWorkflow)
}

export async function validateWorkflow(id: string, etag: string | null): Promise<WorkflowValidation> {
  return decodeWorkflowValidation(await apiFetch<WireValidation>(`${idPath(id)}/validate`, { method: 'POST', headers: etag ? { 'If-Match': etag } : undefined }))
}

export async function simulateWorkflow(id: string, etag: string | null): Promise<WorkflowSimulation> {
  return decodeWorkflowSimulation(await apiFetch(`${idPath(id)}/simulate`, { method: 'POST', headers: etag ? { 'If-Match': etag } : undefined }))
}

export function publishWorkflow(id: string, expectedDraftVersion: number, etag: string | null): Promise<Workflow> {
  return apiFetch<WireWorkflow>(`${idPath(id)}/publish`, { method: 'POST', headers: etag ? { 'If-Match': etag } : undefined, body: JSON.stringify({ expected_draft_version: expectedDraftVersion }) }).then(decodeWorkflow)
}
export function listWorkflowRevisions(id: string): Promise<WorkflowRevision[]> { return apiFetch(`${idPath(id)}/revisions`) }
export function restoreWorkflowRevision(id: string, revision: number): Promise<Workflow> { return apiFetch<WireWorkflow>(`${idPath(id)}/revisions/${revision}/restore`, { method: 'POST' }).then(decodeWorkflow) }
export async function listWorkflowNodeCatalog(): Promise<WorkflowNodeType[]> { const value = await apiFetch<WireCatalog>(`${base}/catalog/nodes`); return Array.isArray(value.nodes) ? value.nodes : [] }
