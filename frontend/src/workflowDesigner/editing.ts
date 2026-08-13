import type { Connection } from '@xyflow/react'
import type { WorkflowDefinition, WorkflowGraphEdge, WorkflowNodeType } from '../types'
import { canConnect } from './connection'
import { canDeleteNode } from './catalog'

export const newNodeId = () => `node-${crypto.randomUUID()}`

const withCandidate = (definition: WorkflowDefinition, type: WorkflowNodeType, id: string): WorkflowDefinition =>
  ({ ...definition, nodes: [...definition.nodes, { id, type: type.type, typeVersion: type.version, config: {} }] })

const edge = (source: WorkflowGraphEdge['source'], target: WorkflowGraphEdge['target']): WorkflowGraphEdge =>
  ({ id: crypto.randomUUID(), source, target })

/** 候選節點先暫時加入圖中再問 `canConnect`，讓連線相容性只有這一個判準（不另寫一套規則）。 */
function firstConnectableInput(
  definition: WorkflowDefinition, source: { nodeId: string; port: string },
  type: WorkflowNodeType, catalog: WorkflowNodeType[], id: string,
): string | null {
  const probe = withCandidate(definition, type, id)
  return type.inputs.find((port) => canConnect(
    { source: source.nodeId, sourceHandle: source.port, target: id, targetHandle: port.id }, probe, catalog,
  ))?.id ?? null
}

/** 拉線到空白處 → 新增節點並自動接線；接不上（`canConnect` 不過）時只新增節點。 */
export function addConnectedNode(
  definition: WorkflowDefinition, source: { nodeId: string; port: string },
  type: WorkflowNodeType, catalog: WorkflowNodeType[], id: string,
): { definition: WorkflowDefinition; connected: boolean } {
  const withNode = withCandidate(definition, type, id)
  const port = firstConnectableInput(definition, source, type, catalog, id)
  if (!port) return { definition: withNode, connected: false }
  return {
    definition: { ...withNode, edges: [...withNode.edges, edge(source, { nodeId: id, port })] },
    connected: true,
  }
}

/** 邊上中插節點：刪原邊 + 新增節點 + 新增兩條邊，全部過 `canConnect`；任一段接不上就整筆放棄（null）。 */
export function insertNodeOnEdge(
  definition: WorkflowDefinition, edgeId: string, type: WorkflowNodeType,
  catalog: WorkflowNodeType[], id: string,
): WorkflowDefinition | null {
  const target = definition.edges.find((item) => item.id === edgeId)
  if (!target) return null
  const detached = removeSemanticEdges(definition, [edgeId])
  const first = addConnectedNode(detached, target.source, type, catalog, id)
  if (!first.connected) return null
  const outPort = type.outputs.find((port) => canConnect(
    { source: id, sourceHandle: port.id, target: target.target.nodeId, targetHandle: target.target.port },
    first.definition, catalog,
  ))
  if (!outPort) return null
  return { ...first.definition, edges: [...first.definition.edges, edge({ nodeId: id, port: outPort.id }, target.target)] }
}

export function removeSemanticNodes(definition: WorkflowDefinition, ids: string[], catalog: WorkflowNodeType[]): WorkflowDefinition {
  const removable = new Set(ids.filter((id) => {
    const node = definition.nodes.find((item) => item.id === id)
    return !!node && canDeleteNode(
      catalog,
      definition.kind,
      node.type,
      node.typeVersion,
      definition.runtimeVariant,
    )
  }))
  return {
    ...definition,
    nodes: definition.nodes.filter((node) => !removable.has(node.id)),
    edges: definition.edges.filter((edge) => !removable.has(edge.source.nodeId) && !removable.has(edge.target.nodeId)),
  }
}

export function removeSemanticEdges(definition: WorkflowDefinition, ids: string[]): WorkflowDefinition {
  const removed = new Set(ids)
  return { ...definition, edges: definition.edges.filter((edge) => !removed.has(edge.id)) }
}

export function reconnectSemanticEdge(
  definition: WorkflowDefinition,
  edgeId: string,
  connection: Connection,
  catalog: WorkflowNodeType[],
): WorkflowDefinition {
  const withoutOld = removeSemanticEdges(definition, [edgeId])
  if (!canConnect(connection, withoutOld, catalog) || !connection.source || !connection.target || !connection.sourceHandle || !connection.targetHandle) return definition
  return { ...withoutOld, edges: [...withoutOld.edges, { id: edgeId, source: { nodeId: connection.source, port: connection.sourceHandle }, target: { nodeId: connection.target, port: connection.targetHandle } }] }
}
