import { expect, test } from 'vitest'
import { canConnect } from '../src/workflowDesigner/connection'
import { patchPositions, semanticFingerprint } from '../src/workflowDesigner/graphAdapter'
import { semanticDiff } from '../src/workflowDesigner/diff'
import { canDeleteNode, catalogForKind, isRequiredStage } from '../src/workflowDesigner/catalog'
import { reconnectSemanticEdge, removeSemanticEdges, removeSemanticNodes } from '../src/workflowDesigner/editing'
import { createBlankDraft } from '../src/workflowDesigner/draft'
import type { WorkflowDefinition, WorkflowNodeType, WorkflowUiMetadata } from '../src/types'

const catalog: WorkflowNodeType[] = [
  { type: 'start', version: '1.0', title: 'Start', kind: 'control', inputs: [], outputs: [{ id: 'out', dataType: 'control' }], configSchema: {}, authoringCapability: 'workflow.manage', catalogVisibility: 'system-admin', runtimePolicy: 'run.control', workflowKinds: ['orchestrator', 'agent-runtime'], requiredStage: true },
  { type: 'join', version: '1.0', title: 'Join', kind: 'control', inputs: [{ id: 'in', dataType: 'control', maxConnections: 1 }], outputs: [], configSchema: {}, authoringCapability: 'workflow.manage', catalogVisibility: 'system-admin', runtimePolicy: 'run.control', workflowKinds: ['orchestrator'], requiredStage: true },
  { type: 'optional', version: '1.0', title: 'Optional', kind: 'control', inputs: [{ id: 'in', dataType: 'control' }], outputs: [{ id: 'out', dataType: 'control' }], configSchema: {}, authoringCapability: 'workflow.manage', catalogVisibility: 'system-admin', runtimePolicy: 'run.control', workflowKinds: ['orchestrator'], requiredStage: false },
  { type: 'model_step', version: '1.0', title: 'Model', kind: 'control', inputs: [], outputs: [], configSchema: {}, authoringCapability: 'workflow.manage', catalogVisibility: 'system-admin', runtimePolicy: 'agent.step', workflowKinds: ['agent-runtime'], runtimeVariants: ['worker', 'verifier'], requiredStage: false },
  { type: 'load_skill', version: '1.0', title: 'Load Skill', kind: 'control', inputs: [], outputs: [], configSchema: {}, authoringCapability: 'workflow.manage', catalogVisibility: 'system-admin', runtimePolicy: 'skill.load', workflowKinds: ['agent-runtime'], runtimeVariants: ['worker'], requiredStage: false },
  { type: 'tool_policy_and_approval_gate', version: '1.0', title: 'Tool Gate', kind: 'control', inputs: [], outputs: [], configSchema: {}, authoringCapability: 'workflow.manage', catalogVisibility: 'system-admin', runtimePolicy: 'tool.gate', workflowKinds: ['agent-runtime'], runtimeVariants: ['worker'], requiredStage: false },
  { type: 'tool_call_and_observation', version: '1.0', title: 'Tool Call', kind: 'control', inputs: [], outputs: [], configSchema: {}, authoringCapability: 'workflow.manage', catalogVisibility: 'system-admin', runtimePolicy: 'tool.invoke', workflowKinds: ['agent-runtime'], runtimeVariants: ['worker'], requiredStage: false },
]
const graph: WorkflowDefinition = { schemaVersion: 1, kind: 'orchestrator', governance: {}, nodes: [
  { id: 'a', type: 'start', typeVersion: '1.0', config: {} }, { id: 'b', type: 'join', typeVersion: '1.0', config: {} },
], edges: [] }

test.describe('Workflow Designer graph boundary', () => {
  test('UI movement changes only ui_metadata, not semantic graph fingerprint', () => {
    const ui: WorkflowUiMetadata = { positions: { a: { x: 1, y: 2 } } }
    const before = semanticFingerprint(graph)
    const moved = patchPositions(ui, [{ id: 'a', position: { x: 600, y: 400 }, data: {} }])
    expect(moved.positions.a).toEqual({ x: 600, y: 400 })
    expect(semanticFingerprint(graph)).toBe(before)
  })

  test('accepts only catalog-compatible typed connections and blocks duplicates', () => {
    const connection = { source: 'a', sourceHandle: 'out', target: 'b', targetHandle: 'in' }
    expect(canConnect(connection, graph, catalog)).toBe(true)
    const withEdge = { ...graph, edges: [{ id: 'e1', source: { nodeId: 'a', port: 'out' }, target: { nodeId: 'b', port: 'in' } }] }
    expect(canConnect(connection, withEdge, catalog)).toBe(false)
    expect(canConnect({ ...connection, sourceHandle: 'missing' }, graph, catalog)).toBe(false)
  })

  test('revision diff operates on semantic nodes, not UI metadata', () => {
    expect(semanticDiff({ ...graph, nodes: [...graph.nodes, { id: 'c', type: 'join', typeVersion: '1.0', config: {} }] }, graph)).toEqual({ added: ['c'], removed: [], changed: [] })
  })

  test('palette is kind-scoped and required-stage metadata remains locked', () => {
    expect(catalogForKind(catalog, 'orchestrator').map((node) => node.type)).toEqual(['start', 'join', 'optional'])
    expect(catalogForKind(catalog, 'agent-runtime').map((node) => node.type)).toEqual(['start'])
    expect(isRequiredStage(catalog, 'join', 'orchestrator')).toBe(true)
    expect(isRequiredStage(catalog, 'join', 'agent-runtime')).toBe(false)
  })

  test('agent-runtime palette filters worker-only stages from verifier variants', () => {
    expect(catalogForKind(catalog, 'agent-runtime', 'worker').map((node) => node.type)).toEqual([
      'start', 'model_step', 'load_skill', 'tool_policy_and_approval_gate', 'tool_call_and_observation',
    ])
    expect(catalogForKind(catalog, 'agent-runtime', 'verifier').map((node) => node.type)).toEqual([
      'start', 'model_step',
    ])
  })

  test('blank agent-runtime drafts explicitly select a variant while orchestrators omit it', () => {
    expect(createBlankDraft('agent-runtime', 'worker').definition.runtimeVariant).toBe('worker')
    expect(createBlankDraft('agent-runtime', 'verifier').definition.runtimeVariant).toBe('verifier')
    expect(createBlankDraft('orchestrator', 'verifier').definition).not.toHaveProperty('runtimeVariant')
  })

  test('optional node/edge edits change semantic graph while required stages cannot be removed', () => {
    const definition: WorkflowDefinition = {
      ...graph,
      nodes: [...graph.nodes, { id: 'c', type: 'optional', typeVersion: '1.0', config: {} }],
      edges: [
        { id: 'e1', source: { nodeId: 'a', port: 'out' }, target: { nodeId: 'c', port: 'in' } },
        { id: 'e2', source: { nodeId: 'c', port: 'out' }, target: { nodeId: 'b', port: 'in' } },
      ],
    }
    const requiredKept = removeSemanticNodes(definition, ['a'], catalog)
    expect(requiredKept.nodes.some((node) => node.id === 'a')).toBe(true)
    const optionalRemoved = removeSemanticNodes(definition, ['c'], catalog)
    expect(optionalRemoved.nodes.some((node) => node.id === 'c')).toBe(false)
    expect(optionalRemoved.edges).toEqual([])
    expect(removeSemanticEdges(definition, ['e1']).edges.map((edge) => edge.id)).toEqual(['e2'])
  })

  test('semantic edge can reconnect through typed catalog ports', () => {
    const definition: WorkflowDefinition = { ...graph, nodes: [...graph.nodes, { id: 'c', type: 'optional', typeVersion: '1.0', config: {} }], edges: [{ id: 'e1', source: { nodeId: 'a', port: 'out' }, target: { nodeId: 'b', port: 'in' } }] }
    const reconnected = reconnectSemanticEdge(definition, 'e1', { source: 'a', sourceHandle: 'out', target: 'c', targetHandle: 'in' }, catalog)
    expect(reconnected.edges[0].target.nodeId).toBe('c')
  })

  test('node deletion fails closed for absent catalog, unknown versions, and missing metadata', () => {
    expect(canDeleteNode([], 'orchestrator', 'optional', '1.0')).toBe(false)
    expect(canDeleteNode(catalog, 'orchestrator', 'unknown', '1.0')).toBe(false)
    expect(canDeleteNode(catalog, 'orchestrator', 'optional', '9.9')).toBe(false)
    const missingMetadata = [{ ...catalog[2], requiredStage: undefined }]
    expect(canDeleteNode(missingMetadata, 'orchestrator', 'optional', '1.0')).toBe(false)
    expect(canDeleteNode(catalog, 'orchestrator', 'optional', '1.0')).toBe(true)
  })
})
