import { expect, test } from 'vitest'
import type { Node } from '@xyflow/react'
import { canConnect } from '../src/workflowDesigner/connection'
import { parseConfigSchema } from '../src/workflowDesigner/configSchema'
import { patchPositions, semanticFingerprint, syncCanvasEdges, syncCanvasNodes } from '../src/workflowDesigner/graphAdapter'
import { semanticDiff } from '../src/workflowDesigner/diff'
import { canDeleteNode, catalogForKind } from '../src/workflowDesigner/catalog'
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
    const moved = patchPositions(ui, [{ id: 'a', position: { x: 600, y: 400 } }])
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

  test('rejects a self-loop even when the node exposes matching in and out ports', () => {
    const withOptional: WorkflowDefinition = { ...graph, nodes: [...graph.nodes, { id: 'c', type: 'optional', typeVersion: '1.0', config: {} }] }
    expect(canConnect({ source: 'c', sourceHandle: 'out', target: 'c', targetHandle: 'in' }, withOptional, catalog)).toBe(false)
    // The very same ports connect to another node, so the rejection is the self-loop guard.
    expect(canConnect({ source: 'c', sourceHandle: 'out', target: 'b', targetHandle: 'in' }, withOptional, catalog)).toBe(true)
  })

  test('rejects connections whose source and target port dataTypes differ', () => {
    const connection = { source: 'a', sourceHandle: 'out', target: 'd', targetHandle: 'in' }
    const typedGraph: WorkflowDefinition = { ...graph, nodes: [graph.nodes[0], { id: 'd', type: 'data_sink', typeVersion: '1.0', config: {} }] }
    const dataSink = { ...catalog[2], type: 'data_sink' }
    expect(canConnect(connection, typedGraph, [catalog[0], { ...dataSink, inputs: [{ id: 'in', dataType: 'data' }] }])).toBe(false)
    expect(canConnect(connection, typedGraph, [catalog[0], { ...dataSink, inputs: [{ id: 'in', dataType: 'control' }] }])).toBe(true)
  })

  test('rejects a different source once the target input port reaches maxConnections', () => {
    const nodes = [...graph.nodes, { id: 'c', type: 'optional', typeVersion: '1.0', config: {} }]
    const connection = { source: 'c', sourceHandle: 'out', target: 'b', targetHandle: 'in' }
    expect(canConnect(connection, { ...graph, nodes }, catalog)).toBe(true)
    // 'join'.in caps at one connection and an unrelated edge already fills it, so this is
    // the maxConnections comparison, not the duplicate-edge short-circuit.
    const filled: WorkflowDefinition = { ...graph, nodes, edges: [{ id: 'e1', source: { nodeId: 'a', port: 'out' }, target: { nodeId: 'b', port: 'in' } }] }
    expect(canConnect(connection, filled, catalog)).toBe(false)
  })

  test('revision diff operates on semantic nodes, not UI metadata', () => {
    expect(semanticDiff({ ...graph, nodes: [...graph.nodes, { id: 'c', type: 'join', typeVersion: '1.0', config: {} }] }, graph)).toEqual({ added: ['c'], removed: [], changed: [] })
  })

  test('revision diff reports removed and changed semantic nodes', () => {
    const withExtra: WorkflowDefinition = { ...graph, nodes: [...graph.nodes, { id: 'c', type: 'join', typeVersion: '1.0', config: {} }] }
    expect(semanticDiff(graph, withExtra)).toEqual({ added: [], removed: ['c'], changed: [] })
    const reconfigured: WorkflowDefinition = { ...graph, nodes: [{ ...graph.nodes[0], config: { retries: 2 } }, graph.nodes[1]] }
    expect(semanticDiff(reconfigured, graph)).toEqual({ added: [], removed: [], changed: ['a'] })
    const retyped: WorkflowDefinition = { ...graph, nodes: [{ ...graph.nodes[0], type: 'optional' }, graph.nodes[1]] }
    expect(semanticDiff(retyped, graph)).toEqual({ added: [], removed: [], changed: ['a'] })
  })

  test('palette is kind-scoped and required-stage metadata remains locked', () => {
    expect(catalogForKind(catalog, 'orchestrator').map((node) => node.type)).toEqual(['start', 'join', 'optional'])
    expect(catalogForKind(catalog, 'agent-runtime').map((node) => node.type)).toEqual(['start'])
    // The gatekeeper is canDeleteNode: a required stage stays locked, and a node absent
    // from the kind's catalog fails closed rather than becoming deletable.
    expect(canDeleteNode(catalog, 'orchestrator', 'join', '1.0')).toBe(false)
    expect(canDeleteNode(catalog, 'agent-runtime', 'join', '1.0')).toBe(false)
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

  test('node removal ignores empty and unmatched id lists and removes batches together', () => {
    const definition: WorkflowDefinition = {
      ...graph,
      nodes: [...graph.nodes,
        { id: 'c', type: 'optional', typeVersion: '1.0', config: {} },
        { id: 'd', type: 'optional', typeVersion: '1.0', config: {} }],
      edges: [{ id: 'e1', source: { nodeId: 'a', port: 'out' }, target: { nodeId: 'c', port: 'in' } }],
    }
    expect(removeSemanticNodes(definition, [], catalog)).toEqual(definition)
    expect(removeSemanticNodes(definition, ['does-not-exist'], catalog)).toEqual(definition)
    expect(removeSemanticEdges(definition, []).edges.map((edge) => edge.id)).toEqual(['e1'])
    // A batch drops every deletable id at once; the required stage 'a' survives its own removal request.
    const batched = removeSemanticNodes(definition, ['a', 'c', 'd'], catalog)
    expect(batched.nodes.map((node) => node.id)).toEqual(['a', 'b'])
    expect(batched.edges).toEqual([])
  })

  test('node removal honours an agent-runtime definition kind and runtime variant', () => {
    const runtimeDefinition: WorkflowDefinition = {
      schemaVersion: 1, kind: 'agent-runtime', runtimeVariant: 'verifier', governance: {},
      nodes: [
        { id: 'a', type: 'start', typeVersion: '1.0', config: {} },
        { id: 'm', type: 'model_step', typeVersion: '1.0', config: {} },
        { id: 'l', type: 'load_skill', typeVersion: '1.0', config: {} },
      ],
      edges: [],
    }
    // verifier: start stays required, model_step is deletable, worker-only load_skill fails closed.
    expect(removeSemanticNodes(runtimeDefinition, ['a', 'm', 'l'], catalog).nodes.map((node) => node.id)).toEqual(['a', 'l'])
    expect(removeSemanticNodes({ ...runtimeDefinition, runtimeVariant: 'worker' }, ['a', 'm', 'l'], catalog).nodes.map((node) => node.id)).toEqual(['a'])
  })

  test('semantic edge can reconnect through typed catalog ports', () => {
    const definition: WorkflowDefinition = { ...graph, nodes: [...graph.nodes, { id: 'c', type: 'optional', typeVersion: '1.0', config: {} }], edges: [{ id: 'e1', source: { nodeId: 'a', port: 'out' }, target: { nodeId: 'b', port: 'in' } }] }
    const reconnected = reconnectSemanticEdge(definition, 'e1', { source: 'a', sourceHandle: 'out', target: 'c', targetHandle: 'in' }, catalog)
    expect(reconnected.edges[0].target.nodeId).toBe('c')
  })

  test('semantic edge reconnect fails closed and leaves the old edge in place', () => {
    const definition: WorkflowDefinition = {
      ...graph,
      nodes: [...graph.nodes, { id: 'c', type: 'optional', typeVersion: '1.0', config: {} }],
      edges: [
        { id: 'e1', source: { nodeId: 'a', port: 'out' }, target: { nodeId: 'c', port: 'in' } },
        { id: 'e2', source: { nodeId: 'c', port: 'out' }, target: { nodeId: 'b', port: 'in' } },
      ],
    }
    expect(reconnectSemanticEdge(definition, 'e1', { source: 'a', sourceHandle: 'out', target: 'c', targetHandle: 'missing' }, catalog)).toEqual(definition)
    expect(reconnectSemanticEdge(definition, 'e1', { source: 'a', sourceHandle: 'out', target: 'c', targetHandle: null }, catalog)).toEqual(definition)
    // Moving e2 onto e1's exact ports would duplicate a surviving edge, so the whole edit is dropped.
    expect(reconnectSemanticEdge(definition, 'e2', { source: 'a', sourceHandle: 'out', target: 'c', targetHandle: 'in' }, catalog)).toEqual(definition)
  })

  test('node deletion fails closed for absent catalog, unknown versions, and missing metadata', () => {
    expect(canDeleteNode([], 'orchestrator', 'optional', '1.0')).toBe(false)
    expect(canDeleteNode(catalog, 'orchestrator', 'unknown', '1.0')).toBe(false)
    expect(canDeleteNode(catalog, 'orchestrator', 'optional', '9.9')).toBe(false)
    const missingMetadata = [{ ...catalog[2], requiredStage: undefined }]
    expect(canDeleteNode(missingMetadata, 'orchestrator', 'optional', '1.0')).toBe(false)
    expect(canDeleteNode(catalog, 'orchestrator', 'optional', '1.0')).toBe(true)
  })

  test('node deletion pairs the workflow kind with an explicit runtime variant', () => {
    // model_step is offered to both variants; load_skill is worker-only.
    expect(canDeleteNode(catalog, 'agent-runtime', 'model_step', '1.0', 'worker')).toBe(true)
    expect(canDeleteNode(catalog, 'agent-runtime', 'model_step', '1.0', 'verifier')).toBe(true)
    expect(canDeleteNode(catalog, 'agent-runtime', 'load_skill', '1.0', 'worker')).toBe(true)
    expect(canDeleteNode(catalog, 'agent-runtime', 'load_skill', '1.0', 'verifier')).toBe(false)
    // Variant-scoped nodes stay locked when no variant is supplied; orchestrators ignore the argument.
    expect(canDeleteNode(catalog, 'agent-runtime', 'model_step', '1.0')).toBe(false)
    expect(canDeleteNode(catalog, 'orchestrator', 'optional', '1.0', 'verifier')).toBe(true)
  })
})

// props → 本地 React Flow 投影的同步規則。UI-only 狀態(選取/量測)必須跨語意提交存活,
// 否則拖曳一次就掉選取;反之語意欄位一律以新投影為準,本地不得回寫語意。
test.describe('Canvas projection sync', () => {
  const node = (id: string, extra: Record<string, unknown> = {}) =>
    ({ id, type: 'workflow', position: { x: 0, y: 0 }, data: {}, ...extra }) as Node

  test('keeps local selection and measurements for surviving nodes, drops them for new ones', () => {
    const prev = [node('a', { selected: true, measured: { width: 100, height: 40 } }), node('b')]
    const next = [node('a', { position: { x: 5, y: 6 } }), node('c')]
    const merged = syncCanvasNodes(next, prev)
    expect(merged.map((item) => [item.id, item.selected])).toEqual([['a', true], ['c', false]])
    expect(merged[0].measured).toEqual({ width: 100, height: 40 })
    expect(merged[0].position).toEqual({ x: 5, y: 6 })
    expect(merged[1].measured).toBeUndefined()
  })

  test('selectId takes exclusive selection (newly added node) over the previous selection', () => {
    const merged = syncCanvasNodes([node('a'), node('b')], [node('a', { selected: true })], 'b')
    expect(merged.map((item) => item.selected)).toEqual([false, true])
  })

  test('edge selection survives a semantic commit; removed edges do not come back', () => {
    const merged = syncCanvasEdges(
      [{ id: 'e1', source: 'a', target: 'b', label: 'out → in' }],
      [{ id: 'e1', source: 'a', target: 'b', selected: true }, { id: 'e2', source: 'a', target: 'c' }],
    )
    expect(merged).toEqual([{ id: 'e1', source: 'a', target: 'b', label: 'out → in', selected: true }])
  })
})

// P1 Phase A': configSchema → 泛型表單欄位解析。三個既有 catalog 形狀(見
// `workflow/app/orchestration/catalog.py` `_node()`)必須成功解析；任何超出
// string/integer/number/boolean 頂層屬性子集的形狀一律 fail-open 回 null。
test.describe('Node Config schema parsing (P1 Phase A\')', () => {
  test('empty object schema (the common case) yields zero fields, not null', () => {
    expect(parseConfigSchema({ type: 'object', additionalProperties: false })).toEqual([])
    expect(parseConfigSchema({})).toEqual([])
  })

  test('the real bounded_agent_loop/bounded_repair catalog shape resolves one required integer field', () => {
    const schema = {
      type: 'object', additionalProperties: false,
      required: ['maxIterations'],
      properties: { maxIterations: { type: 'integer', minimum: 1 } },
    }
    expect(parseConfigSchema(schema)).toEqual([{ key: 'maxIterations', type: 'integer', required: true, minimum: 1, maximum: undefined }])
  })

  test('the real bounded_repair_or_controlled_failure catalog shape resolves maxRepairRounds', () => {
    const schema = {
      type: 'object', additionalProperties: false,
      required: ['maxRepairRounds'],
      properties: { maxRepairRounds: { type: 'integer', minimum: 1 } },
    }
    expect(parseConfigSchema(schema)).toEqual([{ key: 'maxRepairRounds', type: 'integer', required: true, minimum: 1, maximum: undefined }])
  })

  test('an optional field without minimum/maximum omits those keys as undefined', () => {
    expect(parseConfigSchema({ type: 'object', properties: { label: { type: 'string' } } })).toEqual([
      { key: 'label', type: 'string', required: false, minimum: undefined, maximum: undefined },
    ])
  })

  test('falls back to null (raw JSON escape hatch) for shapes outside the supported subset', () => {
    const unsupported: Array<Record<string, unknown> | null | undefined | string> = [
      undefined,
      null,
      'not-an-object',
      { type: 'array' },
      { type: 'object', properties: 'nope' },
      { type: 'object', properties: { child: { type: 'object', properties: {} } } },
      { type: 'object', properties: { items: { type: 'array' } } },
      { type: 'object', properties: { mode: { type: 'string', enum: ['a', 'b'] } } },
      { type: 'object', properties: { mode: { oneOf: [{ type: 'string' }] } } },
    ]
    for (const schema of unsupported) expect(parseConfigSchema(schema as Record<string, unknown>)).toBeNull()
  })
})
