import type { Connection } from '@xyflow/react'
import type { WorkflowDefinition, WorkflowNodeType } from '../types'
import { canConnect } from './connection'
import { canDeleteNode } from './catalog'

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
