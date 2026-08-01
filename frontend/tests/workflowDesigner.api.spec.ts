import { expect, test } from '@playwright/test'
import {
  createWorkflow, decodeWorkflow, decodeWorkflowSimulation, decodeWorkflowValidation, encodeWorkflowUpsert, getWorkflow,
  listWorkflowNodeCatalog, publishWorkflow, putWorkflowDraft, restoreWorkflowRevision, simulateWorkflow, validateWorkflow,
} from '../src/api/workflows'
import { createOrchestrator, decodeOrchestrator, encodeOrchestratorUpsert, getOrchestrator, putOrchestratorDraft } from '../src/api/orchestrators'
import type { OrchestratorDraft, WorkflowDefinition, WorkflowDraft } from '../src/types'

const workflowDraft: WorkflowDraft = { definition: { schemaVersion: 1, kind: 'orchestrator', nodes: [], edges: [], governance: {} }, ui_metadata: { positions: {} } }
const workflowWire = { id: 'w1', name: 'Root', kind: 'orchestrator', enabled: true, draft_version: 4, published_revision: null, definition: workflowDraft.definition, ui_metadata: workflowDraft.ui_metadata, updated_at: '2026-01-01' }
const workflowId = '11111111-1111-4111-8111-111111111111'
const workerId = '22222222-2222-4222-8222-222222222222'
const verifierId = '33333333-3333-4333-8333-333333333333'
const orchestratorDraft: OrchestratorDraft = {
  name: 'Root', description: 'd', instructions: 'i', policy: { dispatchMode: 'bounded-parallel', joinPolicy: 'allow-partial', repairPolicy: 'redispatch', aggregationPolicy: 'verified-only', denialPolicy: 'fail-closed' },
  workflow: { id: workflowId, revision: 1 }, workerPool: [{ agentId: workerId, revision: 3 }],
  workerPolicy: { requiredAudience: ['role:USER'], requiredCapabilities: ['analysis'], selection: 'pinned-only' },
  context: { readOnly: true, allowedTools: ['retrieve'], knowledgeSources: ['kb'] },
  audience: ['role:USER'], capabilities: ['analysis'],
  verifier: { agentId: verifierId, revision: 2, variant: 'read-only', outputContract: { type: 'verification-report' }, independent: true },
  budgets: { maxContextRounds: 2, maxTasks: 3, maxChildRuns: 4, maxConcurrency: 2, maxRepairRounds: 1, tokenBudget: 10000, timeoutSeconds: 300 },
}
const orchestratorDefinition = { instructions: 'i', policy: { dispatchMode: 'bounded-parallel', joinPolicy: 'allow-partial', repairPolicy: 'redispatch', aggregationPolicy: 'verified-only', denialPolicy: 'fail-closed' }, workflow: { id: workflowId, revision: 1 }, workerPool: [{ agentId: workerId, revision: 3 }], workerPolicy: { requiredAudience: ['role:USER'], requiredCapabilities: ['analysis'], selection: 'pinned-only' }, context: { readOnly: true, allowedTools: ['retrieve'], knowledgeSources: ['kb'] }, audience: ['role:USER'], capabilities: ['analysis'], verifier: { agentId: verifierId, revision: 2, variant: 'read-only', outputContract: { type: 'verification-report' }, independent: true }, budgets: { maxContextRounds: 2, maxTasks: 3, maxChildRuns: 4, maxConcurrency: 2, maxRepairRounds: 1, tokenBudget: 10000, timeoutSeconds: 300 } }
const orchestratorWire = { id: 'o1', name: 'Root', description: 'd', enabled: true, draft_version: 2, published_revision: null, definition: orchestratorDefinition }

test.describe('D4 management wire adapters', () => {
  test('workflow create/get/update/validate/simulate/publish/restore/catalog follow backend DTOs', async () => {
    const original = globalThis.fetch; const calls: Array<{ path: string; init?: RequestInit }> = []
    const responses = [workflowWire, workflowWire, workflowWire, { valid: false, errors: [{ field: 'definition', message: 'bad', node_id: 'n1' }] }, { valid: true, canonicalDefinition: workflowDraft.definition, trace: [{ nodeId: 'n1', status: 'passed' }] }, workflowWire, workflowWire, { catalogVersion: '1', nodes: [{ type: 'start', version: '1.0', title: 'Start', kind: 'control', inputs: [], outputs: [], configSchema: {}, authoringCapability: 'workflow.manage', catalogVisibility: 'system-admin', runtimePolicy: 'run.control' }] }]
    globalThis.fetch = async (input, init) => { calls.push({ path: String(input), init }); return new Response(JSON.stringify(responses.shift()), { status: 200, headers: { 'Content-Type': 'application/json', ETag: '"4"' } }) }
    try {
      await createWorkflow({ name: 'Root', kind: 'orchestrator', draft: workflowDraft }); const get = await getWorkflow('w1'); await putWorkflowDraft('w1', get.data, workflowDraft, '"4"'); const validation = await validateWorkflow('w1', '"4"'); const simulation = await simulateWorkflow('w1', '"4"'); await publishWorkflow('w1', 4, '"4"'); const restored = await restoreWorkflowRevision('w1', 1); const catalog = await listWorkflowNodeCatalog()
      // Creation has nothing to lock against, so If-Match must be omitted rather than sent empty.
      expect(calls[0].path).toBe('/api/admin/workflows'); expect(calls[0].init?.method).toBe('POST'); expect(new Headers(calls[0].init?.headers).get('If-Match')).toBeNull()
      expect(JSON.parse(String(calls[0].init?.body))).toEqual({ name: 'Root', kind: 'orchestrator', definition: workflowDraft.definition, ui_metadata: workflowDraft.ui_metadata })
      expect(calls[2].path).toBe('/api/admin/workflows/w1/draft'); expect(new Headers(calls[2].init?.headers).get('If-Match')).toBe('"4"')
      expect(JSON.parse(String(calls[2].init?.body))).toEqual({ name: 'Root', kind: 'orchestrator', definition: workflowDraft.definition, ui_metadata: workflowDraft.ui_metadata })
      expect(calls[3].path).toBe('/api/admin/workflows/w1/validate'); expect(new Headers(calls[3].init?.headers).get('If-Match')).toBe('"4"')
      expect(calls[4].path).toBe('/api/admin/workflows/w1/simulate'); expect(new Headers(calls[4].init?.headers).get('If-Match')).toBe('"4"')
      // Publish carries both guards: the expected draft version in the body and the ETag in If-Match.
      expect(calls[5].path).toBe('/api/admin/workflows/w1/publish'); expect(new Headers(calls[5].init?.headers).get('If-Match')).toBe('"4"'); expect(JSON.parse(String(calls[5].init?.body))).toEqual({ expected_draft_version: 4 })
      // Restore republishes an old revision as a new one: no If-Match, no body, decoded like any workflow.
      expect(calls[6]).toMatchObject({ path: '/api/admin/workflows/w1/revisions/1/restore', init: { method: 'POST' } }); expect(calls[6].init?.body).toBeUndefined(); expect(new Headers(calls[6].init?.headers).get('If-Match')).toBeNull()
      expect(restored).toEqual(decodeWorkflow(workflowWire)); expect(restored.draft.definition).toEqual(workflowDraft.definition)
      expect(calls[7].path).toBe('/api/admin/workflows/catalog/nodes')
      expect(validation.errors[0]).toMatchObject({ scope: 'node', id: 'n1', code: 'definition' }); expect(simulation.trace).toEqual([{ node_id: 'n1', status: 'passed', summary: undefined }]); expect(catalog).toHaveLength(1)
    } finally { globalThis.fetch = original }
  })

  test('workflow runtime variants round-trip exactly and orchestrators omit external variants', () => {
    for (const runtimeVariant of ['worker', 'verifier'] as const) {
      const definition = { ...workflowDraft.definition, kind: 'agent-runtime' as const, runtimeVariant }
      const wire = { ...workflowWire, kind: 'agent-runtime' as const, definition }
      const decoded = decodeWorkflow(wire)
      expect(decoded.draft.definition.runtimeVariant).toBe(runtimeVariant)
      expect(encodeWorkflowUpsert({ name: decoded.name, kind: decoded.kind, draft: decoded.draft })).toEqual({
        name: 'Root', kind: 'agent-runtime', definition, ui_metadata: workflowDraft.ui_metadata,
      })
    }

    const contaminated = decodeWorkflow({
      ...workflowWire,
      definition: { ...workflowDraft.definition, runtimeVariant: 'worker' },
    })
    expect(contaminated.draft.definition).not.toHaveProperty('runtimeVariant')
    expect(encodeWorkflowUpsert({ name: contaminated.name, kind: contaminated.kind, draft: contaminated.draft }))
      .not.toHaveProperty('definition.runtimeVariant')
  })

  test('agent-runtime workflows drop an unknown runtime variant exactly like an absent one', () => {
    for (const runtimeVariant of ['bogus', undefined]) {
      const definition = { ...workflowDraft.definition, kind: 'agent-runtime', runtimeVariant } as unknown as WorkflowDefinition
      const decoded = decodeWorkflow({ ...workflowWire, kind: 'agent-runtime' as const, definition })
      // Only 'worker'/'verifier' survive the decode, so an unrecognised variant can never be echoed back on save.
      expect(decoded.draft.definition).not.toHaveProperty('runtimeVariant')
      expect(encodeWorkflowUpsert({ name: decoded.name, kind: decoded.kind, draft: decoded.draft })).toEqual({
        name: 'Root', kind: 'agent-runtime', definition: { ...workflowDraft.definition, kind: 'agent-runtime' }, ui_metadata: workflowDraft.ui_metadata,
      })
    }
  })

  test('validation issues fall back to edge and graph scope when no node id is present', () => {
    const validation = decodeWorkflowValidation({ valid: false, errors: [{ field: 'edges', message: 'dangling edge', edge_id: 'e1' }, { message: 'cycle detected' }] })
    // Scope decides which canvas element lights up; a graph-level error must stay unattached instead of borrowing an id.
    expect(validation.errors).toEqual([
      { scope: 'edge', id: 'e1', code: 'edges', message: 'dangling edge' },
      { scope: 'graph', id: undefined, code: 'validation', message: 'cycle detected' },
    ])
    expect(validation.canonical_definition).toBeUndefined()
  })

  test('simulation diagnostics map camelCase ids and fall back through code/path/field', () => {
    // The Python simulator answers in camelCase, so scope/id must be read from both spellings.
    const simulation = decodeWorkflowSimulation({ valid: false, errors: [
      { edgeId: 'e1', message: 'bad edge' },
      { nodeId: 'n1', path: 'nodes[0].config', message: 'bad node' },
      { field: 'definition' },
    ] })
    expect(simulation).toEqual({
      valid: false, canonical_definition: undefined, trace: undefined,
      errors: [
        { scope: 'edge', id: 'e1', code: 'validation', message: 'bad edge' },
        { scope: 'node', id: 'n1', code: 'nodes[0].config', message: 'bad node' },
        { scope: 'graph', id: undefined, code: 'definition', message: 'validation failed' },
      ],
    })
  })

  test('node catalog degrades to an empty palette when the response carries no nodes array', async () => {
    const original = globalThis.fetch; const responses = [{ catalogVersion: '1' }, { nodes: [] }]
    globalThis.fetch = async () => new Response(JSON.stringify(responses.shift()), { status: 200, headers: { 'Content-Type': 'application/json' } })
    try {
      // A catalog without the key and an explicitly empty one are the same empty palette, never a crash.
      expect(await listWorkflowNodeCatalog()).toEqual([])
      expect(await listWorkflowNodeCatalog()).toEqual([])
    } finally { globalThis.fetch = original }
  })

  test('orchestrator valid canonical create and load-save preserve every required field exactly', async () => {
    const original = globalThis.fetch; const calls: Array<{ init?: RequestInit }> = []; const responses = [orchestratorWire, orchestratorWire, orchestratorWire]
    globalThis.fetch = async (_, init) => { calls.push({ init }); return new Response(JSON.stringify(responses.shift()), { status: 200, headers: { 'Content-Type': 'application/json', ETag: '"2"' } }) }
    try {
      await createOrchestrator(orchestratorDraft); const current = await getOrchestrator('o1'); await putOrchestratorDraft('o1', current.data.draft, '"2"')
      const body = JSON.parse(String(calls[0].init?.body)); expect(body).toEqual({ name: 'Root', description: 'd', definition: orchestratorDefinition })
      expect(current.data.draft.verifier).toEqual(orchestratorDraft.verifier)
      expect(current.data.draft).toEqual(orchestratorDraft)
      expect(JSON.parse(String(calls[2].init?.body))).toEqual(body)
    } finally { globalThis.fetch = original }
  })

  test('orchestrator decode degrades unknown policies and missing budgets to their safe defaults', () => {
    const relaxed = decodeOrchestrator({ ...orchestratorWire, definition: { ...orchestratorDefinition, policy: { joinPolicy: 'repair', repairPolicy: 'unknown' }, budgets: {} } })
    // 'repair' is a real join policy, but an unreadable repair policy degrades to 'fail' — the UI never inherits a value it could not verify.
    expect(relaxed.draft.policy).toEqual({ dispatchMode: 'bounded-parallel', joinPolicy: 'repair', repairPolicy: 'fail', aggregationPolicy: 'verified-only', denialPolicy: 'fail-closed' })
    expect(relaxed.draft.budgets).toEqual({ maxContextRounds: 0, maxTasks: 0, maxChildRuns: 0, maxConcurrency: 0, maxRepairRounds: 0, tokenBudget: 0, timeoutSeconds: 0 })
    const bare = decodeOrchestrator({ ...orchestratorWire, definition: { ...orchestratorDefinition, policy: undefined } })
    expect(bare.draft.policy.joinPolicy).toBe('fail-fast')
  })

  test('orchestrator load-save drops unknown policy fields instead of reproducing them', () => {
    const decoded = decodeOrchestrator({
      ...orchestratorWire,
      definition: {
        ...orchestratorDefinition,
        policy: { ...orchestratorDefinition.policy, unsupportedMode: 'unsafe' },
        workerPolicy: { ...orchestratorDefinition.workerPolicy, dynamicSelection: true },
        verifier: {
          ...orchestratorDefinition.verifier,
          outputContract: { type: 'verification-report', extraOutput: true },
        },
      },
    })
    expect(encodeOrchestratorUpsert(decoded.draft)).toEqual({
      name: 'Root',
      description: 'd',
      definition: orchestratorDefinition,
    })
  })
})
